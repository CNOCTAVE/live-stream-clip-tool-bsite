// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using LiveStreamClipTool.Common;
using Microsoft.UI.Xaml;

namespace LiveStreamClipTool.Views;

public sealed class StreamerPickRow : ObservableObject
{
    private bool _isSelected;
    private bool _isRecording;

    public long Id { get; set; }
    public string Nickname { get; set; } = "";
    public string RoomId { get; set; } = "";

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (Set(ref _isRecording, value))
            {
                OnPropertyChanged(nameof(RecordingBadgeVisibility));
            }
        }
    }

    public Visibility RecordingBadgeVisibility =>
        _isRecording ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class RecordingRow : ObservableObject
{
    private bool _isSelected;
    private bool _isActive;
    private string _statusText = "";
    private string _stopReason = "";
    private string _durationText = "";

    public long Id { get; set; }
    public long StreamerId { get; set; }
    public string StreamerName { get; set; } = "";
    public bool IsImported { get; set; }
    public string RoomTitle { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string StartedText { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public double DurationSeconds { get; set; }
    public string DbStatusText { get; set; } = "";
    public string DbStopReason { get; set; } = "";

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (Set(ref _isActive, value))
            {
                OnPropertyChanged(nameof(StopButtonVisibility));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public string StopReason
    {
        get => _stopReason;
        set => Set(ref _stopReason, value);
    }

    public string DurationText
    {
        get => _durationText;
        set => Set(ref _durationText, value);
    }

    public Visibility StopButtonVisibility =>
        _isActive ? Visibility.Visible : Visibility.Collapsed;
}
