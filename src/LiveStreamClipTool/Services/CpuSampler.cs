// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using System.Runtime.InteropServices;

namespace LiveStreamClipTool.Services;

public readonly record struct CpuUsage(double TotalPercent, double MaxCorePercent);

/// <summary>通过 NtQuerySystemInformation 读取每个逻辑核的忙闲时间，计算总占用与最高单核占用。</summary>
public static class CpuSampler
{
    private const int SystemProcessorPerformanceInformation = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPerformanceInformation
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int infoClass, IntPtr info, int length, out int returnLength);

    public static async Task<CpuUsage> SampleAsync(TimeSpan? window = null)
    {
        var before = Snapshot();
        if (before.Length == 0)
        {
            return new CpuUsage(-1, -1);
        }
        await Task.Delay(window ?? TimeSpan.FromMilliseconds(800));
        var after = Snapshot();
        var n = Math.Min(before.Length, after.Length);
        if (n == 0)
        {
            return new CpuUsage(-1, -1);
        }

        double busySum = 0;
        double allSum = 0;
        double maxCore = 0;
        for (var i = 0; i < n; i++)
        {
            var idle = after[i].IdleTime - before[i].IdleTime;
            var kernel = after[i].KernelTime - before[i].KernelTime;
            var user = after[i].UserTime - before[i].UserTime;
            // KernelTime 包含 IdleTime
            var busy = kernel - idle + user;
            var all = kernel + user;
            if (all <= 0)
            {
                continue;
            }
            var pct = Math.Clamp(busy * 100.0 / all, 0, 100);
            busySum += busy;
            allSum += all;
            if (pct > maxCore)
            {
                maxCore = pct;
            }
        }
        var total = allSum > 0 ? Math.Clamp(busySum * 100.0 / allSum, 0, 100) : 0;
        return new CpuUsage(total, maxCore);
    }

    private static ProcessorPerformanceInformation[] Snapshot()
    {
        var count = Environment.ProcessorCount;
        var size = Marshal.SizeOf<ProcessorPerformanceInformation>();
        var buffer = Marshal.AllocHGlobal(size * count);
        try
        {
            var rc = NtQuerySystemInformation(
                SystemProcessorPerformanceInformation, buffer, size * count, out _);
            if (rc != 0)
            {
                return Array.Empty<ProcessorPerformanceInformation>();
            }
            var result = new ProcessorPerformanceInformation[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = Marshal.PtrToStructure<ProcessorPerformanceInformation>(
                    buffer + i * size);
            }
            return result;
        }
        catch
        {
            return Array.Empty<ProcessorPerformanceInformation>();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
