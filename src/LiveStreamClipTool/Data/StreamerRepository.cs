// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using LiveStreamClipTool.Models;

namespace LiveStreamClipTool.Data;

public sealed class StreamerRepository
{
    /// <summary>导入本地视频时使用的虚拟主播，不出现在主播列表里。</summary>
    public const string LocalUid = "__local__";

    private readonly Database _db;

    public StreamerRepository(Database db) => _db = db;

    public List<Streamer> GetAll()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, uid, nickname, room_id, room_title, face_url,
                   sort_order, created_at, updated_at
            FROM streamers
            WHERE uid <> @local
            ORDER BY sort_order ASC, id ASC;
            """;
        cmd.Parameters.AddWithValue("@local", LocalUid);
        using var reader = cmd.ExecuteReader();
        var list = new List<Streamer>();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }
        return list;
    }

    public Streamer EnsureLocal()
    {
        return FindByUid(LocalUid)
               ?? Add(LocalUid, "本地视频", "", "", "");
    }

    public Streamer? FindByUid(string uid)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, uid, nickname, room_id, room_title, face_url,
                   sort_order, created_at, updated_at
            FROM streamers WHERE uid = @uid LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@uid", uid);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public Streamer? GetById(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, uid, nickname, room_id, room_title, face_url,
                   sort_order, created_at, updated_at
            FROM streamers WHERE id = @id LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public Streamer Add(string uid, string nickname, string roomId, string roomTitle, string faceUrl)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        var now = DateTime.Now;
        cmd.CommandText = """
            INSERT INTO streamers (uid, nickname, room_id, room_title, face_url,
                                   sort_order, created_at, updated_at)
            VALUES (@uid, @nickname, @room, @title, @face,
                    (SELECT COALESCE(MAX(sort_order), 0) + 1 FROM streamers),
                    @created, @updated);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@uid", uid);
        cmd.Parameters.AddWithValue("@nickname", nickname);
        cmd.Parameters.AddWithValue("@room", roomId);
        cmd.Parameters.AddWithValue("@title", roomTitle);
        cmd.Parameters.AddWithValue("@face", faceUrl);
        cmd.Parameters.AddWithValue("@created", RepoUtil.Fmt(now));
        cmd.Parameters.AddWithValue("@updated", RepoUtil.Fmt(now));
        var id = (long)(cmd.ExecuteScalar() ?? 0L);
        return new Streamer
        {
            Id = id,
            Uid = uid,
            Nickname = nickname,
            RoomId = roomId,
            RoomTitle = roomTitle,
            FaceUrl = faceUrl,
            SortOrder = id,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void UpdateInfo(long id, string nickname, string roomId, string roomTitle, string faceUrl)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE streamers
            SET nickname = @nickname, room_id = @room, room_title = @title,
                face_url = @face, updated_at = @updated
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@nickname", nickname);
        cmd.Parameters.AddWithValue("@room", roomId);
        cmd.Parameters.AddWithValue("@title", roomTitle);
        cmd.Parameters.AddWithValue("@face", faceUrl);
        cmd.Parameters.AddWithValue("@updated", RepoUtil.Fmt(DateTime.Now));
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void UpdateRoomTitle(long id, string roomTitle)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE streamers SET room_title = @title, updated_at = @updated WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@title", roomTitle);
        cmd.Parameters.AddWithValue("@updated", RepoUtil.Fmt(DateTime.Now));
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM streamers WHERE id = @id;";
        var p = cmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Integer);
        foreach (var id in ids)
        {
            p.Value = id;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void UpdateOrder(IReadOnlyList<long> orderedIds)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE streamers SET sort_order = @order WHERE id = @id;";
        var pOrder = cmd.Parameters.Add("@order", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pId = cmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Integer);
        for (var i = 0; i < orderedIds.Count; i++)
        {
            pOrder.Value = i + 1;
            pId.Value = orderedIds[i];
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static Streamer Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Uid = r.GetString(1),
        Nickname = r.GetString(2),
        RoomId = r.GetString(3),
        RoomTitle = r.GetString(4),
        FaceUrl = r.GetString(5),
        SortOrder = r.GetInt64(6),
        CreatedAt = RepoUtil.Parse(r.GetString(7)),
        UpdatedAt = RepoUtil.Parse(r.GetString(8))
    };
}
