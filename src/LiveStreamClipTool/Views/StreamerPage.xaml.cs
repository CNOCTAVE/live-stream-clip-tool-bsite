// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using System.Collections.ObjectModel;
using LiveStreamClipTool.Dialogs;
using LiveStreamClipTool.Models;
using LiveStreamClipTool.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LiveStreamClipTool.Views;

public sealed partial class StreamerPage : Page
{
    private List<StreamerRow> _allRows = new();
    private string? _sortField;
    private bool _sortDesc;
    private string _search = "";
    private bool _suppressSelectAll;
    private bool _suppressRowEvents;

    public ObservableCollection<StreamerRow> Rows { get; } = new();

    public StreamerPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadData();
    }

    private void LoadData()
    {
        _allRows = AppServices.Streamers.GetAll().Select(Map).ToList();
        RebuildView();
    }

    private static StreamerRow Map(Streamer s) => new()
    {
        Id = s.Id,
        Uid = s.Uid,
        Nickname = s.Nickname,
        RoomId = s.RoomId,
        RoomTitle = s.RoomTitle,
        UpdatedAt = s.UpdatedAt,
        CreatedAt = s.CreatedAt,
        UpdatedText = s.UpdatedAt.ToString("yyyy-MM-dd HH:mm"),
        CreatedText = s.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
        SortOrder = s.SortOrder
    };

    private bool IsManualMode() => _sortField is null && _search.Length == 0;

    private void RebuildView()
    {
        IEnumerable<StreamerRow> view = _allRows;
        if (_search.Length > 0)
        {
            view = view.Where(r =>
                r.Nickname.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                r.Uid.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                r.RoomId.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                r.RoomTitle.Contains(_search, StringComparison.OrdinalIgnoreCase));
        }
        if (_sortField is not null)
        {
            view = ApplySort(view, _sortField, _sortDesc);
        }

        Rows.Clear();
        foreach (var row in view)
        {
            Rows.Add(row);
        }

        UpdateReorderMode();
        UpdateHeaderLabels();
        RefreshSelectAllState();
    }

    private static IEnumerable<StreamerRow> ApplySort(
        IEnumerable<StreamerRow> rows, string field, bool desc)
    {
        static long Num(string s) => long.TryParse(s, out var v) ? v : 0;
        return field switch
        {
            "nickname" => desc
                ? rows.OrderByDescending(r => r.Nickname, StringComparer.CurrentCulture)
                : rows.OrderBy(r => r.Nickname, StringComparer.CurrentCulture),
            "uid" => desc
                ? rows.OrderByDescending(r => Num(r.Uid)).ThenBy(r => r.Id)
                : rows.OrderBy(r => Num(r.Uid)).ThenBy(r => r.Id),
            "room" => desc
                ? rows.OrderByDescending(r => Num(r.RoomId)).ThenBy(r => r.Id)
                : rows.OrderBy(r => Num(r.RoomId)).ThenBy(r => r.Id),
            "title" => desc
                ? rows.OrderByDescending(r => r.RoomTitle, StringComparer.CurrentCulture)
                : rows.OrderBy(r => r.RoomTitle, StringComparer.CurrentCulture),
            "updated" => desc
                ? rows.OrderByDescending(r => r.UpdatedAt)
                : rows.OrderBy(r => r.UpdatedAt),
            "created" => desc
                ? rows.OrderByDescending(r => r.CreatedAt)
                : rows.OrderBy(r => r.CreatedAt),
            _ => rows
        };
    }

    private void UpdateReorderMode()
    {
        var manual = IsManualMode();
        StreamerList.CanReorderItems = manual;
        StreamerList.ReorderMode = manual
            ? ListViewReorderMode.Enabled
            : ListViewReorderMode.Disabled;
        StreamerList.AllowDrop = manual;
        foreach (var row in _allRows)
        {
            row.DragHandleVisibility = manual ? Visibility.Visible : Visibility.Collapsed;
        }
        RestoreOrderButton.Visibility = _sortField is null ? Visibility.Collapsed : Visibility.Visible;
        SortHint.Text = _sortField is null
            ? (_search.Length > 0 ? "搜索状态下不支持拖动排序" : "")
            : "临时排序中：不会修改数据库中的顺序字段";
    }

    private void UpdateHeaderLabels()
    {
        SetHeader(NickHeader, "主播昵称", "nickname");
        SetHeader(UidHeader, "主播 UID", "uid");
        SetHeader(RoomHeader, "直播间ID", "room");
        SetHeader(TitleHeader, "直播间标题", "title");
        SetHeader(UpdatedHeader, "更新时间", "updated");
        SetHeader(CreatedHeader, "首次添加时间", "created");
    }

    private void SetHeader(Button button, string title, string field)
    {
        button.Content = _sortField == field
            ? title + (_sortDesc ? " ▼" : " ▲")
            : title;
    }

    private void RefreshSelectAllState()
    {
        _suppressSelectAll = true;
        var visible = Rows.ToList();
        SelectAllBox.IsChecked = visible.Count > 0 && visible.All(r => r.IsSelected);
        _suppressSelectAll = false;
    }

    private void ShowInfo(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        PageInfo.Severity = severity;
        PageInfo.Message = message;
        PageInfo.IsOpen = true;
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string field })
        {
            return;
        }
        if (_sortField == field)
        {
            _sortDesc = !_sortDesc;
        }
        else
        {
            _sortField = field;
            _sortDesc = false;
        }
        RebuildView();
    }

    private void RestoreOrder_Click(object sender, RoutedEventArgs e)
    {
        _sortField = null;
        _sortDesc = false;
        RebuildView();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text.Trim();
        RebuildView();
    }

    private void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectAll)
        {
            return;
        }
        var select = SelectAllBox.IsChecked == true;
        _suppressRowEvents = true;
        foreach (var row in Rows)
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

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        if (button is not null)
        {
            button.IsEnabled = false;
        }
        BusyRing.IsActive = true;
        var ok = 0;
        var failed = 0;
        var errors = new List<string>();
        try
        {
            var list = AppServices.Streamers.GetAll();
            if (list.Count == 0)
            {
                ShowInfo("暂无主播可刷新");
                return;
            }
            for (var i = 0; i < list.Count; i++)
            {
                SortHint.Text = $"正在刷新线上信息 {i + 1}/{list.Count}…";
                var s = list[i];
                try
                {
                    var probe = await AppServices.Bili.ProbeAsync(s.Uid);
                    AppServices.Streamers.UpdateInfo(s.Id, probe.Nickname, probe.RoomId,
                        probe.RoomTitle, probe.FaceUrl);
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    if (errors.Count < 3)
                    {
                        errors.Add($"{s.Nickname}：{ex.Message}");
                    }
                }
            }
            LoadData();
            ShowInfo($"已从 B 站刷新 {ok} 位主播" +
                     (failed > 0 ? $"，{failed} 位失败：{string.Join("；", errors)}" : ""),
                failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo("刷新失败：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            BusyRing.IsActive = false;
            if (button is not null)
            {
                button.IsEnabled = true;
            }
            if (SortHint.Text.StartsWith("正在刷新", StringComparison.Ordinal))
            {
                UpdateReorderMode();
            }
        }
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddStreamerDialog { XamlRoot = XamlRoot };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || dialog.Probe is null)
        {
            return;
        }

        var probe = dialog.Probe;
        var existing = AppServices.Streamers.FindByUid(probe.Uid);
        if (existing is not null)
        {
            AppServices.Streamers.UpdateInfo(existing.Id, probe.Nickname, probe.RoomId,
                probe.RoomTitle, probe.FaceUrl);
            ShowInfo($"主播「{probe.Nickname}」已存在，已更新信息", InfoBarSeverity.Success);
        }
        else
        {
            AppServices.Streamers.Add(probe.Uid, probe.Nickname, probe.RoomId,
                probe.RoomTitle, probe.FaceUrl);
            ShowInfo($"已添加主播「{probe.Nickname}」", InfoBarSeverity.Success);
        }
        LoadData();
    }

    private async void AreaAddButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AreaStreamersDialog { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
        if (dialog.ParentAreaId <= 0 || dialog.AreaId <= 0)
        {
            return;
        }

        AreaAddButton.IsEnabled = false;
        BusyRing.IsActive = true;
        var areaLabel = $"{dialog.ParentAreaName} · {dialog.AreaName}";
        SortHint.Text = $"正在获取「{areaLabel}」的主播…";
        try
        {
            var rooms = await AppServices.Bili.GetAreaRoomsAsync(dialog.ParentAreaId, dialog.AreaId);
            if (rooms.Count == 0)
            {
                ShowInfo($"「{areaLabel}」当前没有直播中的主播");
                return;
            }

            var limited = dialog.MaxCount > 0 && rooms.Count > dialog.MaxCount;
            if (limited)
            {
                rooms = rooms.Take(dialog.MaxCount).ToList();
            }
            var limitNote = limited ? $"（按在线顺序取前 {dialog.MaxCount} 位）" : "";

            var existing = AppServices.Streamers.GetAll()
                .ToDictionary(s => s.Uid, s => s.Id, StringComparer.Ordinal);
            var added = 0;
            var updated = 0;
            for (var i = 0; i < rooms.Count; i++)
            {
                if (i > 0 && i % 100 == 0)
                {
                    SortHint.Text = $"正在写入主播 {i + 1}/{rooms.Count}…";
                }
                var room = rooms[i];
                var nickname = room.Nickname.Length > 0 ? room.Nickname : room.Uid;
                if (existing.TryGetValue(room.Uid, out var id))
                {
                    AppServices.Streamers.UpdateInfo(id, nickname, room.RoomId,
                        room.RoomTitle, room.FaceUrl);
                    updated++;
                }
                else
                {
                    AppServices.Streamers.Add(room.Uid, nickname, room.RoomId,
                        room.RoomTitle, room.FaceUrl);
                    added++;
                }
            }
            LoadData();
            ShowInfo($"已从「{areaLabel}」添加 {added} 位主播" +
                     (updated > 0 ? $"，更新 {updated} 位已有主播" : "") + limitNote,
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo("添加分区主播失败：" + ex.Message, InfoBarSeverity.Error);
            LoadData();
        }
        finally
        {
            BusyRing.IsActive = false;
            AreaAddButton.IsEnabled = true;
            UpdateReorderMode();
        }
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var rows = Rows.Where(r => r.IsSelected).ToList();
        if (rows.Count == 0)
        {
            ShowInfo("请先勾选要删除的主播", InfoBarSeverity.Warning);
            return;
        }
        await DeleteStreamersAsync(rows);
    }

    private async void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: StreamerRow row })
        {
            return;
        }
        await DeleteStreamersAsync(new List<StreamerRow> { row });
    }

    private async Task DeleteStreamersAsync(List<StreamerRow> rows)
    {
        var recording = rows.FirstOrDefault(r => AppServices.Recorder.FindByStreamer(r.Id) is not null);
        if (recording is not null)
        {
            ShowInfo($"「{recording.Nickname}」正在录制，请先停止后再删除", InfoBarSeverity.Warning);
            return;
        }

        var label = rows.Count == 1 ? $"主播「{rows[0].Nickname}」" : $"选中的 {rows.Count} 位主播";
        var confirmed = await ConfirmAsync("删除主播",
            $"确定删除{label}吗？\n" +
            "相关的录播记录、录播视频、切片视频、同名文字文件和封面都会被删除，删除后不可恢复。",
            "删除");
        if (!confirmed)
        {
            return;
        }

        var errors = new List<string>();
        var deleted = 0;
        var clipCount = 0;
        var recordingCount = 0;
        foreach (var row in rows)
        {
            try
            {
                foreach (var clip in AppServices.Clips.GetByStreamer(row.Id))
                {
                    var (_, error) = ClipDeletion.DeleteClipFiles(clip.FilePath);
                    if (error.Length > 0 && errors.Count < 3)
                    {
                        errors.Add($"{row.Nickname}：{error}");
                    }
                    clipCount++;
                }
                foreach (var rec in AppServices.Recordings.GetByStreamer(row.Id))
                {
                    try
                    {
                        if (File.Exists(rec.FilePath))
                        {
                            File.Delete(rec.FilePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (errors.Count < 3)
                        {
                            errors.Add($"{row.Nickname}：{Path.GetFileName(rec.FilePath)}：{ex.Message}");
                        }
                    }
                    recordingCount++;
                }
                // 录播、切片与队列任务由外键级联删除
                AppServices.Streamers.Delete(new[] { row.Id });
                deleted++;
            }
            catch (Exception ex)
            {
                if (errors.Count < 3)
                {
                    errors.Add($"{row.Nickname}：{ex.Message}");
                }
            }
        }

        LoadData();
        var detail = recordingCount > 0 || clipCount > 0
            ? $"（{recordingCount} 条录播、{clipCount} 个切片）"
            : "";
        ShowInfo($"已删除 {deleted} 位主播{detail}" +
                 (errors.Count > 0 ? "；部分文件删除失败：" + string.Join("；", errors) : ""),
            errors.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    private async void StreamerList_DragItemsCompleted(ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        if (!IsManualMode())
        {
            return;
        }
        _allRows = Rows.ToList();
        try
        {
            AppServices.Streamers.UpdateOrder(_allRows.Select(r => r.Id).ToList());
            ShowInfo("手动顺序已保存", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo("保存排序失败：" + ex.Message, InfoBarSeverity.Error);
            LoadData();
        }
        await Task.CompletedTask;
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
