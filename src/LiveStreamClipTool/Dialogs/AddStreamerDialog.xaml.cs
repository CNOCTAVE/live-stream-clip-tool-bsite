// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using LiveStreamClipTool.Models;
using LiveStreamClipTool.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace LiveStreamClipTool.Dialogs;

public sealed partial class AddStreamerDialog : ContentDialog
{
    public AddStreamerDialog()
    {
        InitializeComponent();
    }

    public StreamerProbe? Probe { get; private set; }

    private async void ProbeButton_Click(object sender, RoutedEventArgs e)
    {
        await ProbeAsync();
    }

    private async void UidBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            await ProbeAsync();
        }
    }

    private async Task ProbeAsync()
    {
        ErrorInfo.IsOpen = false;
        ResultPanel.Visibility = Visibility.Collapsed;
        ProbeButton.IsEnabled = false;
        IsPrimaryButtonEnabled = false;
        ProbeRing.IsActive = true;
        Probe = null;
        try
        {
            var probe = await AppServices.Bili.ProbeAsync(UidBox.Text);
            Probe = probe;
            NameText.Text = probe.Nickname;
            RoomText.Text = $"UID {probe.Uid} · 直播间 {probe.RoomId}";
            TitleText.Text = string.IsNullOrWhiteSpace(probe.RoomTitle)
                ? "（暂无直播间标题）"
                : "直播间标题：" + probe.RoomTitle;
            LiveText.Text = probe.Living ? "当前状态：直播中" : "当前状态：未开播";
            ResultPanel.Visibility = Visibility.Visible;
            IsPrimaryButtonEnabled = true;
        }
        catch (Exception ex)
        {
            ErrorInfo.Message = ex.Message;
            ErrorInfo.IsOpen = true;
        }
        finally
        {
            ProbeButton.IsEnabled = true;
            ProbeRing.IsActive = false;
        }
    }
}
