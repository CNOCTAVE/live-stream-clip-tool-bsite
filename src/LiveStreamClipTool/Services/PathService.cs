// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

namespace LiveStreamClipTool.Services;

/// <summary>
/// 设置里只配置一个根目录，各功能在根目录下自动拼接自己的子目录。
/// </summary>
public sealed class PathService
{
    private readonly SettingsStore _settings;

    public PathService(SettingsStore settings) => _settings = settings;

    public string Root => string.IsNullOrWhiteSpace(_settings.Current.RootDirectory)
        ? Models.AppSettings.DefaultRoot
        : _settings.Current.RootDirectory;

    public string RecordingsDir => Path.Combine(Root, "录播");
    public string ClipsDir => Path.Combine(Root, "切片");
    public string TempDir => Path.Combine(Root, "临时");

    public string StreamerRecordingDir(string nickname) =>
        Path.Combine(RecordingsDir, SanitizePart(nickname));

    public string StreamerClipDir(string nickname) =>
        Path.Combine(ClipsDir, SanitizePart(nickname));

    public void EnsureBaseDirs()
    {
        Directory.CreateDirectory(RecordingsDir);
        Directory.CreateDirectory(ClipsDir);
        Directory.CreateDirectory(TempDir);
    }

    public static string SanitizePart(string? value)
    {
        var s = string.IsNullOrWhiteSpace(value) ? "未命名" : value!.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            s = s.Replace(ch, '_');
        }
        s = s.Trim(' ', '.');
        if (s.Length == 0)
        {
            s = "未命名";
        }
        return s.Length > 60 ? s[..60] : s;
    }
}
