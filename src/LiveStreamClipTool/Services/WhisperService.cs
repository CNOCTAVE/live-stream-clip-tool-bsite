// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using System.Diagnostics;
using System.Text;

namespace LiveStreamClipTool.Services;

/// <summary>
/// 调用 Tools/whisper_transcribe.py，把切片语音识别成与切片同目录、同名的 .txt 文件。
/// </summary>
public sealed class WhisperService
{
    private readonly DependencyService _deps;

    public WhisperService(DependencyService deps) => _deps = deps;

    public static string TranscriptPathFor(string mediaPath) =>
        Path.ChangeExtension(mediaPath, ".txt");

    public async Task<string> TranscribeAsync(
        string mediaPath,
        IProgress<string>? stage = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(mediaPath))
        {
            throw new FileNotFoundException("切片文件不存在", mediaPath);
        }
        var python = _deps.FindPythonPath();
        if (python.Length == 0)
        {
            throw new InvalidOperationException("未检测到 Python，请先到「依赖管理」安装");
        }
        var ffmpeg = _deps.FindFfmpegPath();
        if (ffmpeg.Length == 0)
        {
            throw new InvalidOperationException("未检测到 ffmpeg，请先到「依赖管理」安装");
        }
        var script = Path.Combine(AppContext.BaseDirectory, "Tools", "whisper_transcribe.py");
        if (!File.Exists(script))
        {
            throw new InvalidOperationException("转写脚本不存在：" + script);
        }

        var outputPath = TranscriptPathFor(mediaPath);
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
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1";
        foreach (var arg in new[]
        {
            script,
            "--input", mediaPath,
            "--output", outputPath,
            "--ffmpeg", ffmpeg
        })
        {
            psi.ArgumentList.Add(arg);
        }

        var stderr = new StringBuilder();
        string? errorLine = null;

        using var proc = new Process { StartInfo = psi };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }
            lock (stderr)
            {
                stderr.AppendLine(e.Data);
                if (stderr.Length > 4000)
                {
                    stderr.Remove(0, stderr.Length - 4000);
                }
            }
        };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法启动 Python：" + ex.Message, ex);
        }
        proc.BeginErrorReadLine();

        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(cancellationToken)) is not null)
            {
                if (line.StartsWith("STAGE\t", StringComparison.Ordinal))
                {
                    stage?.Report(line[6..]);
                }
                else if (line.StartsWith("PROGRESS\t", StringComparison.Ordinal) &&
                         double.TryParse(line[9..], out var pct))
                {
                    progress?.Report(Math.Clamp(pct, 0, 100));
                }
                else if (line.StartsWith("ERROR\t", StringComparison.Ordinal))
                {
                    errorLine = line[6..];
                }
            }
            await proc.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Kill(proc);
            throw;
        }

        if (proc.ExitCode != 0)
        {
            string stderrTail;
            lock (stderr)
            {
                stderrTail = stderr.ToString().Trim();
            }
            var message = errorLine
                ?? stderrTail.Split('\n').LastOrDefault()?.Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                ? $"转写进程退出码 {proc.ExitCode}"
                : message);
        }
        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException("转写结束但未生成文字文件");
        }
        return await File.ReadAllTextAsync(outputPath, cancellationToken);
    }

    private static void Kill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程可能已自行退出
        }
    }
}
