// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using LiveStreamClipTool.Common;
using Microsoft.UI.Xaml;

namespace LiveStreamClipTool.Views;

public sealed class StreamerRow : ObservableObject
{
    private bool _isSelected;
    private Visibility _dragHandleVisibility = Visibility.Visible;

    public long Id { get; set; }
    public string Uid { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string RoomTitle { get; set; } = "";
    public string UpdatedText { get; set; } = "";
    public string CreatedText { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public long SortOrder { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public Visibility DragHandleVisibility
    {
        get => _dragHandleVisibility;
        set => Set(ref _dragHandleVisibility, value);
    }
}
