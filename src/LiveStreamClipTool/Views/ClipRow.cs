// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using LiveStreamClipTool.Common;

namespace LiveStreamClipTool.Views;

public sealed class ClipRow : ObservableObject
{
    private bool _isSelected;

    public long Id { get; set; }
    public string StreamerName { get; set; } = "";
    public string ClipTimeText { get; set; } = "";
    public string DurationText { get; set; } = "";
    public string FilePath { get; set; } = "";

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}
