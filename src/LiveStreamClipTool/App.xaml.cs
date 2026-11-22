// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using Microsoft.UI.Xaml;
using LiveStreamClipTool.Services;

namespace LiveStreamClipTool;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        AppServices.Initialize();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Closed += (_, _) => AppServices.Recorder.ShutdownSync();
        MainWindow.Activate();
        AppServices.Recorder.RecoverStaleRecordings();
        AppServices.AutoClip.ResumePending();
    }
}
