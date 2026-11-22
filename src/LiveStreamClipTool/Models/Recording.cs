// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

namespace LiveStreamClipTool.Models;

public sealed class Recording
{
    public long Id { get; set; }
    public long StreamerId { get; set; }
    public string FilePath { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }

    /// <summary>recording / stopped / auto_stopped / error</summary>
    public string Status { get; set; } = RecordingStatus.Recording;

    public string StopReason { get; set; } = "";
    public double DurationSeconds { get; set; }
    public int AutoStopSeconds { get; set; }

    // 连表查询带出
    public string StreamerNickname { get; set; } = "";
    public string StreamerUid { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string RoomTitle { get; set; } = "";
}

public static class RecordingStatus
{
    public const string Recording = "recording";
    public const string Stopped = "stopped";
    public const string AutoStopped = "auto_stopped";
    public const string Error = "error";

    public static string ToChinese(string status) => status switch
    {
        Recording => "录制中",
        AutoStopped => "已自动停止",
        Stopped => "已停止",
        Error => "异常结束",
        _ => status
    };
}
