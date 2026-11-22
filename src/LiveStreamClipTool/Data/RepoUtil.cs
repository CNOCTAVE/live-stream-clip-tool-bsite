// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

namespace LiveStreamClipTool.Data;

internal static class RepoUtil
{
    public const string DateFormat = "yyyy-MM-dd HH:mm:ss";

    public static string Fmt(DateTime value) => value.ToString(DateFormat);

    public static DateTime Parse(string? value) =>
        DateTime.TryParse(value, out var dt) ? dt : DateTime.MinValue;
}
