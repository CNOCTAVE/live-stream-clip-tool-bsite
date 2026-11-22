// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using LiveStreamClipTool.Models;

namespace LiveStreamClipTool.Data;

public sealed class RecordingRepository
{
    private readonly Database _db;

    public RecordingRepository(Database db) => _db = db;

    public long Insert(Recording rec)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO recordings (streamer_id, file_path, started_at, ended_at, status,
                                    stop_reason, duration_sec, auto_stop_sec, created_at)
            VALUES (@sid, @file, @started, @ended, @status, @reason, @duration, @auto, @created);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@sid", rec.StreamerId);
        cmd.Parameters.AddWithValue("@file", rec.FilePath);
        cmd.Parameters.AddWithValue("@started", RepoUtil.Fmt(rec.StartedAt));
        cmd.Parameters.AddWithValue("@ended",
            rec.EndedAt is null ? DBNull.Value : RepoUtil.Fmt(rec.EndedAt.Value));
        cmd.Parameters.AddWithValue("@status", rec.Status);
        cmd.Parameters.AddWithValue("@reason", rec.StopReason);
        cmd.Parameters.AddWithValue("@duration", rec.DurationSeconds);
        cmd.Parameters.AddWithValue("@auto", rec.AutoStopSeconds);
        cmd.Parameters.AddWithValue("@created", RepoUtil.Fmt(DateTime.Now));
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public bool FilePathExists(string filePath)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM recordings WHERE file_path = @file;";
        cmd.Parameters.AddWithValue("@file", filePath);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    public List<Recording> GetRecent(int limit = 500)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.streamer_id, r.file_path, r.started_at, r.ended_at,
                   r.status, r.stop_reason, r.duration_sec, r.auto_stop_sec,
                   COALESCE(s.nickname, ''), COALESCE(s.uid, ''),
                   COALESCE(s.room_id, ''), COALESCE(s.room_title, '')
            FROM recordings r
            LEFT JOIN streamers s ON s.id = r.streamer_id
            ORDER BY r.id DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<Recording>();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }
        return list;
    }

    public List<Recording> GetByStreamer(long streamerId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.streamer_id, r.file_path, r.started_at, r.ended_at,
                   r.status, r.stop_reason, r.duration_sec, r.auto_stop_sec,
                   COALESCE(s.nickname, ''), COALESCE(s.uid, ''),
                   COALESCE(s.room_id, ''), COALESCE(s.room_title, '')
            FROM recordings r
            LEFT JOIN streamers s ON s.id = r.streamer_id
            WHERE r.streamer_id = @sid
            ORDER BY r.id DESC;
            """;
        cmd.Parameters.AddWithValue("@sid", streamerId);
        using var reader = cmd.ExecuteReader();
        var list = new List<Recording>();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }
        return list;
    }

    public Recording? GetById(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.streamer_id, r.file_path, r.started_at, r.ended_at,
                   r.status, r.stop_reason, r.duration_sec, r.auto_stop_sec,
                   COALESCE(s.nickname, ''), COALESCE(s.uid, ''),
                   COALESCE(s.room_id, ''), COALESCE(s.room_title, '')
            FROM recordings r
            LEFT JOIN streamers s ON s.id = r.streamer_id
            WHERE r.id = @id LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public List<Recording> GetUnfinished()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.streamer_id, r.file_path, r.started_at, r.ended_at,
                   r.status, r.stop_reason, r.duration_sec, r.auto_stop_sec,
                   COALESCE(s.nickname, ''), COALESCE(s.uid, ''),
                   COALESCE(s.room_id, ''), COALESCE(s.room_title, '')
            FROM recordings r
            LEFT JOIN streamers s ON s.id = r.streamer_id
            WHERE r.status = 'recording'
            ORDER BY r.id DESC;
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<Recording>();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }
        return list;
    }

    public void MarkEnded(long id, string status, string reason, DateTime endedAt, double durationSeconds)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE recordings
            SET status = @status, stop_reason = @reason, ended_at = @ended, duration_sec = @duration
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@reason", reason);
        cmd.Parameters.AddWithValue("@ended", RepoUtil.Fmt(endedAt));
        cmd.Parameters.AddWithValue("@duration", durationSeconds);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void UpdateDuration(long id, double durationSeconds)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE recordings SET duration_sec = @d WHERE id = @id;";
        cmd.Parameters.AddWithValue("@d", durationSeconds);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM recordings WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    private static Recording Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        StreamerId = r.GetInt64(1),
        FilePath = r.GetString(2),
        StartedAt = RepoUtil.Parse(r.GetString(3)),
        EndedAt = r.IsDBNull(4) ? null : RepoUtil.Parse(r.GetString(4)),
        Status = r.GetString(5),
        StopReason = r.GetString(6),
        DurationSeconds = r.GetDouble(7),
        AutoStopSeconds = r.GetInt32(8),
        StreamerNickname = r.GetString(9),
        StreamerUid = r.GetString(10),
        RoomId = r.GetString(11),
        RoomTitle = r.GetString(12)
    };
}
