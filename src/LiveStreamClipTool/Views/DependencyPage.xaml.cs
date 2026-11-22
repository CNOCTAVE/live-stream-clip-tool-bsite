// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using System.Collections.ObjectModel;
using LiveStreamClipTool.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LiveStreamClipTool.Views;

public sealed partial class DependencyPage : Page
{
    private bool _busy;

    public ObservableCollection<DependencyRow> Rows { get; } = new();

    public DependencyPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await DetectAllAsync(showInfo: false);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
    }

    private void ShowInfo(InfoBarSeverity severity, string message)
    {
        PageInfo.Severity = severity;
        PageInfo.Message = message;
        PageInfo.IsOpen = message.Length > 0;
    }

    private static string BuildDetail(DependencyInfo info)
    {
        if (!info.Installed)
        {
            if (!string.IsNullOrWhiteSpace(info.InstallHint))
            {
                return info.InstallHint;
            }
            return string.IsNullOrWhiteSpace(info.ChocoPackage)
                ? "Chocolatey 是依赖安装的包管理器"
                : $"未检测到，将通过 Chocolatey 安装 choco 包「{info.ChocoPackage}」";
        }
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.Version))
        {
            parts.Add(info.Version);
        }
        if (!string.IsNullOrWhiteSpace(info.ToolPath))
        {
            parts.Add(info.ToolPath);
        }
        return string.Join(" ｜ ", parts);
    }

    private async Task DetectAllAsync(bool showInfo)
    {
        if (_busy)
        {
            return;
        }
        SetBusy(true);
        try
        {
            var list = await AppServices.Dependencies.DetectAllAsync();
            Rows.Clear();
            foreach (var info in list)
            {
                Rows.Add(new DependencyRow
                {
                    Key = info.Key,
                    Name = info.Name,
                    PackageName = info.ChocoPackage,
                    DetailText = BuildDetail(info),
                    IsInstalled = info.Installed
                });
            }
            var installed = list.Count(x => x.Installed);
            SummaryText.Text = $"已安装 {installed} / {list.Count} 项依赖";
            if (showInfo)
            {
                ShowInfo(installed == list.Count ? InfoBarSeverity.Success : InfoBarSeverity.Informational,
                    installed == list.Count
                        ? "所有依赖均已安装"
                        : $"检测完成：{list.Count - installed} 项依赖未安装");
            }
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, "检测依赖失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Detect_Click(object sender, RoutedEventArgs e)
    {
        await DetectAllAsync(showInfo: true);
    }

    private async void InstallRow_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not FrameworkElement { DataContext: DependencyRow row })
        {
            return;
        }

        SetBusy(true);
        row.IsInstalling = true;
        ShowInfo(InfoBarSeverity.Informational, $"正在安装 {row.Name}…安装过程需要管理员授权。");
        var progress = new Progress<string>(message =>
        {
            ShowInfo(InfoBarSeverity.Informational, message);
        });

        try
        {
            var (ok, message) = await AppServices.Dependencies.InstallAsync(row.Key, progress);
            if (ok)
            {
                ShowInfo(InfoBarSeverity.Success, message.Length > 0 ? message : $"{row.Name} 安装成功");
            }
            else
            {
                ShowInfo(InfoBarSeverity.Error, message.Length > 0 ? message : $"{row.Name} 安装失败");
            }
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, $"安装 {row.Name} 失败：{ex.Message}");
        }
        finally
        {
            row.IsInstalling = false;
            SetBusy(false);
        }

        await DetectAllAsync(showInfo: false);
    }
}
