// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace LiveStreamClipTool.Services;

public sealed class DependencyInfo
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string ChocoPackage { get; init; } = "";
    public string InstallHint { get; init; } = "";
    public bool Installed { get; set; }
    public string Version { get; set; } = "";
    public string ToolPath { get; set; } = "";
}

/// <summary>
/// 检测 ffmpeg / python / chocolatey，并通过 chocolatey 安装缺失依赖。
/// choco 安装需要管理员权限，会触发 UAC；输出写入临时日志后再回读。
/// </summary>
public sealed class DependencyService
{
    private const string ChocoExe = @"C:\ProgramData\chocolatey\bin\choco.exe";

    private readonly SettingsStore _settings;

    public DependencyService(SettingsStore settings) => _settings = settings;

    public async Task<List<DependencyInfo>> DetectAllAsync()
    {
        return await Task.Run(() =>
        {
            var list = new List<DependencyInfo>
            {
                DetectFfmpeg(),
                DetectPython(),
                DetectWhisper(),
                DetectChoco()
            };
            return list;
        });
    }

    public string FindFfmpegPath() => DetectFfmpeg().ToolPath;

    public string FindPythonPath() => DetectPython().ToolPath;

    private DependencyInfo DetectFfmpeg()
    {
        var info = new DependencyInfo
        {
            Key = "ffmpeg",
            Name = "FFmpeg",
            ChocoPackage = "ffmpeg"
        };
        var candidates = new List<string>();
        if (File.Exists(_settings.Current.FfmpegPath))
        {
            candidates.Add(_settings.Current.FfmpegPath);
        }
        candidates.AddRange(FindInPath("ffmpeg.exe"));
        candidates.Add(@"C:\ProgramData\chocolatey\bin\ffmpeg.exe");
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WinGet\Links\ffmpeg.exe"));
        candidates.Add(@"C:\ffmpeg\bin\ffmpeg.exe");

        var exe = candidates.FirstOrDefault(File.Exists) ?? "";
        if (exe.Length == 0)
        {
            return info;
        }
        info.Installed = true;
        info.ToolPath = exe;
        var (code, output) = RunCapture(exe, "-version", 6000);
        if (code == 0 && output.Length > 0)
        {
            info.Version = output.Split('\n')[0].Trim();
        }
        return info;
    }

    private DependencyInfo DetectPython()
    {
        var info = new DependencyInfo
        {
            Key = "python",
            Name = "Python",
            ChocoPackage = "python"
        };
        var candidates = new List<string>();
        if (File.Exists(_settings.Current.PythonPath))
        {
            candidates.Add(_settings.Current.PythonPath);
        }

        try
        {
            foreach (var dir in Directory.GetDirectories(@"C:\", "Python*"))
            {
                var p = Path.Combine(dir, "python.exe");
                if (File.Exists(p))
                {
                    candidates.Add(p);
                }
            }
        }
        catch
        {
            // 无权限扫盘时跳过
        }

        var localPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Programs\Python");
        try
        {
            if (Directory.Exists(localPrograms))
            {
                foreach (var dir in Directory.GetDirectories(localPrograms, "Python*"))
                {
                    var p = Path.Combine(dir, "python.exe");
                    if (File.Exists(p))
                    {
                        candidates.Add(p);
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WindowsApps\python.exe"));
        candidates.AddRange(FindInPath("python.exe"));

        // 优先真实安装目录，WindowsApps 里的 store 别名放最后
        var ordered = candidates
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ToList();

        foreach (var exe in ordered)
        {
            var (code, output) = RunCapture(exe, "--version", 6000);
            if (code == 0)
            {
                info.Installed = true;
                info.ToolPath = exe;
                info.Version = output.Trim().Split('\n')[0].Trim();
                return info;
            }
        }
        return info;
    }

    private DependencyInfo DetectWhisper()
    {
        var info = new DependencyInfo
        {
            Key = "whisper",
            Name = "Whisper 语音识别",
            ChocoPackage = "faster-whisper",
            InstallHint = "未检测到，将通过 pip 安装 faster-whisper（安装到当前用户）"
        };
        var python = DetectPython();
        if (!python.Installed)
        {
            info.Version = "需要先安装 Python";
            return info;
        }
        var (code, output) = RunCapture(python.ToolPath,
            "-c \"import faster_whisper,sys;sys.stdout.write(faster_whisper.__version__)\"", 30000);
        if (code == 0 && output.Length > 0)
        {
            info.Installed = true;
            info.Version = "faster-whisper " + output.Trim();
            info.ToolPath = python.ToolPath;
        }
        return info;
    }

    private DependencyInfo DetectChoco()
    {        var info = new DependencyInfo
        {
            Key = "choco",
            Name = "Chocolatey",
            ChocoPackage = ""
        };
        var exe = File.Exists(ChocoExe)
            ? ChocoExe
            : FindInPath("choco.exe").FirstOrDefault() ?? "";
        if (exe.Length == 0)
        {
            return info;
        }
        var (code, output) = RunCapture(exe, "--version", 8000);
        if (code == 0)
        {
            info.Installed = true;
            info.ToolPath = exe;
            info.Version = output.Trim().Split('\n')[0].Trim();
        }
        return info;
    }

    public async Task<(bool Ok, string Message)> InstallAsync(
        string key, IProgress<string>? progress = null)
    {
        if (key == "whisper")
        {
            return await InstallWhisperAsync(progress);
        }

        progress?.Report("正在检测 Chocolatey…");
        var choco = DetectChoco();
        if (!choco.Installed)
        {
            progress?.Report("未检测到 Chocolatey，开始静默安装（需要管理员授权）…");
            var (okChoco, msgChoco) = await InstallChocolateyAsync(progress);
            if (!okChoco)
            {
                return (false, msgChoco);
            }
            choco = DetectChoco();
            if (!choco.Installed)
            {
                return (false, "Chocolatey 安装后仍未检测到，请重启终端或手动检查安装");
            }
            progress?.Report("Chocolatey 安装完成。");
        }

        if (key == "choco")
        {
            return (true, "Chocolatey 已安装");
        }

        var package = key switch
        {
            "ffmpeg" => "ffmpeg",
            "python" => "python",
            _ => ""
        };
        if (package.Length == 0)
        {
            return (false, "未知的依赖项");
        }

        progress?.Report($"开始安装 {package}（choco install {package} -y）…");
        var logPath = Path.Combine(Path.GetTempPath(),
            $"lsct_{package}_{DateTime.Now:yyyyMMddHHmmss}.log");
        var inner = $"""
            $ErrorActionPreference = 'Continue'
            & '{ChocoExe}' install {package} -y --no-progress --limit-output *> '{logPath}'
            """;
        var (elevated, message) = await RunElevatedPowerShellAsync(inner);
        var log = ReadLog(logPath);
        if (!elevated)
        {
            return (false, message + (log.Length > 0 ? "\n" + Tail(log) : ""));
        }

        var detected = key == "ffmpeg" ? DetectFfmpeg() : DetectPython();
        if (detected.Installed)
        {
            progress?.Report($"{detected.Name} 安装成功：{detected.Version}");
            return (true, $"{detected.Name} 安装成功：{detected.Version}");
        }
        return (false, $"{package} 安装命令已执行，但仍未检测到可执行文件。\n" + Tail(log));
    }

    private async Task<(bool Ok, string Message)> InstallWhisperAsync(IProgress<string>? progress)
    {
        var python = DetectPython();
        if (!python.Installed)
        {
            return (false, "未检测到 Python，请先安装 Python 后再安装 Whisper");
        }

        progress?.Report("正在通过 pip 安装 faster-whisper（安装到当前用户，无需管理员权限）…");
        var psi = new ProcessStartInfo
        {
            FileName = python.ToolPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in new[] { "-m", "pip", "install", "--user", "--upgrade", "faster-whisper" })
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (false, "无法启动 pip");
            }
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
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
                return (false, "pip 安装超时（10 分钟），请检查网络后重试");
            }

            var log = (await stdoutTask) + "\n" + (await stderrTask);
            if (proc.ExitCode != 0)
            {
                return (false, "pip 安装失败：\n" + Tail(log));
            }
            var detected = DetectWhisper();
            return detected.Installed
                ? (true, "Whisper 语音识别安装成功：" + detected.Version)
                : (false, "pip 安装命令已执行，但仍未检测到 faster-whisper。\n" + Tail(log));
        }
        catch (Exception ex)
        {
            return (false, "安装 Whisper 失败：" + ex.Message);
        }
    }

    private async Task<(bool Ok, string Message)> InstallChocolateyAsync(IProgress<string>? progress)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(),
            $"lsct_install_choco_{DateTime.Now:yyyyMMddHHmmss}.ps1");
        await File.WriteAllTextAsync(scriptPath, """
            $ErrorActionPreference = 'Stop'
            [System.Net.ServicePointManager]::SecurityProtocol = `
                [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
            $web = New-Object System.Net.WebClient
            Invoke-Expression ($web.DownloadString('https://community.chocolatey.org/install.ps1'))
            """, new UTF8Encoding(false));

        var logPath = Path.ChangeExtension(scriptPath, ".log");
        var inner = $"""
            $ErrorActionPreference = 'Continue'
            & '{scriptPath}' *> '{logPath}'
            """;
        var (ok, message) = await RunElevatedPowerShellAsync(inner);
        var log = ReadLog(logPath);
        if (!ok)
        {
            return (false, message + (log.Length > 0 ? "\n" + Tail(log) : ""));
        }
        return File.Exists(ChocoExe)
            ? (true, "Chocolatey 安装完成")
            : (false, "Chocolatey 安装脚本执行完毕，但未找到 choco.exe。\n" + Tail(log));
    }

    /// <summary>
    /// 以管理员身份运行一段 PowerShell 命令，并把输出重定向写到脚本自带的位置。
    /// 返回 (是否成功启动并完成, 错误信息)。用户取消 UAC 时返回 false。
    /// </summary>
    private async Task<(bool Ok, string Message)> RunElevatedPowerShellAsync(string innerCommand)
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(innerCommand));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            if (IsAdministrator())
            {
                psi.ArgumentList.Add("-EncodedCommand");
                psi.ArgumentList.Add(encoded);
            }
            else
            {
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(
                    "Start-Process -FilePath powershell.exe -Verb RunAs -Wait " +
                    "-ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand','" +
                    encoded + "'");
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (false, "无法启动 PowerShell");
            }
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (proc.ExitCode != 0)
            {
                return (false, "管理员进程返回错误：" +
                    Tail(stderr.Length > 0 ? stderr : stdout));
            }
            return (true, stdout);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (false, "未能获取管理员权限（可能取消了 UAC 授权）：" + ex.Message);
        }
        catch (Exception ex)
        {
            return (false, "安装失败：" + ex.Message);
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string ReadLog(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch
        {
            return "";
        }
    }

    private static string Tail(string text, int max = 1500)
    {
        var t = (text ?? "").Trim();
        return t.Length <= max ? t : t[^max..];
    }

    private static IEnumerable<string> FindInPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(dir.Trim('"'), fileName);
            }
            catch
            {
                continue;
            }
            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static (int Code, string Output) RunCapture(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (-1, "");
            }
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(timeoutMs))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }
                return (-1, stdout + stderr);
            }
            return (proc.ExitCode, (stdout + "\n" + stderr).Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
