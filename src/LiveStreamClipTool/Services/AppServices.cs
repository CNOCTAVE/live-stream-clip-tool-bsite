// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using LiveStreamClipTool.Data;

namespace LiveStreamClipTool.Services;

/// <summary>应用级单例容器（避免引入 DI 框架，页面直接取用）。</summary>
public static class AppServices
{
    public static Database Db { get; private set; } = null!;
    public static SettingsStore Settings { get; private set; } = null!;
    public static PathService Paths { get; private set; } = null!;
    public static StreamerRepository Streamers { get; private set; } = null!;
    public static RecordingRepository Recordings { get; private set; } = null!;
    public static ClipRepository Clips { get; private set; } = null!;
    public static ClipQueueRepository ClipQueue { get; private set; } = null!;
    public static BilibiliService Bili { get; private set; } = null!;
    public static DependencyService Dependencies { get; private set; } = null!;
    public static RecordingService Recorder { get; private set; } = null!;
    public static AutoClipService AutoClip { get; private set; } = null!;
    public static WhisperService Whisper { get; private set; } = null!;
    public static CoverService Cover { get; private set; } = null!;

    public static void Initialize()
    {
        Db = new Database();
        Db.Initialize();

        var settingsRepo = new SettingsRepository(Db);
        Settings = new SettingsStore(settingsRepo);
        Paths = new PathService(Settings);
        Streamers = new StreamerRepository(Db);
        Recordings = new RecordingRepository(Db);
        Clips = new ClipRepository(Db);
        ClipQueue = new ClipQueueRepository(Db);
        Bili = new BilibiliService();
        Dependencies = new DependencyService(Settings);
        Whisper = new WhisperService(Dependencies);
        Cover = new CoverService(Dependencies, Paths);
        AutoClip = new AutoClipService(Settings, Paths, Recordings, Clips, ClipQueue, Dependencies, Whisper);
        Recorder = new RecordingService(Bili, Settings, Paths, Streamers, Recordings, Dependencies);
        Recorder.SetAutoClip(AutoClip);
    }
}
