// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using LiveStreamClipTool.Data;
using LiveStreamClipTool.Models;

namespace LiveStreamClipTool.Services;

/// <summary>设置的内存缓存：所有页面读 Current，保存后立即生效。</summary>
public sealed class SettingsStore
{
    private readonly SettingsRepository _repo;

    public SettingsStore(SettingsRepository repo)
    {
        _repo = repo;
        Current = _repo.Load();
    }

    public AppSettings Current { get; private set; }

    public void Reload() => Current = _repo.Load();

    public void Save(AppSettings settings)
    {
        _repo.Save(settings);
        Current = _repo.Load();
    }
}
