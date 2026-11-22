// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

namespace LiveStreamClipTool.Views;

public sealed class ClipExportRow
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string MetaText { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public double DurationSeconds { get; set; }
}
