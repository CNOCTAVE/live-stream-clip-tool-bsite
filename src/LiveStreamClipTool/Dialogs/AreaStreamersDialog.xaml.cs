// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using LiveStreamClipTool.Models;
using LiveStreamClipTool.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LiveStreamClipTool.Dialogs;

public sealed partial class AreaStreamersDialog : ContentDialog
{
    private readonly List<AreaCategory> _categories = new();
    private CancellationTokenSource? _cts;
    private AreaCategory? _selectedParent;
    private bool _loading;

    public AreaStreamersDialog()
    {
        InitializeComponent();
        Opened += async (_, _) => await LoadAreasAsync();
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        };
    }

    public int ParentAreaId { get; private set; }
    public string ParentAreaName { get; private set; } = "";
    public int AreaId { get; private set; }
    public string AreaName { get; private set; } = "";
    public int MaxCount { get; private set; } = 10;

    private async Task LoadAreasAsync()
    {
        if (_loading)
        {
            return;
        }
        _loading = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        AreasRing.IsActive = true;
        RetryButton.Visibility = Visibility.Collapsed;
        AreaError.IsOpen = false;
        AreaStatus.Text = "正在从 B 站直播主页获取一级、二级分区…";
        ParentPanel.Children.Clear();
        SubPanel.Children.Clear();
        SubPanel.RowDefinitions.Clear();
        SubTitle.Text = "二级分区";
        _selectedParent = null;
        _categories.Clear();
        try
        {
            var categories = await AppServices.Bili.GetAreaCategoriesAsync(ct);
            if (categories.Count == 0)
            {
                throw new InvalidOperationException("没有解析到分区，请稍后重试");
            }
            _categories.AddRange(categories);
            BuildParentButtons();
            AreaStatus.Text = $"已获取 {categories.Count} 个一级分区（实时来自 B 站直播主页）";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AreaStatus.Text = "分区获取失败";
            AreaError.Message = ex.Message;
            AreaError.IsOpen = true;
            RetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            AreasRing.IsActive = false;
            _loading = false;
        }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadAreasAsync();
    }

    private void BuildParentButtons()
    {
        foreach (var category in _categories)
        {
            var button = new Button
            {
                Content = category.Name,
                Tag = category,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 5, 10, 5)
            };
            button.Click += ParentButton_Click;
            ParentPanel.Children.Add(button);
        }
    }

    private void ParentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AreaCategory category })
        {
            return;
        }
        _selectedParent = category;
        SelectSummary.Text = $"已选一级分区「{category.Name}」，请继续选择二级分区";
        BuildSubButtons(category);
    }

    private void BuildSubButtons(AreaCategory category)
    {
        SubTitle.Text = $"二级分区（{category.Name} · 共 {category.SubAreas.Count} 个）";
        SubPanel.Children.Clear();
        SubPanel.RowDefinitions.Clear();
        for (var i = 0; i < category.SubAreas.Count; i += 2)
        {
            SubPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = SubPanel.RowDefinitions.Count - 1;
            for (var col = 0; col < 2 && i + col < category.SubAreas.Count; col++)
            {
                var sub = category.SubAreas[i + col];
                var button = new Button
                {
                    Content = sub.Name,
                    Tag = sub,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(8, 4, 8, 4)
                };
                ToolTipService.SetToolTip(button, sub.Name);
                button.Click += SubButton_Click;
                Grid.SetRow(button, row);
                Grid.SetColumn(button, col);
                SubPanel.Children.Add(button);
            }
        }
    }

    private void SubButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedParent is null || sender is not Button { Tag: AreaSubCategory sub })
        {
            return;
        }
        ParentAreaId = _selectedParent.ParentId;
        ParentAreaName = _selectedParent.Name;
        AreaId = sub.Id;
        AreaName = sub.Name;
        MaxCount = ReadMaxCount();
        Hide();
    }

    private int ReadMaxCount()
    {
        var value = MaxCountBox.Value;
        if (double.IsNaN(value) || value < 0)
        {
            return 10;
        }
        return (int)Math.Floor(value);
    }
}
