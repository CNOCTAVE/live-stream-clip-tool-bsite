// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using System.Collections.Concurrent;
using System.Diagnostics;
using LiveStreamClipTool.Data;
using LiveStreamClipTool.Models;

namespace LiveStreamClipTool.Services;

public sealed class StartRecordResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public long RecordingId { get; init; }
}

public sealed class ImportResult
{
    public string FilePath { get; init; } = "";
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
}

public sealed class ActiveRecording
{
    public long RecordingId { get; init; }
    public long StreamerId { get; init; }
    public string RoomId { get; init; } = "";
    public string Nickname { get; init; } = "";
    public string RoomTitle { get; set; } = "";
    public string FilePath { get; init; } = "";
    public DateTime StartedAt { get; init; }
    public int AutoStopSeconds { get; init; }
    public Process? Process { get; init; }
    public CancellationTokenSource? MonitorCts { get; init; }
    public string LastError { get; set; } = "";

    public double DurationSeconds => Math.Max(0, (DateTime.Now - StartedAt).TotalSeconds);
}

/// <summary>
/// 录播管理：按主播启动 ffmpeg 直录 FLV，监控开播状态、自动停止，停止后交给自动切片。
/// </summary>
public sealed class RecordingService
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private readonly BilibiliService _bili;
    private readonly SettingsStore _settings;
    private readonly PathService _paths;
    private readonly StreamerRepository _streamers;
    private readonly RecordingRepository _recordings;
    private readonly DependencyService _deps;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<long, ActiveRecording> _active = new();
    private readonly Dictionary<string, long> _activeByRoom = new();
    private AutoClipService? _autoClip;

    public RecordingService(
        BilibiliService bili,
        SettingsStore settings,
        PathService paths,
        StreamerRepository streamers,
        RecordingRepository recordings,
        DependencyService deps)
    {
        _bili = bili;
        _settings = settings;
        _paths = paths;
        _streamers = streamers;
        _recordings = recordings;
        _deps = deps;
    }

    public event EventHandler? Changed;
    public event Action<string>? Log;

    public void SetAutoClip(AutoClipService autoClip) => _autoClip = autoClip;

    public IReadOnlyList<ActiveRecording> ActiveSnapshot()
    {
        lock (_gate)
        {
            return _active.Values.ToList();
        }
    }

    public bool IsRecording(long recordingId)
    {
        lock (_gate)
        {
            return _active.ContainsKey(recordingId);
        }
    }

    public ActiveRecording? FindByStreamer(long streamerId)
    {
        lock (_gate)
        {
            return _active.Values.FirstOrDefault(a => a.StreamerId == streamerId);
        }
    }

    public async Task<StartRecordResult> StartAsync(Streamer streamer, CancellationToken ct = default)
    {
        await _startGate.WaitAsync(ct);
        try
        {
            lock (_gate)
            {
                if (streamer.RoomId.Length > 0 && _activeByRoom.ContainsKey(streamer.RoomId))
                {
                    return new StartRecordResult { Ok = false, Message = "该主播已在录制中" };
                }
            }

            if (string.IsNullOrWhiteSpace(streamer.RoomId))
            {
                return new StartRecordResult { Ok = false, Message = "该主播没有直播间 ID，请先刷新" };
            }

            var ffmpeg = _deps.FindFfmpegPath();
            if (ffmpeg.Length == 0)
            {
                return new StartRecordResult { Ok = false, Message = "未找到 ffmpeg，请先到「依赖管理」安装" };
            }

            RoomInfo info;
            try
            {
                info = await _bili.GetRoomInfoAsync(streamer.RoomId, ct);
            }
            catch (Exception ex)
            {
                return new StartRecordResult { Ok = false, Message = "查询直播间失败：" + ex.Message };
            }
            if (!info.Living)
            {
                return new StartRecordResult { Ok = false, Message = "主播当前未开播，无法开始录制" };
            }

            string url;
            try
            {
                url = await _bili.GetLiveStreamUrlAsync(streamer.RoomId, _settings.Current.RecordQn, ct);
            }
            catch (Exception ex)
            {
                return new StartRecordResult { Ok = false, Message = "获取直播流失败：" + ex.Message };
            }

            var dir = _paths.StreamerRecordingDir(streamer.Nickname);
            Directory.CreateDirectory(dir);
            var file = BuildRecordingPath(dir, streamer.RoomId);
            var now = DateTime.Now;
            var autoStopSeconds = Math.Max(0, _settings.Current.AutoStopMinutes) * 60;

            var recording = new Recording
            {
                StreamerId = streamer.Id,
                FilePath = file,
                StartedAt = now,
                Status = RecordingStatus.Recording,
                AutoStopSeconds = autoStopSeconds
            };
            var recordingId = _recordings.Insert(recording);
            if (info.Title.Length > 0 && info.Title != streamer.RoomTitle)
            {
                _streamers.UpdateRoomTitle(streamer.Id, info.Title);
                streamer.RoomTitle = info.Title;
            }

            Process? proc = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                void Add(params string[] args)
                {
                    foreach (var a in args)
                    {
                        psi.ArgumentList.Add(a);
                    }
                }
                Add("-hide_banner", "-loglevel", "error",
                    "-fflags", "+genpts+discardcorrupt",
                    "-reconnect", "1", "-reconnect_streamed", "1",
                    "-reconnect_delay_max", "5");
                Add("-headers", $"Referer: https://live.bilibili.com/\r\nUser-Agent: {UserAgent}\r\n");
                Add("-i", url, "-c", "copy", "-f", "flv", "-y", file);

                proc = new Process { StartInfo = psi };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        lock (_gate)
                        {
                            if (_active.TryGetValue(recordingId, out var a))
                            {
                                a.LastError = e.Data.Trim();
                            }
                            else
                            {
                                _pendingErrors[recordingId] = e.Data.Trim();
                            }
                        }
                    }
                };
                proc.Start();
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _recordings.MarkEnded(recordingId, RecordingStatus.Error,
                    "启动 ffmpeg 失败：" + ex.Message, DateTime.Now, 0);
                return new StartRecordResult { Ok = false, Message = "启动 ffmpeg 失败：" + ex.Message };
            }

            var active = new ActiveRecording
            {
                RecordingId = recordingId,
                StreamerId = streamer.Id,
                RoomId = streamer.RoomId,
                Nickname = string.IsNullOrWhiteSpace(streamer.Nickname) ? streamer.RoomId : streamer.Nickname,
                RoomTitle = info.Title,
                FilePath = file,
                StartedAt = now,
                AutoStopSeconds = autoStopSeconds,
                Process = proc,
                MonitorCts = new CancellationTokenSource()
            };
            lock (_gate)
            {
                _active[recordingId] = active;
                _activeByRoom[streamer.RoomId] = recordingId;
                if (_pendingErrors.TryRemove(recordingId, out var err))
                {
                    active.LastError = err;
                }
            }

            Log?.Invoke($"[{active.Nickname}] 开始录制 → {Path.GetFileName(file)}");
            Changed?.Invoke(this, EventArgs.Empty);
            _ = MonitorAsync(active);
            return new StartRecordResult { Ok = true, Message = "已开始录制", RecordingId = recordingId };
        }
        finally
        {
            _startGate.Release();
        }
    }

    private readonly ConcurrentDictionary<long, string> _pendingErrors = new();

    public async Task StopAsync(long recordingId, string reason = "手动停止", bool autoStop = false)
    {
        ActiveRecording? active;
        lock (_gate)
        {
            if (!_active.TryGetValue(recordingId, out active))
            {
                return;
            }
        }
        await StopInternalAsync(active, reason, autoStop, processExited: false);
    }

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".flv", ".mkv", ".mov", ".avi", ".ts", ".webm", ".m4v", ".wmv"
    };

    /// <summary>把用户选择的本地视频登记成录播记录，并按设置加入自动切片队列。</summary>
    public async Task<List<ImportResult>> ImportLocalAsync(IReadOnlyList<string> files)
    {
        var results = new List<ImportResult>();
        var local = _streamers.EnsureLocal();
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                results.Add(new ImportResult { FilePath = file, Message = "文件不存在" });
                continue;
            }
            if (!VideoExtensions.Contains(Path.GetExtension(file)))
            {
                results.Add(new ImportResult { FilePath = file, Message = "不支持的视频格式" });
                continue;
            }
            if (_recordings.FilePathExists(file))
            {
                results.Add(new ImportResult { FilePath = file, Message = "该视频已在录播列表中" });
                continue;
            }

            try
            {
                var duration = await ProbeDurationAsync(file);
                var started = File.GetLastWriteTime(file);
                var recordingId = _recordings.Insert(new Recording
                {
                    StreamerId = local.Id,
                    FilePath = file,
                    StartedAt = started,
                    EndedAt = started,
                    Status = RecordingStatus.Stopped,
                    StopReason = "本地导入",
                    DurationSeconds = duration
                });

                var queued = false;
                if (_settings.Current.AutoClipEnabled && new FileInfo(file).Length >= 64 * 1024)
                {
                    _autoClip?.Queue(recordingId);
                    queued = true;
                }
                results.Add(new ImportResult
                {
                    FilePath = file,
                    Ok = true,
                    Message = queued ? "已导入并加入自动切片队列" : "已导入"
                });
            }
            catch (Exception ex)
            {
                results.Add(new ImportResult { FilePath = file, Message = ex.Message });
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return results;
    }

    private async Task<double> ProbeDurationAsync(string file)
    {
        var ffmpeg = _deps.FindFfmpegPath();
        if (ffmpeg.Length == 0)
        {
            return 0;
        }
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in new[] { "-hide_banner", "-i", file })
        {
            psi.ArgumentList.Add(a);
        }
        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return 0;
        }
        var errTask = proc.StandardError.ReadToEndAsync();
        _ = proc.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(15000);
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }
            return 0;
        }
        var err = await errTask;
        var m = System.Text.RegularExpressions.Regex.Match(
            err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (!m.Success)
        {
            return 0;
        }
        var h = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var min = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var sec = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        return h * 3600 + min * 60 + sec;
    }

    public async Task StopAllAsync(string reason = "手动停止")
    {
        List<ActiveRecording> list;
        lock (_gate)
        {
            list = _active.Values.ToList();
        }
        foreach (var active in list)
        {
            await StopInternalAsync(active, reason, autoStop: false, processExited: false);
        }
    }

    /// <summary>关闭窗口时调用：只把 ffmpeg 收尾，数据库保留「录制中」，下次启动按异常退出恢复并自动切片。</summary>
    public void ShutdownSync()
    {
        List<ActiveRecording> list;
        lock (_gate)
        {
            list = _active.Values.ToList();
            _active.Clear();
            _activeByRoom.Clear();
        }
        foreach (var active in list)
        {
            active.MonitorCts?.Cancel();
            try
            {
                if (active.Process is { HasExited: false })
                {
                    try
                    {
                        active.Process.StandardInput.Write("q");
                        active.Process.StandardInput.Flush();
                        active.Process.StandardInput.Close();
                    }
                    catch
                    {
                        // ignore
                    }
                    if (!active.Process.WaitForExit(3000))
                    {
                        active.Process.Kill(entireProcessTree: true);
                    }
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>启动时恢复上次异常退出遗留的录制记录，并补跑自动切片。</summary>
    public void RecoverStaleRecordings()
    {
        foreach (var rec in _recordings.GetUnfinished())
        {
            var reason = File.Exists(rec.FilePath) ? "程序异常退出（已恢复）" : "程序异常退出，且录制文件不存在";
            var status = File.Exists(rec.FilePath) ? RecordingStatus.Stopped : RecordingStatus.Error;
            _recordings.MarkEnded(rec.Id, status, reason, DateTime.Now, rec.DurationSeconds);
            if (status == RecordingStatus.Stopped)
            {
                _autoClip?.Queue(rec.Id);
            }
        }
    }

    private async Task MonitorAsync(ActiveRecording active)
    {
        var ct = active.MonitorCts?.Token ?? CancellationToken.None;
        var lastStatusCheck = DateTime.MinValue;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(5000, ct);
                lock (_gate)
                {
                    if (!_active.ContainsKey(active.RecordingId))
                    {
                        return;
                    }
                }

                if (active.Process is { HasExited: true })
                {
                    var reason = await TryDetectExitReasonAsync(active, ct);
                    await StopInternalAsync(active, reason, autoStop: false, processExited: true);
                    return;
                }

                if (active.AutoStopSeconds > 0 && active.DurationSeconds >= active.AutoStopSeconds)
                {
                    await StopInternalAsync(active, "自动停止（达到录制时长）", autoStop: true, processExited: false);
                    return;
                }

                if ((DateTime.Now - lastStatusCheck).TotalSeconds >= 30)
                {
                    lastStatusCheck = DateTime.Now;
                    try
                    {
                        var info = await _bili.GetRoomInfoAsync(active.RoomId, ct);
                        if (!info.Living)
                        {
                            await StopInternalAsync(active, "主播下播", autoStop: false, processExited: false);
                            return;
                        }
                        if (info.Title.Length > 0 && info.Title != active.RoomTitle)
                        {
                            active.RoomTitle = info.Title;
                            _streamers.UpdateRoomTitle(active.StreamerId, info.Title);
                            Changed?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    catch
                    {
                        // 状态接口抖动不影响录制
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[{active.Nickname}] 监控异常：{ex.Message}");
        }
    }

    private async Task<string> TryDetectExitReasonAsync(ActiveRecording active, CancellationToken ct)
    {
        try
        {
            var info = await _bili.GetRoomInfoAsync(active.RoomId, ct);
            return info.Living ? "流中断" : "主播下播";
        }
        catch
        {
            return "流中断或主播下播";
        }
    }

    private async Task StopInternalAsync(
        ActiveRecording active, string reason, bool autoStop, bool processExited)
    {
        lock (_gate)
        {
            if (!_active.Remove(active.RecordingId, out var removed))
            {
                return;
            }
            _activeByRoom.Remove(removed.RoomId);
        }

        try
        {
            active.MonitorCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        if (!processExited && active.Process is { HasExited: false })
        {
            try
            {
                active.Process.StandardInput.Write("q");
                active.Process.StandardInput.Flush();
                active.Process.StandardInput.Close();
            }
            catch
            {
                // ignore
            }
            try
            {
                using var timeout = new CancellationTokenSource(8000);
                await active.Process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                try
                {
                    active.Process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }
            }
        }

        var endedAt = DateTime.Now;
        var duration = Math.Max(0, (endedAt - active.StartedAt).TotalSeconds);
        var status = autoStop
            ? RecordingStatus.AutoStopped
            : reason.Contains("异常", StringComparison.Ordinal) || reason.Contains("失败", StringComparison.Ordinal)
                ? RecordingStatus.Error
                : RecordingStatus.Stopped;
        _recordings.MarkEnded(active.RecordingId, status, reason, endedAt, duration);
        Log?.Invoke($"[{active.Nickname}] 录制停止（{reason}），时长 {FormatDuration(duration)}");
        Changed?.Invoke(this, EventArgs.Empty);

        if (_settings.Current.AutoClipEnabled && File.Exists(active.FilePath))
        {
            long size;
            try
            {
                size = new FileInfo(active.FilePath).Length;
            }
            catch
            {
                size = 0;
            }
            if (size >= 64 * 1024)
            {
                _autoClip?.Queue(active.RecordingId);
            }
            else
            {
                Log?.Invoke($"[{active.Nickname}] 录制文件过小（{size} 字节），跳过自动切片");
            }
        }
    }

    private static string BuildRecordingPath(string dir, string roomId)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var baseName = $"录制-{roomId}-{stamp}";
        var path = Path.Combine(dir, baseName + ".flv");
        var i = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(dir, $"{baseName}_{i}.flv");
            i++;
        }
        return path;
    }

    public static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }
}
