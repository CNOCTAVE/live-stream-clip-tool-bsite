// Copyright (c) 2026 cnoctave.top
// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of LiveStreamClipTool; see LICENSE for the full license text.

using Microsoft.Data.Sqlite;

namespace LiveStreamClipTool.Data;

public sealed class Database
{
    public string DbPath { get; }

    public Database()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveStreamClipTool");
        Directory.CreateDirectory(dir);
        DbPath = Path.Combine(dir, "data.db");
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS streamers (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                uid         TEXT NOT NULL UNIQUE,
                nickname    TEXT NOT NULL DEFAULT '',
                room_id     TEXT NOT NULL DEFAULT '',
                room_title  TEXT NOT NULL DEFAULT '',
                face_url    TEXT NOT NULL DEFAULT '',
                sort_order  INTEGER NOT NULL DEFAULT 0,
                created_at  TEXT NOT NULL,
                updated_at  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS recordings (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                streamer_id   INTEGER NOT NULL,
                file_path     TEXT NOT NULL,
                started_at    TEXT NOT NULL,
                ended_at      TEXT,
                status        TEXT NOT NULL DEFAULT 'recording',
                stop_reason   TEXT NOT NULL DEFAULT '',
                duration_sec  REAL NOT NULL DEFAULT 0,
                auto_stop_sec INTEGER NOT NULL DEFAULT 0,
                created_at    TEXT NOT NULL,
                FOREIGN KEY (streamer_id) REFERENCES streamers(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS clips (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                recording_id  INTEGER,
                streamer_id   INTEGER NOT NULL,
                title         TEXT NOT NULL DEFAULT '',
                file_path     TEXT NOT NULL,
                clip_time     TEXT NOT NULL,
                duration_sec  REAL NOT NULL DEFAULT 0,
                created_at    TEXT NOT NULL,
                FOREIGN KEY (streamer_id) REFERENCES streamers(id) ON DELETE CASCADE,
                FOREIGN KEY (recording_id) REFERENCES recordings(id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS clip_queue (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                task_type     TEXT NOT NULL DEFAULT 'slice',
                recording_id  INTEGER,
                clip_id       INTEGER,
                status        TEXT NOT NULL DEFAULT 'pending',
                attempts      INTEGER NOT NULL DEFAULT 0,
                last_error    TEXT NOT NULL DEFAULT '',
                created_at    TEXT NOT NULL,
                updated_at    TEXT NOT NULL,
                FOREIGN KEY (recording_id) REFERENCES recordings(id) ON DELETE CASCADE,
                FOREIGN KEY (clip_id) REFERENCES clips(id) ON DELETE CASCADE,
                CHECK (
                    (task_type = 'slice' AND recording_id IS NOT NULL AND clip_id IS NULL)
                    OR (task_type = 'transcribe' AND clip_id IS NOT NULL AND recording_id IS NULL)
                )
            );

            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_streamers_order ON streamers(sort_order, id);
            CREATE INDEX IF NOT EXISTS idx_recordings_streamer ON recordings(streamer_id);
            CREATE INDEX IF NOT EXISTS idx_recordings_created ON recordings(id DESC);
            CREATE INDEX IF NOT EXISTS idx_clips_time ON clips(clip_time DESC);
            CREATE INDEX IF NOT EXISTS idx_clip_queue_status ON clip_queue(status, id);
            """;
        cmd.ExecuteNonQuery();

        // 检测判据改为「按响度 + 停顿分段」后，50 秒的旧默认值会把整段录播并成 1 段。
        // 仅在首次升级时把未被用户改过的旧默认值 50 调整为 10。
        using var mig = conn.CreateCommand();
        mig.CommandText = """
            INSERT INTO settings (key, value)
            SELECT 'clip_algo_version', '0'
            WHERE NOT EXISTS (SELECT 1 FROM settings WHERE key = 'clip_algo_version');

            UPDATE settings SET value = '10'
            WHERE key = 'merge_gap_seconds' AND value = '50'
              AND (SELECT value FROM settings WHERE key = 'clip_algo_version') = '0';

            UPDATE settings SET value = '1' WHERE key = 'clip_algo_version';
            """;
        mig.ExecuteNonQuery();

        // 队列升级：旧表只有切片任务。迁移为「切片 / 语音转文字」共用队列，保留未完成与历史行。
        var hasTaskType = false;
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(clip_queue);";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "task_type", StringComparison.OrdinalIgnoreCase))
                {
                    hasTaskType = true;
                    break;
                }
            }
        }

        if (!hasTaskType)
        {
            using var queueMig = conn.CreateCommand();
            queueMig.CommandText = """
                ALTER TABLE clip_queue RENAME TO clip_queue_legacy;

                CREATE TABLE clip_queue (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    task_type     TEXT NOT NULL DEFAULT 'slice',
                    recording_id  INTEGER,
                    clip_id       INTEGER,
                    status        TEXT NOT NULL DEFAULT 'pending',
                    attempts      INTEGER NOT NULL DEFAULT 0,
                    last_error    TEXT NOT NULL DEFAULT '',
                    created_at    TEXT NOT NULL,
                    updated_at    TEXT NOT NULL,
                    FOREIGN KEY (recording_id) REFERENCES recordings(id) ON DELETE CASCADE,
                    FOREIGN KEY (clip_id) REFERENCES clips(id) ON DELETE CASCADE,
                    CHECK (
                        (task_type = 'slice' AND recording_id IS NOT NULL AND clip_id IS NULL)
                        OR (task_type = 'transcribe' AND clip_id IS NOT NULL AND recording_id IS NULL)
                    )
                );

                INSERT INTO clip_queue
                    (task_type, recording_id, status, attempts, last_error, created_at, updated_at)
                SELECT 'slice', recording_id, status, attempts, last_error, created_at, updated_at
                FROM clip_queue_legacy;

                DROP TABLE clip_queue_legacy;
                """;
            queueMig.ExecuteNonQuery();
        }

        using (var queueIdx = conn.CreateCommand())
        {
            queueIdx.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_clip_queue_status ON clip_queue(status, id);
                CREATE UNIQUE INDEX IF NOT EXISTS idx_clip_queue_recording
                    ON clip_queue(recording_id) WHERE recording_id IS NOT NULL;
                CREATE UNIQUE INDEX IF NOT EXISTS idx_clip_queue_clip
                    ON clip_queue(clip_id) WHERE clip_id IS NOT NULL;
                """;
            queueIdx.ExecuteNonQuery();
        }
    }
}
