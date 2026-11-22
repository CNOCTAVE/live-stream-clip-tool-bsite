// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using LiveStreamClipTool.Models;

namespace LiveStreamClipTool.Data;

public sealed class SettingsRepository
{
    private readonly Database _db;

    public SettingsRepository(Database db) => _db = db;

    public AppSettings Load()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var conn = _db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT key, value FROM settings;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                map[reader.GetString(0)] = reader.GetString(1);
            }
        }

        var s = new AppSettings();
        if (map.TryGetValue("root_dir", out var root) && !string.IsNullOrWhiteSpace(root))
        {
            s.RootDirectory = root;
        }
        s.AutoStopMinutes = GetInt(map, "auto_stop_minutes", s.AutoStopMinutes, 0, 24 * 60);
        s.AutoClipEnabled = GetBool(map, "auto_clip_enabled", s.AutoClipEnabled);
        s.MinSegmentSeconds = GetInt(map, "min_segment_seconds", s.MinSegmentSeconds, 10, 3600);
        s.MaxSegmentSeconds = GetInt(map, "max_segment_seconds", s.MaxSegmentSeconds, 60, 6 * 3600);
        s.MergeGapSeconds = GetInt(map, "merge_gap_seconds", s.MergeGapSeconds, 5, 600);
        s.CpuTotalThreshold = GetInt(map, "cpu_total_threshold", s.CpuTotalThreshold, 0, 100);
        s.CpuCoreThreshold = GetInt(map, "cpu_core_threshold", s.CpuCoreThreshold, 0, 100);
        s.RecordQn = GetInt(map, "record_qn", s.RecordQn, 80, 10000);
        s.FfmpegPath = map.TryGetValue("ffmpeg_path", out var ff) ? ff : "";
        s.PythonPath = map.TryGetValue("python_path", out var py) ? py : "";

        if (s.MaxSegmentSeconds <= s.MinSegmentSeconds)
        {
            s.MaxSegmentSeconds = s.MinSegmentSeconds + 60;
        }
        return s;
    }

    public void Save(AppSettings s)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES (@k, @v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        var k = cmd.Parameters.Add("@k", Microsoft.Data.Sqlite.SqliteType.Text);
        var v = cmd.Parameters.Add("@v", Microsoft.Data.Sqlite.SqliteType.Text);

        void Put(string key, string value)
        {
            k.Value = key;
            v.Value = value;
            cmd.ExecuteNonQuery();
        }

        Put("root_dir", s.RootDirectory);
        Put("auto_stop_minutes", s.AutoStopMinutes.ToString());
        Put("auto_clip_enabled", s.AutoClipEnabled ? "1" : "0");
        Put("min_segment_seconds", s.MinSegmentSeconds.ToString());
        Put("max_segment_seconds", s.MaxSegmentSeconds.ToString());
        Put("merge_gap_seconds", s.MergeGapSeconds.ToString());
        Put("cpu_total_threshold", s.CpuTotalThreshold.ToString());
        Put("cpu_core_threshold", s.CpuCoreThreshold.ToString());
        Put("record_qn", s.RecordQn.ToString());
        Put("ffmpeg_path", s.FfmpegPath);
        Put("python_path", s.PythonPath);
        tx.Commit();
    }

    private static int GetInt(Dictionary<string, string> map, string key, int fallback, int min, int max)
    {
        if (map.TryGetValue(key, out var raw) && int.TryParse(raw, out var v))
        {
            return Math.Clamp(v, min, max);
        }
        return fallback;
    }

    private static bool GetBool(Dictionary<string, string> map, string key, bool fallback)
    {
        if (map.TryGetValue(key, out var raw))
        {
            return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }
        return fallback;
    }
}
