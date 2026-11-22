// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using LiveStreamClipTool.Views;

namespace LiveStreamClipTool;

public sealed partial class MainWindow : Window
{
    private const uint WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const uint LrDefaultSize = 0x0040;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hinst, string name,
        uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 840));
        Title = "批量切片工具（B站）";
        ApplyWindowIcons();

        try
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        }
        catch
        {
            // Mica 不可用时保持默认背景
        }

        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void ApplyWindowIcons()
    {
        var branding = Path.Combine(AppContext.BaseDirectory, "Assets", "Branding");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var big = LoadImage(IntPtr.Zero, Path.Combine(branding, "app.ico"),
            ImageIcon, 0, 0, LrLoadFromFile | LrDefaultSize);
        var small = LoadImage(IntPtr.Zero, Path.Combine(branding, "favicon.ico"),
            ImageIcon, 16, 16, LrLoadFromFile);
        if (big != IntPtr.Zero)
        {
            SendMessage(hwnd, WmSetIcon, IconBig, big);
        }
        if (small != IntPtr.Zero)
        {
            SendMessage(hwnd, WmSetIcon, IconSmall, small);
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        Type pageType = tag switch
        {
            "streamers" => typeof(StreamerPage),
            "recording" => typeof(RecordingPage),
            "clips" => typeof(ClipPage),
            "clip-export" => typeof(ClipExportPage),
            "dependencies" => typeof(DependencyPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(StreamerPage)
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
        }
    }
}
