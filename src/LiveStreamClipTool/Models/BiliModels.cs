// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

namespace LiveStreamClipTool.Models;

public sealed class StreamerProbe
{
    public string Uid { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string RoomTitle { get; set; } = "";
    public string FaceUrl { get; set; } = "";
    public bool Living { get; set; }
}

public sealed class AreaSubCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class AreaCategory
{
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
    public List<AreaSubCategory> SubAreas { get; } = new();
}

public sealed class AreaRoom
{
    public string Uid { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string RoomTitle { get; set; } = "";
    public string FaceUrl { get; set; } = "";
}

public sealed class RoomInfo
{
    public string Uid { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Area { get; set; } = "";
    public string Cover { get; set; } = "";
    public bool Living { get; set; }
}
