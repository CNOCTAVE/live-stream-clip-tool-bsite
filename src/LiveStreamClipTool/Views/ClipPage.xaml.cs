// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using System.Collections.ObjectModel;
using System.Diagnostics;
using LiveStreamClipTool.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LiveStreamClipTool.Views;

public sealed partial class ClipPage : Page
{
    private bool _suppressSelectAll;
    private bool _suppressRowEvents;

    public ObservableCollection<ClipRow> ClipRows { get; } = new();

    public ClipPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppServices.AutoClip.Changed += AutoClip_Changed;
        AppServices.AutoClip.ProgressChanged += AutoClip_ProgressChanged;
        LoadData();
        UpdateProgressVisibility();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppServices.AutoClip.Changed -= AutoClip_Changed;
        AppServices.AutoClip.ProgressChanged -= AutoClip_ProgressChanged;
    }

    private void AutoClip_Changed(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            LoadData();
            UpdateProgressVisibility();
        });

    private void AutoClip_ProgressChanged(double percent, string stage) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            ProgressPanel.Visibility = Visibility.Visible;
            if (percent < 0)
            {
                ClipProgress.IsIndeterminate = true;
                ProgressText.Text = stage;
                return;
            }
            ClipProgress.IsIndeterminate = false;
            ClipProgress.Value = Math.Clamp(percent, 0, 100);
            ProgressText.Text = $"{stage} · {Math.Clamp(percent, 0, 100):0}%";
        });

    private void UpdateProgressVisibility()
    {
        if (AppServices.AutoClip.IsBusy)
        {
            ProgressPanel.Visibility = Visibility.Visible;
            if (!ClipProgress.IsIndeterminate && ClipProgress.Value <= 0)
            {
                ClipProgress.IsIndeterminate = true;
                ProgressText.Text = "正在执行自动切片…";
            }
        }
        else
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            ClipProgress.IsIndeterminate = false;
            ClipProgress.Value = 0;
        }
    }

    private void LoadData()
    {
        ClipRows.Clear();
        foreach (var clip in AppServices.Clips.GetRecent(500))
        {
            ClipRows.Add(new ClipRow
            {
                Id = clip.Id,
                StreamerName = clip.StreamerNickname.Length > 0 ? clip.StreamerNickname : clip.RoomId,
                ClipTimeText = clip.ClipTime.ToString("yyyy-MM-dd HH:mm:ss"),
                DurationText = RecordingService.FormatDuration(clip.DurationSeconds),
                FilePath = clip.FilePath
            });
        }
        CountText.Text = $"共 {ClipRows.Count} 个切片";
        RefreshSelectAllState();
    }

    private void RefreshSelectAllState()
    {
        _suppressSelectAll = true;
        var rows = ClipRows.ToList();
        var selected = rows.Count(r => r.IsSelected);
        SelectAllBox.IsChecked = rows.Count > 0 && selected == rows.Count
            ? true
            : selected == 0
                ? false
                : null;
        _suppressSelectAll = false;
    }

    private void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectAll)
        {
            return;
        }
        var select = SelectAllBox.IsChecked == true;
        _suppressRowEvents = true;
        foreach (var row in ClipRows)
        {
            row.IsSelected = select;
        }
        _suppressRowEvents = false;
    }

    private void RowCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressRowEvents)
        {
            return;
        }
        RefreshSelectAllState();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadData();
        PageInfo.Severity = InfoBarSeverity.Success;
        PageInfo.Message = "切片列表已刷新";
        PageInfo.IsOpen = true;
    }

    private async void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        var pending = AppServices.AutoClip.PendingCount();
        if (pending == 0)
        {
            PageInfo.Severity = InfoBarSeverity.Informational;
            PageInfo.Message = "切片队列为空";
            PageInfo.IsOpen = true;
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "清空队列",
            Content = $"确定清空切片队列中的 {pending} 个等待任务吗？\n" +
                      "自动切片和语音转文字的待执行任务都会移除，已生成的切片文件不受影响。" +
                      (AppServices.AutoClip.IsBusy
                          ? "\n当前正在执行的任务不会被中断，执行完后队列即为空。"
                          : ""),
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var cleared = AppServices.AutoClip.ClearQueue();
        LoadData();
        PageInfo.Severity = InfoBarSeverity.Success;
        PageInfo.Message = AppServices.AutoClip.IsBusy
            ? $"已清空 {cleared} 个等待任务，当前任务执行完后队列为空"
            : $"已清空 {cleared} 个等待任务";
        PageInfo.IsOpen = true;
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var rows = ClipRows.Where(r => r.IsSelected).ToList();
        if (rows.Count == 0)
        {
            PageInfo.Severity = InfoBarSeverity.Warning;
            PageInfo.Message = "请先勾选要删除的切片";
            PageInfo.IsOpen = true;
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "删除切片",
            Content = $"确定删除选中的 {rows.Count} 个切片吗？\n" +
                      "切片文件、同名文字文件、封面图片和识别队列任务会一并删除。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var removed = 0;
        var missing = 0;
        var failed = 0;
        var errors = new List<string>();
        foreach (var row in rows)
        {
            var result = ClipDeletion.Delete(row.Id, row.FilePath);
            if (result.Ok)
            {
                removed++;
                if (result.Missing)
                {
                    missing++;
                }
            }
            else
            {
                failed++;
                if (errors.Count < 3)
                {
                    errors.Add(result.Error);
                }
            }
        }

        LoadData();
        PageInfo.Severity = failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        PageInfo.Message = $"已删除 {removed} 个切片" +
                           (missing > 0 ? $"（{missing} 个文件不存在，已直接清理记录）" : "") +
                           (failed > 0 ? $"；{failed} 个失败：{string.Join("；", errors)}" : "");
        PageInfo.IsOpen = true;
    }

    private async void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipRow row })
        {
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "删除切片",
            Content = $"确定删除「{Path.GetFileName(row.FilePath)}」吗？\n" +
                      "切片文件、同名文字文件、封面图片和识别队列任务会一并删除。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = ClipDeletion.Delete(row.Id, row.FilePath);
        LoadData();
        PageInfo.Severity = result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        PageInfo.Message = result.Ok
            ? result.Missing ? "文件不存在，已清理切片记录、文字文件、封面和识别任务" : "已删除切片、文字文件、封面和相关记录"
            : "删除失败：" + result.Error;
        PageInfo.IsOpen = true;
    }

    private void OpenClipFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipRow row })
        {
            return;
        }
        try
        {
            var dir = Path.GetDirectoryName(row.FilePath) ?? "";
            if (File.Exists(row.FilePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{row.FilePath}\"",
                    UseShellExecute = true
                });
            }
            else if (dir.Length > 0 && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            else
            {
                PageInfo.Severity = InfoBarSeverity.Warning;
                PageInfo.Message = "切片文件和所在文件夹都不存在";
                PageInfo.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            PageInfo.Severity = InfoBarSeverity.Error;
            PageInfo.Message = "打开文件夹失败：" + ex.Message;
            PageInfo.IsOpen = true;
        }
    }
}
