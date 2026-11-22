// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using LiveStreamClipTool.Common;
using Microsoft.UI.Xaml;

namespace LiveStreamClipTool.Views;

public sealed class DependencyRow : ObservableObject
{
    private bool _installed;
    private bool _installing;
    private string _detailText = "";

    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string PackageName { get; set; } = "";

    public bool IsInstalled
    {
        get => _installed;
        set
        {
            if (Set(ref _installed, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(InstalledVisibility));
                OnPropertyChanged(nameof(NotInstalledVisibility));
                OnPropertyChanged(nameof(InstallButtonVisibility));
            }
        }
    }

    public bool IsInstalling
    {
        get => _installing;
        set
        {
            if (Set(ref _installing, value))
            {
                OnPropertyChanged(nameof(InstallButtonVisibility));
                OnPropertyChanged(nameof(InstallingVisibility));
            }
        }
    }

    public string DetailText
    {
        get => _detailText;
        set => Set(ref _detailText, value);
    }

    public string StatusText => _installed ? "已安装" : "未安装";

    public Visibility InstalledVisibility =>
        _installed ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NotInstalledVisibility =>
        _installed ? Visibility.Collapsed : Visibility.Visible;

    public Visibility InstallButtonVisibility =>
        !_installed && !_installing ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InstallingVisibility =>
        _installing ? Visibility.Visible : Visibility.Collapsed;

    public string InstallButtonText => Key == "choco" ? "安装 Chocolatey" : "安装";
}
