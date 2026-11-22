// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using Microsoft.UI.Xaml.Controls;

namespace LiveStreamClipTool.Dialogs;

public sealed partial class AutoStopDialog : ContentDialog
{
    public AutoStopDialog(int currentMinutes)
    {
        InitializeComponent();
        MinutesBox.Value = Math.Clamp(currentMinutes, 0, 1440);
    }

    public int Minutes => double.IsNaN(MinutesBox.Value)
        ? 0
        : (int)Math.Clamp(Math.Round(MinutesBox.Value), 0, 1440);
}
