// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using LiveStreamClipTool.Models;
using Microsoft.Data.Sqlite;

namespace LiveStreamClipTool.Data;

public sealed class ClipRepository
{
    private readonly Database _db;

    public ClipRepository(Database db) => _db = db;

    public long Insert(Clip clip)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO clips (recording_id, streamer_id, title, file_path,
                               clip_time, duration_sec, created_at)
            VALUES (@rid, @sid, @title, @file, @time, @duration, @created);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@rid", (object?)clip.RecordingId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sid", clip.StreamerId);
        cmd.Parameters.AddWithValue("@title", clip.Title);
        cmd.Parameters.AddWithValue("@file", clip.FilePath);
        cmd.Parameters.AddWithValue("@time", RepoUtil.Fmt(clip.ClipTime));
        cmd.Parameters.AddWithValue("@duration", clip.DurationSeconds);
        cmd.Parameters.AddWithValue("@created", RepoUtil.Fmt(DateTime.Now));
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>按时间倒序分页；nickname 为部分匹配的主播昵称过滤，空表示全部。</summary>
    public (List<Clip> Items, int Total) GetPage(int offset, int limit, string? nickname)
    {
        var hasFilter = !string.IsNullOrWhiteSpace(nickname);
        var where = hasFilter ? "WHERE s.nickname LIKE @like ESCAPE '\\'" : "";
        using var conn = _db.Open();

        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"""
            SELECT COUNT(*)
            FROM clips c
            LEFT JOIN streamers s ON s.id = c.streamer_id
            {where};
            """;
        if (hasFilter)
        {
            countCmd.Parameters.AddWithValue("@like", LikePattern(nickname!));
        }
        var total = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT c.id, c.recording_id, c.streamer_id, c.title, c.file_path,
                   c.clip_time, c.duration_sec, c.created_at,
                   COALESCE(s.nickname, ''), COALESCE(s.room_id, '')
            FROM clips c
            LEFT JOIN streamers s ON s.id = c.streamer_id
            {where}
            ORDER BY c.clip_time DESC, c.id DESC
            LIMIT @limit OFFSET @offset;
            """;
        if (hasFilter)
        {
            cmd.Parameters.AddWithValue("@like", LikePattern(nickname!));
        }
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 500));
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, offset));
        using var reader = cmd.ExecuteReader();
        var list = new List<Clip>();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }
        return (list, total);
    }

    /// <summary>切片中出现过的主播昵称，供筛选下拉框使用。</summary>
    public List<string> GetStreamerNicknames()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT s.nickname
            FROM clips c
            JOIN streamers s ON s.id = c.streamer_id
            WHERE TRIM(s.nickname) <> ''
            ORDER BY s.nickname COLLATE NOCASE;
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    private static string LikePattern(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        return $"%{escaped}%";
    }

    public List<Clip> GetByStreamer(long streamerId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.id, c.recording_id, c.streamer_id, c.title, c.file_path,
                   c.clip_time, c.duration_sec, c.created_at,
                   COALESCE(s.nickname, ''), COALESCE(s.room_id, '')
            FROM clips c
            LEFT JOIN streamers s ON s.id = c.streamer_id
            WHERE c.streamer_id = @sid
            ORDER BY c.clip_time DESC, c.id DESC;
            """;
        cmd.Parameters.AddWithValue("@sid", streamerId);
        using var reader = cmd.ExecuteReader();
        var list = new List<Clip>();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }
        return list;
    }

    public Clip? GetById(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.id, c.recording_id, c.streamer_id, c.title, c.file_path,
                   c.clip_time, c.duration_sec, c.created_at,
                   COALESCE(s.nickname, ''), COALESCE(s.room_id, '')
            FROM clips c
            LEFT JOIN streamers s ON s.id = c.streamer_id
            WHERE c.id = @id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static Clip Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        RecordingId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
        StreamerId = reader.GetInt64(2),
        Title = reader.GetString(3),
        FilePath = reader.GetString(4),
        ClipTime = RepoUtil.Parse(reader.GetString(5)),
        DurationSeconds = reader.GetDouble(6),
        CreatedAt = RepoUtil.Parse(reader.GetString(7)),
        StreamerNickname = reader.GetString(8),
        RoomId = reader.GetString(9)
    };

    public int Count()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM clips;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public void Delete(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM clips WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void UpdateFilePath(long id, string filePath)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE clips SET file_path = @file WHERE id = @id;";
        cmd.Parameters.AddWithValue("@file", filePath);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }
}
