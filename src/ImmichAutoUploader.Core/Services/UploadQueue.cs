using System.Security.Cryptography;
using ImmichAutoUploader.Core.Models;
using Microsoft.Data.Sqlite;

namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// SQLite-backed persistent upload queue.
/// <para/>
/// Deduplication is by SHA-256 content hash, so re-adding the same photo (even under
/// a different name or after an app restart) never creates a duplicate upload.
/// A separate <c>uploaded</c> table remembers every finished hash permanently.
/// </summary>
public sealed class UploadQueue : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();
    private bool _disposed;

    public static string DefaultDbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ImmichAutoUploader", "upload-queue.db");

    public UploadQueue(string dbPath)
    {
        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();

        using (var pragma = _conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            pragma.ExecuteNonQuery();
        }

        InitializeSchema();
    }

    private void InitializeSchema()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS queue (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    source_path   TEXT NOT NULL,
                    file_hash     TEXT NOT NULL,
                    file_size     INTEGER NOT NULL DEFAULT 0,
                    status        TEXT NOT NULL DEFAULT 'Pending',
                    attempts      INTEGER NOT NULL DEFAULT 0,
                    last_error    TEXT,
                    next_retry_at TEXT,
                    detected_at   TEXT NOT NULL,
                    updated_at    TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_queue_status ON queue(status);
                CREATE TABLE IF NOT EXISTS uploaded (
                    file_hash       TEXT PRIMARY KEY,
                    source_path     TEXT NOT NULL,
                    uploaded_at     TEXT NOT NULL,
                    immich_asset_id TEXT
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_queue_hash_unique ON queue(file_hash);";
            cmd.ExecuteNonQuery();
        }

        // Migration (phase 3): retry backoff timestamp. Older DBs lack the column.
        lock (_lock)
        {
            bool hasRetryColumn = false;
            using (var pragma = _conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(queue);";
                using var reader = pragma.ExecuteReader();
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), "next_retry_at", StringComparison.OrdinalIgnoreCase))
                    {
                        hasRetryColumn = true;
                        break;
                    }
                }
            }
            if (!hasRetryColumn)
            {
                using var alter = _conn.CreateCommand();
                alter.CommandText = "ALTER TABLE queue ADD COLUMN next_retry_at TEXT;";
                alter.ExecuteNonQuery();
            }

            using var idxCmd = _conn.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_queue_status_retry_detected ON queue(status, next_retry_at, detected_at);";
            idxCmd.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------------
    // Enqueue (with validation + dedup). Hashing happens outside the lock.
    // ------------------------------------------------------------------

    public async Task<EnqueueResult> TryEnqueueAsync(string sourcePath, CancellationToken ct = default)
    {
        if (FileWatcherService.IsTempFile(sourcePath))
            return EnqueueResult.SkippedTempFile;
        if (!FileWatcherService.IsMediaFile(sourcePath))
            return EnqueueResult.SkippedNotMedia;

        long size;
        try
        {
            size = new FileInfo(sourcePath).Length;
        }
        catch
        {
            return EnqueueResult.FileNotFound;
        }

        if (size == 0)
            return EnqueueResult.SkippedEmptyFile;

        string hash = await ComputeHashAsync(sourcePath, ct).ConfigureAwait(false);

        lock (_lock)
        {
            ThrowIfDisposed();
            if (ExistsInUploaded(hash))
                return EnqueueResult.AlreadyUploaded;

            using (var checkCmd = _conn.CreateCommand())
            {
                checkCmd.CommandText = "SELECT id, source_path FROM queue WHERE file_hash = $h AND status IN ('Pending','Uploading','Failed') LIMIT 1;";
                checkCmd.Parameters.AddWithValue("$h", hash);
                using var reader = checkCmd.ExecuteReader();
                if (reader.Read())
                {
                    long existingId = reader.GetInt64(0);
                    string existingPath = reader.GetString(1);
                    if (!string.Equals(existingPath, sourcePath, StringComparison.OrdinalIgnoreCase) && !File.Exists(existingPath))
                    {
                        reader.Close();
                        using var updateCmd = _conn.CreateCommand();
                        updateCmd.CommandText = "UPDATE queue SET source_path = $path, updated_at = $now WHERE id = $id;";
                        updateCmd.Parameters.AddWithValue("$path", sourcePath);
                        updateCmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                        updateCmd.Parameters.AddWithValue("$id", existingId);
                        updateCmd.ExecuteNonQuery();
                        return EnqueueResult.Enqueued;
                    }
                    return EnqueueResult.AlreadyQueued;
                }
            }

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO queue (source_path, file_hash, file_size, status, detected_at, updated_at)
                VALUES ($path, $hash, $size, 'Pending', $now, $now);";
            cmd.Parameters.AddWithValue("$path", sourcePath);
            cmd.Parameters.AddWithValue("$hash", hash);
            cmd.Parameters.AddWithValue("$size", size);
            string now = DateTime.UtcNow.ToString("o");
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
            return EnqueueResult.Enqueued;
        }
    }

    private bool ExistsInUploaded(string hash)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM uploaded WHERE file_hash = $h LIMIT 1;";
        cmd.Parameters.AddWithValue("$h", hash);
        return cmd.ExecuteScalar() is not null;
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, bufferSize: 81920, useAsync: true);
        byte[] hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    // ------------------------------------------------------------------
    // Dequeue / completion (used by the phase-3 upload engine)
    // ------------------------------------------------------------------

    public IReadOnlyList<QueuedFile> DequeueBatch(int maxCount)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            var batch = new List<QueuedFile>();
            string now = DateTime.UtcNow.ToString("o");
            using var select = _conn.CreateCommand();
            select.CommandText = @"
                SELECT id, source_path, file_hash, file_size, status, attempts, last_error, detected_at
                FROM queue
                WHERE (status = 'Pending' OR (status = 'Failed' AND next_retry_at <= $now))
                  AND (next_retry_at IS NULL OR next_retry_at <= $now)
                ORDER BY detected_at ASC LIMIT $n;";
            select.Parameters.AddWithValue("$now", now);
            select.Parameters.AddWithValue("$n", maxCount);
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                batch.Add(new QueuedFile
                {
                    Id = reader.GetInt64(0),
                    SourcePath = reader.GetString(1),
                    FileHash = reader.GetString(2),
                    FileSize = reader.GetInt64(3),
                    Status = reader.GetString(4),
                    Attempts = reader.GetInt32(5),
                    LastError = reader.IsDBNull(6) ? null : reader.GetString(6),
                    DetectedAt = DateTime.Parse(reader.GetString(7)),
                });
            }
            reader.Close();

            if (batch.Count > 0)
            {
                using var update = _conn.CreateCommand();
                update.CommandText =
                    $"UPDATE queue SET status = 'Uploading', updated_at = $now WHERE id IN ({string.Join(",", batch.Select(b => b.Id))});";
                update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                update.ExecuteNonQuery();
                foreach (var b in batch) b.Status = "Uploading";
            }
            return batch;
        }
    }

    /// <summary>
    /// How many queue rows are currently due for upload (Pending or Failed with expired backoff).
    /// Lets the engine skip the Tailscale hook entirely when there is nothing to do.
    /// </summary>
    public int CountDue()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM queue WHERE (status = 'Pending' OR (status = 'Failed' AND next_retry_at <= $now)) AND (next_retry_at IS NULL OR next_retry_at <= $now);";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void MarkUploaded(long id, string? immichAssetId = null)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            string? hash = null, path = null;
            using (var select = _conn.CreateCommand())
            {
                select.CommandText = "SELECT file_hash, source_path FROM queue WHERE id = $id;";
                select.Parameters.AddWithValue("$id", id);
                using var reader = select.ExecuteReader();
                if (reader.Read())
                {
                    hash = reader.GetString(0);
                    path = reader.GetString(1);
                }
            }
            if (hash is null) return;

            using var tx = _conn.BeginTransaction();
            try
            {
                using var insert = _conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = @"
                    INSERT OR IGNORE INTO uploaded (file_hash, source_path, uploaded_at, immich_asset_id)
                    VALUES ($h, $p, $now, $asset);";
                insert.Parameters.AddWithValue("$h", hash);
                insert.Parameters.AddWithValue("$p", path);
                insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                insert.Parameters.AddWithValue("$asset", (object?)immichAssetId ?? DBNull.Value);
                insert.ExecuteNonQuery();

                using var delete = _conn.CreateCommand();
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM queue WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Reverts files that were dequeued as 'Uploading' back to 'Pending'
    /// if a batch was interrupted, cancelled, or stopped on error.
    /// </summary>
    public void RevertToPending(IEnumerable<long> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;

        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"UPDATE queue SET status = 'Pending', updated_at = $now WHERE id IN ({string.Join(",", idList)}) AND status = 'Uploading';";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkFailed(long id, string error)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            int attempts = 0;
            using (var get = _conn.CreateCommand())
            {
                get.CommandText = "SELECT attempts FROM queue WHERE id = $id;";
                get.Parameters.AddWithValue("$id", id);
                object? v = get.ExecuteScalar();
                if (v is not null && v != DBNull.Value)
                    attempts = Convert.ToInt32(v);
            }
            attempts++;

            // Exponential backoff: 5, 10, 20, 40, 80 … capped at 240 minutes.
            int delayMinutes = (int)Math.Min(5 * Math.Pow(2, attempts - 1), 240);
            string nextRetry = DateTime.UtcNow.AddMinutes(delayMinutes).ToString("o");

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE queue SET status = 'Failed', attempts = $a, last_error = $err,
                                 next_retry_at = $retry, updated_at = $now WHERE id = $id;";
            cmd.Parameters.AddWithValue("$a", attempts);
            cmd.Parameters.AddWithValue("$err", error);
            cmd.Parameters.AddWithValue("$retry", nextRetry);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Move failed items back to Pending for another attempt.</summary>
    public int RequeueFailed()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE queue SET status = 'Pending', next_retry_at = NULL, updated_at = $now WHERE status = 'Failed';";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Crash recovery: anything left "Uploading" when the app starts goes back to Pending.
    /// Call once at startup.
    /// </summary>
    public int ResetStuckUploading()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE queue SET status = 'Pending', next_retry_at = NULL, updated_at = $now WHERE status = 'Uploading';";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            return cmd.ExecuteNonQuery();
        }
    }

    public (int Pending, int Uploading, int Uploaded, int Failed) GetStats()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            int Count(string sql)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = sql;
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            return (
                Count("SELECT COUNT(*) FROM queue WHERE status = 'Pending';"),
                Count("SELECT COUNT(*) FROM queue WHERE status = 'Uploading';"),
                Count("SELECT COUNT(*) FROM uploaded;"),
                Count("SELECT COUNT(*) FROM queue WHERE status = 'Failed';")
            );
        }
    }

    /// <summary>
    /// Drop a queue row entirely (e.g. the source file was deleted before upload).
    /// </summary>
    public void Remove(long id)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM queue WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _conn.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UploadQueue));
    }
}
