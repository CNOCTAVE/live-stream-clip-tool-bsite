// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using System.Collections.ObjectModel;
using LiveStreamClipTool.Data;
using LiveStreamClipTool.Dialogs;
using LiveStreamClipTool.Models;
using LiveStreamClipTool.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace LiveStreamClipTool.Views;

public sealed partial class RecordingPage : Page
{
    private readonly DispatcherQueueTimer _timer;
    private int _suppressCheckEvents;
    private bool _busy;

    public ObservableCollection<StreamerPickRow> PickRows { get; } = new();
    public ObservableCollection<RecordingRow> RecordRows { get; } = new();

    public RecordingPage()
    {
        InitializeComponent();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => TickTimer();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppServices.Recorder.Changed += Recorder_Changed;
        LoadAll();
        _timer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        AppServices.Recorder.Changed -= Recorder_Changed;
    }

    private void Recorder_Changed(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(LoadAll);

    private void LoadAll()
    {
        _suppressCheckEvents++;
        try
        {
            var selectedPicks = PickRows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
            PickRows.Clear();
            foreach (var s in AppServices.Streamers.GetAll())
            {
                PickRows.Add(new StreamerPickRow
                {
                    Id = s.Id,
                    Nickname = s.Nickname,
                    RoomId = s.RoomId,
                    IsSelected = selectedPicks.Contains(s.Id)
                });
            }

            var selectedRecs = RecordRows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
            RecordRows.Clear();
            foreach (var rec in AppServices.Recordings.GetRecent(300))
            {
                var row = new RecordingRow
                {
                    Id = rec.Id,
                    StreamerId = rec.StreamerId,
                    StreamerName = rec.StreamerNickname.Length > 0 ? rec.StreamerNickname : rec.RoomId,
                    RoomTitle = rec.RoomTitle,
                    FileName = Path.GetFileName(rec.FilePath),
                    FilePath = rec.FilePath,
                    IsImported = rec.StreamerUid == StreamerRepository.LocalUid,
                    StartedAt = rec.StartedAt,
                    StartedText = rec.StartedAt.ToString("MM-dd HH:mm:ss"),
                    DurationSeconds = rec.DurationSeconds,
                    DbStatusText = RecordingStatus.ToChinese(rec.Status),
                    DbStopReason = string.IsNullOrWhiteSpace(rec.StopReason) ? "—" : rec.StopReason,
                    IsSelected = selectedRecs.Contains(rec.Id)
                };
                row.StatusText = row.DbStatusText;
                row.StopReason = row.DbStopReason;
                row.DurationText = RecordingService.FormatDuration(rec.DurationSeconds);
                RecordRows.Add(row);
            }
        }
        finally
        {
            _suppressCheckEvents--;
        }

        ApplyActiveStates();
        UpdateAutoStopText();
        RefreshPickSelectAll();
        RefreshRecordSelectAll();
        PickHint.Visibility = PickRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyActiveStates()
    {
        var active = AppServices.Recorder.ActiveSnapshot();
        var byId = active.ToDictionary(a => a.RecordingId);

        foreach (var row in RecordRows)
        {
            if (byId.TryGetValue(row.Id, out var a))
            {
                row.IsActive = true;
                row.StatusText = "录制中";
                row.StopReason = "—";
                row.DurationText = RecordingService.FormatDuration(a.DurationSeconds);
            }
            else
            {
                row.IsActive = false;
                row.StatusText = row.DbStatusText;
                row.StopReason = row.DbStopReason;
                row.DurationText = RecordingService.FormatDuration(row.DurationSeconds);
            }
        }

        var recordingStreamerIds = active.Select(a => a.StreamerId).ToHashSet();
        foreach (var pick in PickRows)
        {
            pick.IsRecording = recordingStreamerIds.Contains(pick.Id);
        }
    }

    private void TickTimer()
    {
        var active = AppServices.Recorder.ActiveSnapshot();
        var byId = active.ToDictionary(a => a.RecordingId);
        foreach (var row in RecordRows)
        {
            if (row.IsActive && byId.TryGetValue(row.Id, out var a))
            {
                row.DurationText = RecordingService.FormatDuration(a.DurationSeconds);
            }
        }

        var recordingStreamerIds = active.Select(a => a.StreamerId).ToHashSet();
        foreach (var pick in PickRows)
        {
            pick.IsRecording = recordingStreamerIds.Contains(pick.Id);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
    }

    private void UpdateAutoStopText()
    {
        var minutes = AppServices.Settings.Current.AutoStopMinutes;
        AutoStopText.Text = minutes > 0 ? $"自动停止：{minutes} 分钟" : "自动停止：关闭";
    }

    private void ShowInfo(InfoBarSeverity severity, string message)
    {
        PageInfo.Severity = severity;
        PageInfo.Message = message;
        PageInfo.IsOpen = message.Length > 0;
    }

    private static string JoinErrors(List<string> errors)
    {
        if (errors.Count == 0)
        {
            return "";
        }
        var head = string.Join("；", errors.Take(3));
        return errors.Count > 3 ? $"；{head} 等 {errors.Count} 项失败" : $"；{head}";
    }

    private async void StartAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        var picks = PickRows.Where(p => p.IsSelected).ToList();
        if (picks.Count == 0)
        {
            ShowInfo(InfoBarSeverity.Warning, "请先在左侧勾选要录制的主播");
            return;
        }

        var streamers = AppServices.Streamers.GetAll().ToDictionary(s => s.Id);
        var errors = new List<string>();
        var started = 0;

        SetBusy(true);
        try
        {
            foreach (var pick in picks)
            {
                if (!streamers.TryGetValue(pick.Id, out var streamer))
                {
                    continue;
                }
                if (AppServices.Recorder.FindByStreamer(streamer.Id) is not null)
                {
                    errors.Add($"{streamer.Nickname}：已在录制中");
                    continue;
                }
                var result = await AppServices.Recorder.StartAsync(streamer);
                if (result.Ok)
                {
                    started++;
                }
                else
                {
                    errors.Add($"{streamer.Nickname}：{result.Message}");
                }
            }
        }
        finally
        {
            SetBusy(false);
        }

        LoadAll();
        if (started == 0)
        {
            ShowInfo(InfoBarSeverity.Error, "没有启动任何录制" + JoinErrors(errors));
        }
        else
        {
            var text = $"已开始录制 {started} 路";
            ShowInfo(errors.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                text + JoinErrors(errors));
        }
    }

    private async void StopAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        var selectedIds = PickRows.Where(p => p.IsSelected).Select(p => p.Id).ToHashSet();
        var active = AppServices.Recorder.ActiveSnapshot();
        var targets = selectedIds.Count > 0
            ? active.Where(a => selectedIds.Contains(a.StreamerId)).ToList()
            : active.ToList();

        if (targets.Count == 0)
        {
            ShowInfo(InfoBarSeverity.Informational, "当前没有正在进行的录制");
            return;
        }

        SetBusy(true);
        try
        {
            foreach (var target in targets)
            {
                await AppServices.Recorder.StopAsync(target.RecordingId, "手动停止");
            }
        }
        finally
        {
            SetBusy(false);
        }

        LoadAll();
        ShowInfo(InfoBarSeverity.Success, $"已停止 {targets.Count} 路录制，停止后将自动执行切片");
    }

    private async void AutoStop_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AutoStopDialog(AppServices.Settings.Current.AutoStopMinutes)
        {
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var settings = AppServices.Settings.Current;
        settings.AutoStopMinutes = dialog.Minutes;
        AppServices.Settings.Save(settings);
        UpdateAutoStopText();
        ShowInfo(InfoBarSeverity.Success, dialog.Minutes > 0
            ? $"已设置：任意录制达到 {dialog.Minutes} 分钟后自动停止"
            : "已关闭自动停止录播");
    }

    private async void DeleteFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        var rows = RecordRows.Where(r => r.IsSelected).ToList();
        if (rows.Count == 0)
        {
            ShowInfo(InfoBarSeverity.Warning, "请先勾选要删除的录播记录");
            return;
        }
        var recording = rows.FirstOrDefault(r => AppServices.Recorder.IsRecording(r.Id));
        if (recording is not null)
        {
            ShowInfo(InfoBarSeverity.Warning,
                $"「{recording.StreamerName}」正在录制，请先停止后再删除");
            return;
        }

        var confirmed = await ConfirmAsync("删除录播文件",
            $"将删除选中的 {rows.Count} 条记录对应的录制文件，删除后不可恢复。\n" +
            "本地导入的视频只删除记录，不删除源文件；文件已不存在时会直接删除数据库条目。", "删除");
        if (!confirmed)
        {
            return;
        }

        var errors = new List<string>();
        var deleted = 0;
        SetBusy(true);
        try
        {
            foreach (var row in rows)
            {
                try
                {
                    if (!row.IsImported && File.Exists(row.FilePath))
                    {
                        File.Delete(row.FilePath);
                    }
                    AppServices.Recordings.Delete(row.Id);
                    deleted++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{row.FileName}：{ex.Message}");
                }
            }
        }
        finally
        {
            SetBusy(false);
        }

        LoadAll();
        if (deleted == 0)
        {
            ShowInfo(InfoBarSeverity.Error, "没有删除任何录播文件" + JoinErrors(errors));
        }
        else
        {
            ShowInfo(errors.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                $"已删除 {deleted} 条录播记录" + JoinErrors(errors));
        }
    }

    private async void AddLocal_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || App.MainWindow is null)
        {
            return;
        }
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary
        };
        foreach (var ext in new[] { ".mp4", ".flv", ".mkv", ".mov", ".avi", ".ts", ".webm", ".m4v", ".wmv" })
        {
            picker.FileTypeFilter.Add(ext);
        }
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0)
        {
            return;
        }

        SetBusy(true);
        List<ImportResult> results;
        try
        {
            results = await AppServices.Recorder.ImportLocalAsync(
                files.Select(f => f.Path).ToList());
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, "导入失败：" + ex.Message);
            return;
        }
        finally
        {
            SetBusy(false);
        }

        LoadAll();
        var ok = results.Count(r => r.Ok);
        var errors = results.Where(r => !r.Ok)
            .Select(r => $"{Path.GetFileName(r.FilePath)}：{r.Message}")
            .ToList();
        if (ok == 0)
        {
            ShowInfo(InfoBarSeverity.Error, "没有导入任何视频" + JoinErrors(errors));
        }
        else
        {
            ShowInfo(errors.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                $"已导入 {ok} 个本地视频" +
                (AppServices.Settings.Current.AutoClipEnabled ? "，将自动执行切片" : "") +
                JoinErrors(errors));
        }
    }

    private async void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RecordingRow row })
        {
            return;
        }
        if (AppServices.Recorder.IsRecording(row.Id))
        {
            ShowInfo(InfoBarSeverity.Warning, $"「{row.StreamerName}」正在录制，请先停止后再删除");
            return;
        }

        var confirmed = await ConfirmAsync("删除录播记录",
            row.IsImported
                ? $"删除「{row.FileName}」的记录？本地导入的视频源文件不会被删除。"
                : $"删除「{row.FileName}」的记录并删除视频文件？删除后不可恢复。",
            "删除");
        if (!confirmed)
        {
            return;
        }

        try
        {
            if (!row.IsImported && File.Exists(row.FilePath))
            {
                File.Delete(row.FilePath);
            }
            AppServices.Recordings.Delete(row.Id);
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, "删除失败：" + ex.Message);
            return;
        }

        LoadAll();
        ShowInfo(InfoBarSeverity.Success,
            row.IsImported ? "已删除记录，本地视频文件保留" : "已删除录播记录和视频文件");
    }

    private async void StopRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RecordingRow row })
        {
            return;
        }
        await AppServices.Recorder.StopAsync(row.Id, "手动停止");
        ShowInfo(InfoBarSeverity.Success, $"已停止「{row.StreamerName}」的录制");
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadAll();
        ShowInfo(InfoBarSeverity.Success, "列表已刷新");
    }

    private void PickCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressCheckEvents > 0)
        {
            return;
        }
        RefreshPickSelectAll();
    }

    private void PickSelectAll_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressCheckEvents > 0)
        {
            return;
        }
        _suppressCheckEvents++;
        try
        {
            var check = PickSelectAll.IsChecked == true;
            foreach (var row in PickRows)
            {
                row.IsSelected = check;
            }
        }
        finally
        {
            _suppressCheckEvents--;
        }
    }

    private void RecordSelectAll_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressCheckEvents > 0)
        {
            return;
        }
        _suppressCheckEvents++;
        try
        {
            var check = RecordSelectAll.IsChecked == true;
            foreach (var row in RecordRows)
            {
                row.IsSelected = check;
            }
        }
        finally
        {
            _suppressCheckEvents--;
        }
    }

    private void RowCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressCheckEvents > 0)
        {
            return;
        }
        RefreshRecordSelectAll();
    }

    private void RefreshPickSelectAll()
    {
        _suppressCheckEvents++;
        try
        {
            var selected = PickRows.Count(r => r.IsSelected);
            PickSelectAll.IsChecked = selected == 0
                ? false
                : selected == PickRows.Count ? true : null;
        }
        finally
        {
            _suppressCheckEvents--;
        }
    }

    private void RefreshRecordSelectAll()
    {
        _suppressCheckEvents++;
        try
        {
            var selected = RecordRows.Count(r => r.IsSelected);
            RecordSelectAll.IsChecked = selected == 0
                ? false
                : selected == RecordRows.Count ? true : null;
        }
        finally
        {
            _suppressCheckEvents--;
        }
    }

    private async Task<bool> ConfirmAsync(string title, string content, string primaryText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
