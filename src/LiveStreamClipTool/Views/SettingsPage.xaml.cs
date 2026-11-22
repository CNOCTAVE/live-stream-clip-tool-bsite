// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using LiveStreamClipTool.Models;
using LiveStreamClipTool.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace LiveStreamClipTool.Views;

public sealed partial class SettingsPage : Page
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadForm();
    }

    private void LoadForm()
    {
        _loading = true;
        var s = AppServices.Settings.Current;
        RootBox.Text = s.RootDirectory;
        FfmpegBox.Text = s.FfmpegPath;
        PythonBox.Text = s.PythonPath;
        AutoStopBox.Value = Math.Clamp(s.AutoStopMinutes, 0, 1440);
        AutoClipSwitch.IsOn = s.AutoClipEnabled;
        MinSegBox.Value = s.MinSegmentSeconds;
        MaxSegBox.Value = s.MaxSegmentSeconds;
        MergeGapBox.Value = s.MergeGapSeconds;
        CpuTotalBox.Value = s.CpuTotalThreshold;
        CpuCoreBox.Value = s.CpuCoreThreshold;
        SelectQn(s.RecordQn);
        UpdateDerivedPaths();
        _loading = false;
    }

    private void SelectQn(int qn)
    {
        var tag = qn.ToString();
        foreach (var item in QnBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                QnBox.SelectedItem = item;
                return;
            }
        }
        QnBox.SelectedIndex = 0;
    }

    private int SelectedQn()
    {
        if (QnBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            int.TryParse(tag, out var qn))
        {
            return qn;
        }
        return 10000;
    }

    private void RootBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
        {
            UpdateDerivedPaths();
        }
    }

    private void UpdateDerivedPaths()
    {
        var root = RootBox.Text.Trim();
        if (root.Length == 0)
        {
            root = AppSettings.DefaultRoot;
        }
        try
        {
            RecordingsDirText.Text = "录播目录：" + Path.Combine(root, "录播");
            ClipsDirText.Text = "切片目录：" + Path.Combine(root, "切片");
            TempDirText.Text = "临时目录：" + Path.Combine(root, "临时");
        }
        catch
        {
            RecordingsDirText.Text = "录播目录：路径无效";
            ClipsDirText.Text = "切片目录：路径无效";
            TempDirText.Text = "临时目录：路径无效";
        }
    }

    private void ShowInfo(InfoBarSeverity severity, string message)
    {
        PageInfo.Severity = severity;
        PageInfo.Message = message;
        PageInfo.IsOpen = message.Length > 0;
    }

    private static int IntValue(NumberBox box, int fallback)
    {
        if (double.IsNaN(box.Value))
        {
            return fallback;
        }
        return (int)Math.Round(box.Value);
    }

    private async void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is null)
        {
            return;
        }
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            RootBox.Text = folder.Path;
        }
    }

    private void OpenRoot_Click(object sender, RoutedEventArgs e)
    {
        var root = RootBox.Text.Trim();
        if (root.Length == 0)
        {
            root = AppSettings.DefaultRoot;
        }
        try
        {
            Directory.CreateDirectory(root);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = root,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, "打开目录失败：" + ex.Message);
        }
    }

    private async void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is null || sender is not FrameworkElement { Tag: string key })
        {
            return;
        }
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }
        if (key == "ffmpeg")
        {
            FfmpegBox.Text = file.Path;
        }
        else
        {
            PythonBox.Text = file.Path;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var root = RootBox.Text.Trim();
        if (root.Length == 0)
        {
            ShowInfo(InfoBarSeverity.Error, "请设置根目录");
            return;
        }
        var minSeg = IntValue(MinSegBox, 45);
        var maxSeg = IntValue(MaxSegBox, 720);
        var mergeGap = IntValue(MergeGapBox, 10);
        if (minSeg < 10 || maxSeg < 60)
        {
            ShowInfo(InfoBarSeverity.Error, "片段时长设置不合法：最短不少于 10 秒，最长不少于 60 秒");
            return;
        }
        if (minSeg > maxSeg)
        {
            ShowInfo(InfoBarSeverity.Error, "最短片段时长不能大于最长片段时长");
            return;
        }

        var ffmpeg = FfmpegBox.Text.Trim();
        var python = PythonBox.Text.Trim();
        var warnings = new List<string>();
        if (ffmpeg.Length > 0 && !File.Exists(ffmpeg))
        {
            warnings.Add("指定的 ffmpeg 路径不存在");
        }
        if (python.Length > 0 && !File.Exists(python))
        {
            warnings.Add("指定的 python 路径不存在");
        }

        var settings = new AppSettings
        {
            RootDirectory = root,
            AutoStopMinutes = Math.Clamp(IntValue(AutoStopBox, 0), 0, 1440),
            AutoClipEnabled = AutoClipSwitch.IsOn,
            MinSegmentSeconds = minSeg,
            MaxSegmentSeconds = maxSeg,
            MergeGapSeconds = Math.Clamp(mergeGap, 0, 600),
            CpuTotalThreshold = Math.Clamp(IntValue(CpuTotalBox, 75), 0, 100),
            CpuCoreThreshold = Math.Clamp(IntValue(CpuCoreBox, 85), 0, 100),
            RecordQn = SelectedQn(),
            FfmpegPath = ffmpeg,
            PythonPath = python
        };

        try
        {
            AppServices.Settings.Save(settings);
            AppServices.Paths.EnsureBaseDirs();
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, "保存设置失败：" + ex.Message);
            return;
        }

        UpdateDerivedPaths();
        if (warnings.Count > 0)
        {
            ShowInfo(InfoBarSeverity.Warning, "设置已保存，但" + string.Join("；", warnings));
        }
        else
        {
            ShowInfo(InfoBarSeverity.Success, "设置已保存，并已创建根目录下的子目录");
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        RootBox.Text = defaults.RootDirectory;
        FfmpegBox.Text = defaults.FfmpegPath;
        PythonBox.Text = defaults.PythonPath;
        AutoStopBox.Value = defaults.AutoStopMinutes;
        AutoClipSwitch.IsOn = defaults.AutoClipEnabled;
        MinSegBox.Value = defaults.MinSegmentSeconds;
        MaxSegBox.Value = defaults.MaxSegmentSeconds;
        MergeGapBox.Value = defaults.MergeGapSeconds;
        CpuTotalBox.Value = defaults.CpuTotalThreshold;
        CpuCoreBox.Value = defaults.CpuCoreThreshold;
        SelectQn(defaults.RecordQn);
        UpdateDerivedPaths();
        ShowInfo(InfoBarSeverity.Informational, "已填入默认值，点击「保存设置」后生效");
    }
}
