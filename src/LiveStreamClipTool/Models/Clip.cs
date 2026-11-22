// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

namespace LiveStreamClipTool.Models;

public sealed class Clip
{
    public long Id { get; set; }
    public long? RecordingId { get; set; }
    public long StreamerId { get; set; }
    public string Title { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DateTime ClipTime { get; set; }
    public double DurationSeconds { get; set; }
    public DateTime CreatedAt { get; set; }

    // 连表查询带出
    public string StreamerNickname { get; set; } = "";
    public string RoomId { get; set; } = "";
}
