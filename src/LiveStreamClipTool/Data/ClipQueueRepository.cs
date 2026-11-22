// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

namespace LiveStreamClipTool.Data;

public sealed record QueueTask(
    long Id, string TaskType, long? RecordingId, long? ClipId);

/// <summary>
/// 自动切片与语音转文字共用的串行任务队列。
/// task_type=slice 时只带 recording_id，task_type=transcribe 时只带 clip_id。
/// </summary>
public sealed class ClipQueueRepository
{
    private readonly Database _db;

    public ClipQueueRepository(Database db) => _db = db;

    /// <summary>同一段录播只入队一次；失败过的任务再次入队时重置为 pending。</summary>
    public void EnqueueSlice(long recordingId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        var now = RepoUtil.Fmt(DateTime.Now);
        cmd.CommandText = """
            INSERT OR IGNORE INTO clip_queue
                (task_type, recording_id, status, attempts, last_error, created_at, updated_at)
            VALUES ('slice', @rid, 'pending', 0, '', @now, @now);

            UPDATE clip_queue
            SET status = 'pending', last_error = '', updated_at = @now
            WHERE task_type = 'slice' AND recording_id = @rid AND status = 'error';
            """;
        cmd.Parameters.AddWithValue("@rid", recordingId);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>同一片切只保留一个识别任务；已存在时不重复入队。</summary>
    public bool EnqueueTranscribe(long clipId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        var now = RepoUtil.Fmt(DateTime.Now);
        cmd.CommandText = """
            INSERT OR IGNORE INTO clip_queue
                (task_type, clip_id, status, attempts, last_error, created_at, updated_at)
            VALUES ('transcribe', @cid, 'pending', 0, '', @now, @now);
            """;
        cmd.Parameters.AddWithValue("@cid", clipId);
        cmd.Parameters.AddWithValue("@now", now);
        return cmd.ExecuteNonQuery() > 0;
    }

    public QueueTask? GetNextPending()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, task_type, recording_id, clip_id FROM clip_queue
            WHERE status = 'pending' ORDER BY id ASC LIMIT 1;
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        return new QueueTask(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3));
    }

    public (long Id, string Status, string LastError)? FindByClip(long clipId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, status, last_error FROM clip_queue
            WHERE task_type = 'transcribe' AND clip_id = @cid
            ORDER BY id DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@cid", clipId);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? (reader.GetInt64(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    public void DeleteByClip(long clipId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM clip_queue WHERE task_type = 'transcribe' AND clip_id = @cid;";
        cmd.Parameters.AddWithValue("@cid", clipId);
        cmd.ExecuteNonQuery();
    }

    public void MarkRunning(long id) => SetStatus(id, "running", null, bumpAttempts: false);

    public void MarkDone(long id) => SetStatus(id, "done", null, bumpAttempts: false);

    public void MarkError(long id, string error) =>
        SetStatus(id, "error", error, bumpAttempts: true);

    /// <summary>程序异常退出时 running 任务没有机会收尾，下次启动重新排队。</summary>
    public void ResetRunning()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE clip_queue SET status = 'pending', updated_at = @now
            WHERE status = 'running';
            """;
        cmd.Parameters.AddWithValue("@now", RepoUtil.Fmt(DateTime.Now));
        cmd.ExecuteNonQuery();
    }

    public bool HasPending()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM clip_queue WHERE status = 'pending' LIMIT 1;";
        return cmd.ExecuteScalar() is not null;
    }

    private void SetStatus(long id, string status, string? error, bool bumpAttempts)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = bumpAttempts
            ? """
              UPDATE clip_queue
              SET status = @status, attempts = attempts + 1, last_error = @err, updated_at = @now
              WHERE id = @id;
              """
            : """
              UPDATE clip_queue
              SET status = @status, updated_at = @now
              WHERE id = @id;
              """;
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@err", error ?? "");
        cmd.Parameters.AddWithValue("@now", RepoUtil.Fmt(DateTime.Now));
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }
}
