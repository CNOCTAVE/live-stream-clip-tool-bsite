// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

namespace LiveStreamClipTool.Models;

public sealed class AppSettings
{
    public string RootDirectory { get; set; } = DefaultRoot;
    public int AutoStopMinutes { get; set; }
    public bool AutoClipEnabled { get; set; } = true;
    public int MinSegmentSeconds { get; set; } = 45;
    public int MaxSegmentSeconds { get; set; } = 720;
    public int MergeGapSeconds { get; set; } = 10;
    public int CpuTotalThreshold { get; set; } = 75;
    public int CpuCoreThreshold { get; set; } = 85;
    public int RecordQn { get; set; } = 10000;
    public string FfmpegPath { get; set; } = "";
    public string PythonPath { get; set; } = "";

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "批量切片工具");
}
