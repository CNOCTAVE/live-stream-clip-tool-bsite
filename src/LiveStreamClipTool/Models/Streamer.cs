// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

namespace LiveStreamClipTool.Models;

public sealed class Streamer
{
    public long Id { get; set; }
    public string Uid { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string RoomTitle { get; set; } = "";
    public string FaceUrl { get; set; } = "";
    public long SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
