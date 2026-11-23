// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LiveStreamClipTool.Data;
using LiveStreamClipTool.Models;

namespace LiveStreamClipTool.Services;

/// <summary>
/// 录制停止后的自动切片：任务持久化在 SQLite 的 clip_queue 表里，
/// 后台 worker 依次取出，调用移植自原项目 auto_segments 的检测脚本找出内容片段，
/// 再用 ffmpeg 逐段切出 mp4，写入 clips 表。
/// 开始前会检查 CPU 总占用与最高单核占用，过高时等待，避免和录制/观看抢占资源。
/// </summary>
public sealed class AutoClipService
{
    private readonly SettingsStore _settings;
    private readonly PathService _paths;
    private readonly RecordingRepository _recordings;
    private readonly ClipRepository _clips;
    private readonly ClipQueueRepository _queue;
    private readonly DependencyService _deps;
    private readonly WhisperService _whisper;
    private int _running;
    private long _runningClipId = -1;

    public AutoClipService(
        SettingsStore settings,
        PathService paths,
        RecordingRepository recordings,
        ClipRepository clips,
        ClipQueueRepository queue,
        DependencyService deps,
        WhisperService whisper)
    {
        _settings = settings;
        _paths = paths;
        _recordings = recordings;
        _clips = clips;
        _queue = queue;
        _deps = deps;
        _whisper = whisper;
    }

    public event EventHandler? Changed;
    public event Action<string>? Log;
    public event Action<double, string>? ProgressChanged;

    public bool IsBusy => Volatile.Read(ref _running) == 1;

    public long? RunningClipId
    {
        get
        {
            var value = Interlocked.Read(ref _runningClipId);
            return value > 0 ? value : null;
        }
    }

    /// <summary>录播停止时调用：写入数据库队列并唤醒后台 worker。</summary>
    public void Queue(long recordingId)
    {
        _queue.EnqueueSlice(recordingId);
        Wake();
    }

    public (string Status, string LastError)? GetTranscribeTask(long clipId)
    {
        var task = _queue.FindByClip(clipId);
        return task is null ? null : (task.Value.Status, task.Value.LastError);
    }

    /// <summary>把切片加入识别队列；force 会清掉已结束的旧任务再重新入队。</summary>
    public bool QueueTranscribe(long clipId, bool force = false)
    {
        if (force)
        {
            var task = _queue.FindByClip(clipId);
            if (task is not null)
            {
                if (task.Value.Status == "running")
                {
                    return false;
                }
                _queue.DeleteByClip(clipId);
            }
        }
        var added = _queue.EnqueueTranscribe(clipId);
        if (added)
        {
            Wake();
        }
        return added;
    }

    /// <summary>应用启动时调用：把上次异常退出遗留的 running 任务改回 pending，并接着处理。</summary>
    public void ResumePending()
    {
        _queue.ResetRunning();
        Wake();
    }

    public int PendingCount() => _queue.CountPending();

    /// <summary>清空等待中的任务；正在执行的任务会继续完成，不会被中断。</summary>
    public int ClearQueue()
    {
        var cleared = _queue.ClearPending();
        Changed?.Invoke(this, EventArgs.Empty);
        return cleared;
    }

    private void Wake()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
        {
            _ = Task.Run(ProcessQueueAsync);
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (true)
            {
                // 先等资源再取任务：等待 CPU 期间用户可能清空队列，
                // 这样不会出现「已清空却仍执行一条」的情况。
                if (!_queue.HasPending())
                {
                    break;
                }
                await WaitForCpuAsync();
                var task = _queue.GetNextPending();
                if (task is null)
                {
                    break;
                }
                try
                {
                    _queue.MarkRunning(task.Id);
                    if (task.TaskType == "transcribe" && task.ClipId is long clipId)
                    {
                        await ProcessTranscribeAsync(clipId);
                    }
                    else if (task.TaskType == "slice" && task.RecordingId is long recordingId)
                    {
                        await ProcessOneAsync(recordingId);
                    }
                    else
                    {
                        throw new InvalidOperationException("队列任务缺少目标");
                    }
                    _queue.MarkDone(task.Id);
                }
                catch (Exception ex)
                {
                    _queue.MarkError(task.Id, ex.Message);
                    Log?.Invoke($"自动任务失败：{ex.Message}");
                }
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            Changed?.Invoke(this, EventArgs.Empty);
            // 收尾瞬间可能又有新任务入队（Queue 看到 running=1 不会启动新 worker）
            if (_queue.HasPending())
            {
                Wake();
            }
        }
    }

    private async Task WaitForCpuAsync()
    {
        var announced = false;
        while (true)
        {
            var s = _settings.Current;
            if (s.CpuTotalThreshold <= 0 && s.CpuCoreThreshold <= 0)
            {
                return;
            }
            var usage = await CpuSampler.SampleAsync();
            if (usage.TotalPercent < 0)
            {
                return; // 采样失败时不阻塞切片
            }
            var totalOver = s.CpuTotalThreshold > 0 &&
                            usage.TotalPercent >= s.CpuTotalThreshold;
            var coreOver = s.CpuCoreThreshold > 0 &&
                           usage.MaxCorePercent >= s.CpuCoreThreshold;
            if (!totalOver && !coreOver)
            {
                return;
            }
            if (!announced)
            {
                announced = true;
                Log?.Invoke(
                    $"CPU 占用较高（总 {usage.TotalPercent:0}% / 最高单核 {usage.MaxCorePercent:0}%），暂停切片等待资源空闲");
                ProgressChanged?.Invoke(0, "等待 CPU 空闲");
            }
            await Task.Delay(TimeSpan.FromSeconds(8));
        }
    }

    private async Task ProcessOneAsync(long recordingId)
    {
        var recording = _recordings.GetById(recordingId)
            ?? throw new InvalidOperationException("录播记录不存在");

        if (!File.Exists(recording.FilePath))
        {
            throw new InvalidOperationException($"录播文件不存在（{recording.FilePath}）");
        }

        var ffmpeg = _deps.FindFfmpegPath();
        var python = _deps.FindPythonPath();
        if (ffmpeg.Length == 0 || python.Length == 0)
        {
            throw new InvalidOperationException("缺少 ffmpeg 或 python，请先到「依赖管理」安装");
        }

        var script = Path.Combine(AppContext.BaseDirectory, "Tools", "slice_detect.py");
        if (!File.Exists(script))
        {
            throw new InvalidOperationException($"检测脚本不存在（{script}）");
        }

        _paths.EnsureBaseDirs();
        var nickname = string.IsNullOrWhiteSpace(recording.StreamerNickname)
            ? recording.RoomId
            : recording.StreamerNickname;
        var jsonPath = Path.Combine(_paths.TempDir,
            $"segments_{recording.Id}_{DateTime.Now:yyyyMMddHHmmss}.json");

        var settings = _settings.Current;
        Log?.Invoke($"[{nickname}] 开始自动切片分析：{Path.GetFileName(recording.FilePath)}");
        ProgressChanged?.Invoke(0, "正在分析音频");

        var psi = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in new[]
        {
            script,
            "--input", recording.FilePath,
            "--output", jsonPath,
            "--ffmpeg", ffmpeg,
            "--min-seg", settings.MinSegmentSeconds.ToString(),
            "--max-seg", settings.MaxSegmentSeconds.ToString(),
            "--min-gap", settings.MergeGapSeconds.ToString(),
            "--snap", "90"
        })
        {
            psi.ArgumentList.Add(a);
        }

        var detectOk = false;
        using (var proc = new Process { StartInfo = psi })
        {
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    Log?.Invoke("检测：" + e.Data.Trim());
                }
            };
            proc.Start();
            proc.BeginErrorReadLine();

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) is not null)
            {
                if (line.StartsWith("PROGRESS\t", StringComparison.Ordinal))
                {
                    var parts = line.Split('\t');
                    if (parts.Length >= 3 &&
                        double.TryParse(parts[1], out var pct))
                    {
                        ProgressChanged?.Invoke(pct * 0.6, parts[2]);
                    }
                }
                else if (line.StartsWith("INFO\t", StringComparison.Ordinal))
                {
                    Log?.Invoke(line[5..]);
                }
            }
            await proc.WaitForExitAsync();
            detectOk = proc.ExitCode == 0 && File.Exists(jsonPath);
            if (!detectOk)
            {
                throw new InvalidOperationException($"自动切片分析失败（退出码 {proc.ExitCode}）");
            }
        }

        List<(double Start, double End)> segments;
        try
        {
            segments = await ReadSegmentsAsync(jsonPath);
        }
        finally
        {
            try
            {
                File.Delete(jsonPath);
            }
            catch
            {
                // ignore
            }
        }

        if (segments.Count == 0)
        {
            Log?.Invoke($"[{nickname}] 未检测到有效片段，未生成切片");
            ProgressChanged?.Invoke(100, "未检测到有效片段");
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var clipDir = _paths.StreamerClipDir(nickname);
        Directory.CreateDirectory(clipDir);
        var baseName = PathService.SanitizePart(Path.GetFileNameWithoutExtension(recording.FilePath));
        var clipTitleBase = recording.StreamerUid == StreamerRepository.LocalUid
            ? Path.GetFileNameWithoutExtension(recording.FilePath)
            : string.IsNullOrWhiteSpace(recording.RoomTitle)
                ? nickname
                : recording.RoomTitle;
        var made = 0;

        for (var i = 0; i < segments.Count; i++)
        {
            var (start, end) = segments[i];
            var duration = Math.Max(0.5, end - start);
            var fileName = $"{baseName}_第{i + 1:00}段_{FmtClock(start)}~{FmtClock(end)}.mp4";
            var outPath = UniquePath(Path.Combine(clipDir, fileName));
            ProgressChanged?.Invoke(60 + 40.0 * i / segments.Count,
                $"正在切第 {i + 1}/{segments.Count} 段");

            var ok = await CutAsync(ffmpeg, recording.FilePath, outPath, start, duration);
            if (!ok)
            {
                Log?.Invoke($"[{nickname}] 第 {i + 1} 段切片失败，已跳过");
                continue;
            }

            var clip = new Clip
            {
                RecordingId = recording.Id,
                StreamerId = recording.StreamerId,
                Title = $"{clipTitleBase} 第{i + 1}段",
                FilePath = outPath,
                ClipTime = recording.StartedAt.AddSeconds(start),
                DurationSeconds = duration
            };
            clip.Id = _clips.Insert(clip);
            made++;
            _queue.EnqueueTranscribe(clip.Id);
            Log?.Invoke($"[{nickname}] 已生成切片：{Path.GetFileName(outPath)}（{RecordingService.FormatDuration(duration)}），已加入语音识别队列");
            Changed?.Invoke(this, EventArgs.Empty);
        }

        ProgressChanged?.Invoke(100, $"完成，共 {made} 段");
        Log?.Invoke($"[{nickname}] 自动切片完成，共生成 {made} 段");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<List<(double Start, double End)>> ReadSegmentsAsync(string path)
    {
        var text = await File.ReadAllTextAsync(path);
        using var doc = JsonDocument.Parse(text);
        var list = new List<(double, double)>();
        if (!doc.RootElement.TryGetProperty("segments", out var segs) ||
            segs.ValueKind != JsonValueKind.Array)
        {
            return list;
        }
        foreach (var seg in segs.EnumerateArray())
        {
            if (seg.TryGetProperty("start", out var s) &&
                seg.TryGetProperty("end", out var e) &&
                s.ValueKind == JsonValueKind.Number &&
                e.ValueKind == JsonValueKind.Number)
            {
                var start = s.GetDouble();
                var end = e.GetDouble();
                if (end > start + 1)
                {
                    list.Add((start, end));
                }
            }
        }
        return list;
    }

    private async Task<bool> CutAsync(
        string ffmpeg, string input, string output, double start, double duration)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-ss", start.ToString("0.###"),
            "-i", input,
            "-t", duration.ToString("0.###"),
            "-map", "0:v:0?",
            "-map", "0:a:0?",
            "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "21",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000",
            "-sn", "-dn",
            "-movflags", "+faststart",
            "-y", output
        })
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var errTask = proc.StandardError.ReadToEndAsync();
        _ = proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var err = await errTask;
        if (proc.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(err))
            {
                Log?.Invoke("ffmpeg：" + err.Trim().Split('\n')[^1].Trim());
            }
            return false;
        }
        return File.Exists(output) && new FileInfo(output).Length > 1024;
    }

    private async Task ProcessTranscribeAsync(long clipId)
    {
        var clip = _clips.GetById(clipId)
            ?? throw new InvalidOperationException("切片记录不存在");
        var sourcePath = clip.FilePath;
        if (!File.Exists(sourcePath))
        {
            throw new InvalidOperationException($"切片文件不存在（{sourcePath}）");
        }

        Interlocked.Exchange(ref _runningClipId, clipId);
        try
        {
            var fileName = Path.GetFileName(sourcePath);
            Log?.Invoke($"开始语音转文字：{fileName}");
            ProgressChanged?.Invoke(0, "正在识别语音");

            var stage = new Progress<string>(message => ProgressChanged?.Invoke(-1, message));
            var progress = new Progress<double>(percent => ProgressChanged?.Invoke(percent, "正在识别语音"));
            var text = await _whisper.TranscribeAsync(sourcePath, stage, progress);

            var transcriptPath = WhisperService.TranscriptPathFor(sourcePath);
            var latest = _clips.GetById(clipId);
            if (latest is null)
            {
                TryDeleteFile(transcriptPath);
                Log?.Invoke($"切片已删除，已清理文字文件：{Path.GetFileName(transcriptPath)}");
                return;
            }
            if (!string.Equals(latest.FilePath, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                var movedPath = WhisperService.TranscriptPathFor(latest.FilePath);
                try
                {
                    File.Move(transcriptPath, movedPath, overwrite: true);
                    transcriptPath = movedPath;
                    Log?.Invoke("识别期间切片已改名，文字文件已跟随改名");
                }
                catch (Exception ex)
                {
                    Log?.Invoke("文字文件跟随切片改名失败：" + ex.Message);
                }
            }

            if (text.Length == 0)
            {
                ProgressChanged?.Invoke(100, "未识别到语音");
                Log?.Invoke($"未识别到语音：{Path.GetFileName(transcriptPath)}");
            }
            else
            {
                ProgressChanged?.Invoke(100, "语音转文字完成");
                Log?.Invoke($"语音转文字完成：{Path.GetFileName(transcriptPath)}（{text.Length} 字）");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _runningClipId, -1);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 文件可能被占用或已删除
        }
    }

    private static string FmtClock(double seconds)
    {
        var t = (int)Math.Round(Math.Max(0, seconds));
        return $"{t / 3600:00}-{t % 3600 / 60:00}-{t % 60:00}";
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
        return path;
    }
}
