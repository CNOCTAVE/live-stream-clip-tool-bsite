// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using System.Collections.ObjectModel;
using LiveStreamClipTool.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.UI;

namespace LiveStreamClipTool.Views;

public sealed partial class ClipExportPage : Page
{
    private static readonly SolidColorBrush AccentBorder = new(Color.FromArgb(255, 0, 120, 212));
    private static readonly SolidColorBrush NormalBorder = new(Color.FromArgb(255, 96, 96, 96));

    private bool _reloading;
    private bool _exporting;
    private long _currentId;
    private ClipExportRow? _current;
    private CancellationTokenSource? _frameCts;
    private CoverFrame[] _frames = Array.Empty<CoverFrame>();
    private int _selectedFrame = -1;
    private int _coverToken;
    private bool _coverBusy;
    private bool _framesLoading;
    private bool _coverExists;
    private bool _transcriptExpanded;

    public ObservableCollection<ClipExportRow> ClipRows { get; } = new();

    public ClipExportPage()
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
        SyncTranscriptState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppServices.AutoClip.Changed -= AutoClip_Changed;
        AppServices.AutoClip.ProgressChanged -= AutoClip_ProgressChanged;
        _frameCts?.Cancel();
        _frameCts?.Dispose();
        _frameCts = null;
        _coverToken++;
    }

    private void AutoClip_Changed(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            LoadData();
            SyncTranscriptState();
        });

    private void AutoClip_ProgressChanged(double percent, string stage) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_current is null || AppServices.AutoClip.RunningClipId != _current.Id)
            {
                return;
            }
            TranscribeStatus.Text = stage;
            TranscribeProgress.Visibility = Visibility.Visible;
            if (percent < 0)
            {
                TranscribeProgress.IsIndeterminate = true;
            }
            else
            {
                TranscribeProgress.IsIndeterminate = false;
                TranscribeProgress.Value = Math.Clamp(percent, 0, 100);
            }
            UpdatePanelState();
        });

    private void ShowInfo(InfoBarSeverity severity, string message)
    {
        PageInfo.Severity = severity;
        PageInfo.Message = message;
        PageInfo.IsOpen = message.Length > 0;
    }

    private void LoadData()
    {
        var keepId = _currentId;
        _reloading = true;
        ClipRows.Clear();
        foreach (var clip in AppServices.Clips.GetRecent(500))
        {
            var fileName = Path.GetFileName(clip.FilePath);
            ClipRows.Add(new ClipExportRow
            {
                Id = clip.Id,
                Title = string.IsNullOrWhiteSpace(clip.Title) ? fileName : clip.Title,
                MetaText = $"{clip.StreamerNickname} · {clip.ClipTime:yyyy-MM-dd HH:mm:ss} · " +
                           RecordingService.FormatDuration(clip.DurationSeconds),
                FileName = fileName,
                FilePath = clip.FilePath,
                DurationSeconds = clip.DurationSeconds
            });
        }
        CountText.Text = $"共 {ClipRows.Count} 个切片";
        EmptyHint.Visibility = ClipRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipList.SelectedItem = keepId == 0
            ? null
            : ClipRows.FirstOrDefault(r => r.Id == keepId);
        _reloading = false;

        if (ClipList.SelectedItem is ClipExportRow row)
        {
            if (row.Id != _currentId)
            {
                SelectRow(row);
            }
            else
            {
                var pathChanged = !string.Equals(
                    _current?.FilePath, row.FilePath, StringComparison.OrdinalIgnoreCase);
                _current = row;
                if (pathChanged)
                {
                    _ = LoadFramesAsync(row);
                }
                else
                {
                    UpdatePanelState();
                }
            }
        }
        else
        {
            _currentId = 0;
            _current = null;
            ResetPanel();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadData();
        SyncTranscriptState();
        ShowInfo(InfoBarSeverity.Success, "切片列表已刷新");
    }

    private void ClipList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_reloading)
        {
            return;
        }
        if (ClipList.SelectedItem is ClipExportRow row)
        {
            SelectRow(row);
        }
        else
        {
            _currentId = 0;
            _current = null;
            ResetPanel();
        }
    }

    private void ResetPanel()
    {
        _frameCts?.Cancel();
        _frameCts?.Dispose();
        _frameCts = null;
        _coverToken++;
        _frames = Array.Empty<CoverFrame>();
        _selectedFrame = -1;
        _coverExists = false;
        _framesLoading = false;
        _coverBusy = false;
        for (var i = 0; i < 4; i++)
        {
            FrameImageAt(i).Source = null;
        }
        UpdateCoverSelection();
        CoverPreviewH.Source = null;
        CoverPreviewV.Source = null;
        CoverTextBox.Text = "";
        CoverStatus.Text = "选择切片后自动截取 4 帧";

        FileNameBox.Text = "";
        ExtText.Text = "";
        TranscriptBox.Text = "";
        TranscribeStatus.Text = "请选择左侧切片";
        HideProgress();
        UpdatePanelState();
    }

    private void SelectRow(ClipExportRow row)
    {
        var sameClip = row.Id == _currentId;
        _currentId = row.Id;
        _current = row;
        if (sameClip)
        {
            UpdatePanelState();
            return;
        }

        FileNameBox.Text = Path.GetFileNameWithoutExtension(row.FilePath);
        ExtText.Text = Path.GetExtension(row.FilePath);
        TranscriptBox.Text = "";
        HideProgress();
        TranscribeStatus.Text = "准备识别…";

        var transcriptPath = WhisperService.TranscriptPathFor(row.FilePath);
        if (File.Exists(transcriptPath))
        {
            LoadTranscript(transcriptPath);
        }
        else
        {
            EnsureTranscribeQueued(row);
        }
        UpdatePanelState();

        _ = LoadFramesAsync(row);
    }

    private void LoadTranscript(string transcriptPath)
    {
        try
        {
            TranscriptBox.Text = File.ReadAllText(transcriptPath);
            TranscribeStatus.Text = "已加载已有文字文件";
            HideProgress();
        }
        catch (Exception ex)
        {
            TranscribeStatus.Text = "读取文字文件失败：" + ex.Message;
        }
    }

    /// <summary>没有文字文件时：已有任务就显示状态，没有就让切片进入识别队列。</summary>
    private void EnsureTranscribeQueued(ClipExportRow row)
    {
        var task = AppServices.AutoClip.GetTranscribeTask(row.Id);
        if (task is null)
        {
            AppServices.AutoClip.QueueTranscribe(row.Id);
            TranscribeStatus.Text = "已加入识别队列，等待执行…";
            return;
        }
        switch (task.Value.Status)
        {
            case "pending":
                TranscribeStatus.Text = "已加入识别队列，等待执行…";
                break;
            case "running":
                TranscribeStatus.Text = "正在识别语音…";
                TranscribeProgress.Visibility = Visibility.Visible;
                TranscribeProgress.IsIndeterminate = true;
                break;
            case "error":
                TranscribeStatus.Text = "识别失败：" + task.Value.LastError;
                break;
            default:
                // 任务已完成但文字文件被删掉了，重新排队
                AppServices.AutoClip.QueueTranscribe(row.Id, force: true);
                TranscribeStatus.Text = "已加入识别队列，等待执行…";
                break;
        }
    }

    private void SyncTranscriptState()
    {
        if (_current is null)
        {
            return;
        }
        var transcriptPath = WhisperService.TranscriptPathFor(_current.FilePath);
        if (File.Exists(transcriptPath))
        {
            if (TranscriptBox.Text.Length == 0)
            {
                try
                {
                    TranscriptBox.Text = File.ReadAllText(transcriptPath);
                    TranscribeStatus.Text = "识别完成，文字已保存到同名 .txt 文件";
                }
                catch (Exception ex)
                {
                    TranscribeStatus.Text = "读取文字文件失败：" + ex.Message;
                }
            }
            else
            {
                TranscribeStatus.Text = "文字已保存到同名 .txt 文件";
            }
            HideProgress();
        }
        else
        {
            ApplyQueueState();
        }
        UpdatePanelState();
    }

    private void ApplyQueueState()
    {
        if (_current is null)
        {
            return;
        }
        var task = AppServices.AutoClip.GetTranscribeTask(_current.Id);
        if (task is null)
        {
            TranscribeStatus.Text = "未找到文字文件，可点击「语音转文字」重试";
            HideProgress();
            return;
        }
        switch (task.Value.Status)
        {
            case "pending":
                TranscribeStatus.Text = "已加入识别队列，等待执行…";
                HideProgress();
                break;
            case "running":
                TranscribeStatus.Text = "正在识别语音…";
                TranscribeProgress.Visibility = Visibility.Visible;
                TranscribeProgress.IsIndeterminate = true;
                break;
            case "error":
                TranscribeStatus.Text = "识别失败：" + task.Value.LastError;
                HideProgress();
                break;
            default:
                TranscribeStatus.Text = "未找到文字文件，可点击「语音转文字」重试";
                HideProgress();
                break;
        }
    }

    private void HideProgress()
    {
        TranscribeProgress.Visibility = Visibility.Collapsed;
        TranscribeProgress.IsIndeterminate = false;
        TranscribeProgress.Value = 0;
    }

    private void UpdatePanelState()
    {
        var hasClip = _current is not null;
        var status = "";
        if (_current is not null)
        {
            var task = AppServices.AutoClip.GetTranscribeTask(_current.Id);
            if (task is not null)
            {
                status = task.Value.Status;
            }
        }
        var queued = status is "pending" or "running";
        FileNameBox.IsEnabled = hasClip && !_exporting;
        ExportButton.IsEnabled = hasClip && !_exporting && FileNameBox.Text.Trim().Length > 0;
        TranscribeButton.IsEnabled = hasClip && !_exporting && !queued;
        BusyRing.IsActive = _exporting || status == "running";

        var framesReady = hasClip && !_framesLoading && !_coverBusy &&
                          _selectedFrame >= 0 && _selectedFrame < _frames.Length;
        CoverButton.IsEnabled = framesReady;
        RerollButton.IsEnabled = framesReady && _coverExists;
        CoverRing.IsActive = _framesLoading || _coverBusy;
    }

    private void FileNameBox_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdatePanelState();

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null || _exporting)
        {
            return;
        }
        var row = _current;
        var name = FileNameBox.Text.Trim().TrimEnd(' ', '.');
        if (name.Length == 0)
        {
            ShowInfo(InfoBarSeverity.Warning, "请输入文件名");
            return;
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('\\') || name.Contains('/'))
        {
            ShowInfo(InfoBarSeverity.Error, "文件名包含非法字符");
            return;
        }

        var dir = Path.GetDirectoryName(row.FilePath) ?? "";
        var ext = Path.GetExtension(row.FilePath);
        var oldName = Path.GetFileNameWithoutExtension(row.FilePath);
        if (string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase))
        {
            ShowInfo(InfoBarSeverity.Informational, "文件名没有变化，无需导出");
            return;
        }
        if (!File.Exists(row.FilePath))
        {
            ShowInfo(InfoBarSeverity.Error, "原切片文件不存在：" + row.FilePath);
            return;
        }

        var newPath = Path.Combine(dir, name + ext);
        if (File.Exists(newPath))
        {
            ShowInfo(InfoBarSeverity.Error, $"目标文件已存在：{name}{ext}");
            return;
        }

        _exporting = true;
        UpdatePanelState();
        try
        {
            try
            {
                await Task.Run(() => File.Copy(row.FilePath, newPath, overwrite: false));
            }
            catch (Exception ex)
            {
                ShowInfo(InfoBarSeverity.Error, "复制切片失败：" + ex.Message);
                return;
            }

            try
            {
                File.Delete(row.FilePath);
            }
            catch (Exception ex)
            {
                try
                {
                    File.Delete(newPath);
                }
                catch
                {
                    // 撤销失败时保留副本，提示用户手动处理
                }
                ShowInfo(InfoBarSeverity.Error, "复制成功但删除原切片失败，已撤销：" + ex.Message);
                return;
            }

            var dbError = "";
            try
            {
                AppServices.Clips.UpdateFilePath(row.Id, newPath);
            }
            catch (Exception ex)
            {
                dbError = "；数据库路径更新失败：" + ex.Message;
            }

            var txtNote = "";
            var oldTxt = WhisperService.TranscriptPathFor(row.FilePath);
            var newTxt = WhisperService.TranscriptPathFor(newPath);
            if (File.Exists(oldTxt))
            {
                try
                {
                    File.Move(oldTxt, newTxt);
                }
                catch (Exception ex)
                {
                    txtNote = "；文字文件改名失败：" + ex.Message;
                }
            }

            var coverNote = "";
            var (oldH, oldV) = CoverService.PathsFor(row.FilePath);
            var (newH, newV) = CoverService.PathsFor(newPath);
            foreach (var (oldCover, newCover) in new[] { (oldH, newH), (oldV, newV) })
            {
                if (!File.Exists(oldCover))
                {
                    continue;
                }
                try
                {
                    File.Move(oldCover, newCover, overwrite: true);
                }
                catch (Exception ex)
                {
                    coverNote = "；封面图片改名失败：" + ex.Message;
                }
            }

            FileNameBox.Text = name;
            LoadData();
            SyncTranscriptState();
            ShowInfo(dbError.Length > 0 || txtNote.Length > 0 || coverNote.Length > 0
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success,
                $"已导出为「{name}{ext}」，原切片已删除{txtNote}{coverNote}{dbError}");
        }
        finally
        {
            _exporting = false;
            UpdatePanelState();
        }
    }

    private void TranscriptToggle_Click(object sender, RoutedEventArgs e)
    {
        _transcriptExpanded = !_transcriptExpanded;
        if (_transcriptExpanded)
        {
            TranscriptBox.Height = double.NaN;
            TranscriptToggleButton.Content = "收起";
        }
        else
        {
            TranscriptBox.Height = 70;
            TranscriptToggleButton.Content = "展开";
        }
    }

    private async void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipExportRow row })
        {
            return;
        }
        if (_exporting)
        {
            ShowInfo(InfoBarSeverity.Warning, "正在导出，请稍候再删除");
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
        SyncTranscriptState();
        ShowInfo(result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
            result.Ok
                ? result.Missing
                    ? "文件不存在，已清理切片记录、文字文件、封面和识别任务"
                    : "已删除切片、文字文件、封面和相关记录"
                : "删除失败：" + result.Error);
    }

    private void Transcribe_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null || _exporting)
        {
            return;
        }
        var task = AppServices.AutoClip.GetTranscribeTask(_current.Id);
        if (task is not null)
        {
            if (task.Value.Status == "running")
            {
                TranscribeStatus.Text = "正在识别中，请稍候…";
                return;
            }
            if (task.Value.Status == "pending")
            {
                TranscribeStatus.Text = "已在队列中等待执行…";
                return;
            }
        }

        if (AppServices.AutoClip.QueueTranscribe(_current.Id, force: true))
        {
            TranscriptBox.Text = "";
            HideProgress();
            TranscribeStatus.Text = "已加入识别队列，等待执行…";
            ShowInfo(InfoBarSeverity.Informational,
                "已加入识别队列，将与自动切片任务串行执行");
        }
        else
        {
            TranscribeStatus.Text = "加入识别队列失败";
        }
        UpdatePanelState();
    }

    // ---------------- 自动封面 ----------------

    private Border FrameBoxAt(int index) => index switch
    {
        0 => Frame0Box,
        1 => Frame1Box,
        2 => Frame2Box,
        _ => Frame3Box,
    };

    private Image FrameImageAt(int index) => index switch
    {
        0 => FrameImage0,
        1 => FrameImage1,
        2 => FrameImage2,
        _ => FrameImage3,
    };

    private async Task LoadFramesAsync(ClipExportRow row)
    {
        _frameCts?.Cancel();
        _frameCts?.Dispose();
        var cts = new CancellationTokenSource();
        _frameCts = cts;
        var token = ++_coverToken;

        _frames = Array.Empty<CoverFrame>();
        _selectedFrame = -1;
        _coverExists = false;
        _framesLoading = true;
        for (var i = 0; i < 4; i++)
        {
            FrameImageAt(i).Source = null;
        }
        UpdateCoverSelection();
        CoverPreviewH.Source = null;
        CoverPreviewV.Source = null;
        CoverTextBox.Text = Path.GetFileNameWithoutExtension(row.FilePath);
        CoverStatus.Text = "正在截取视频帧…";
        UpdatePanelState();

        var (hPath, vPath) = CoverService.PathsFor(row.FilePath);
        if (File.Exists(hPath) && File.Exists(vPath))
        {
            _coverExists = true;
            try
            {
                CoverPreviewH.Source = await LoadImageAsync(hPath);
                CoverPreviewV.Source = await LoadImageAsync(vPath);
            }
            catch
            {
                // 已有封面预览读取失败不影响重新生成
            }
        }

        try
        {
            var frames = await AppServices.Cover.ExtractFramesAsync(
                row.FilePath, row.DurationSeconds, cts.Token);
            if (token != _coverToken)
            {
                return;
            }
            _frames = frames.ToArray();
            for (var i = 0; i < _frames.Length && i < 4; i++)
            {
                FrameImageAt(i).Source = await LoadImageAsync(_frames[i].Path);
                if (token != _coverToken)
                {
                    return;
                }
            }
            _selectedFrame = 0;
            CoverStatus.Text = _coverExists
                ? "已截取 4 帧；已有封面，可换一换重新生成"
                : "已截取 4 帧，默认选中第 1 帧";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (token == _coverToken)
            {
                CoverStatus.Text = "截取视频帧失败：" + ex.Message;
            }
        }
        finally
        {
            if (token == _coverToken)
            {
                _framesLoading = false;
                UpdateCoverSelection();
                UpdatePanelState();
            }
        }
    }

    private void UpdateCoverSelection()
    {
        for (var i = 0; i < 4; i++)
        {
            var box = FrameBoxAt(i);
            box.BorderBrush = i == _selectedFrame ? AccentBorder : NormalBorder;
            box.BorderThickness = new Thickness(i == _selectedFrame ? 2 : 1);
        }
    }

    private async void Frame_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_coverBusy || sender is not FrameworkElement fe ||
            fe.Tag is not string tag || !int.TryParse(tag, out var index))
        {
            return;
        }
        if (index < 0 || index >= _frames.Length || index == _selectedFrame)
        {
            return;
        }
        _selectedFrame = index;
        UpdateCoverSelection();
        if (_coverExists)
        {
            await GenerateCoverAsync();
        }
    }

    private async void Cover_Click(object sender, RoutedEventArgs e) =>
        await GenerateCoverAsync();

    private async void Reroll_Click(object sender, RoutedEventArgs e) =>
        await GenerateCoverAsync(reroll: true);

    private async Task GenerateCoverAsync(bool reroll = false)
    {
        if (_current is null || _coverBusy ||
            _selectedFrame < 0 || _selectedFrame >= _frames.Length)
        {
            return;
        }
        var row = _current;
        var token = _coverToken;
        var lines = CoverTextBox.Text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Take(10)
            .ToArray();
        if (lines.Length == 0)
        {
            CoverStatus.Text = "请先输入封面文字";
            ShowInfo(InfoBarSeverity.Warning, "请先输入封面文字");
            return;
        }

        var (hPath, vPath) = CoverService.PathsFor(row.FilePath);
        _coverBusy = true;
        CoverStatus.Text = reroll ? "正在重新随机生成封面…" : "正在生成封面…";
        UpdatePanelState();
        try
        {
            var result = await Task.Run(() =>
                AppServices.Cover.RenderCovers(_frames[_selectedFrame].Path, lines, hPath, vPath));
            if (token != _coverToken)
            {
                return;
            }
            _coverExists = true;
            CoverPreviewH.Source = await LoadImageAsync(result.HorizontalPath);
            CoverPreviewV.Source = await LoadImageAsync(result.VerticalPath);
            CoverStatus.Text = $"已生成横版/竖版封面（字体：{result.FontName}）";
        }
        catch (Exception ex)
        {
            if (token == _coverToken)
            {
                CoverStatus.Text = "生成封面失败：" + ex.Message;
                ShowInfo(InfoBarSeverity.Error, "生成封面失败：" + ex.Message);
            }
        }
        finally
        {
            _coverBusy = false;
            UpdatePanelState();
        }
    }

    private static async Task<BitmapImage> LoadImageAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}
