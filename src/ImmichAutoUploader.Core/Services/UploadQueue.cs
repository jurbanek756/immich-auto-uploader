using System.Security.Cryptography;
using ImmichAutoUploader.Core.Models;
using Microsoft.Data.Sqlite;

namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// Provides a persistent, thread-safe SQLite-backed queue for media ingestion and deduplication.
/// <para/>
/// <b>Concurrency &amp; Thread-Safety Guarantees:</b>
/// <list type="bullet">
///   <item><description><b>Serialized SQLite Access:</b> All database operations (reads, writes, transactions) are strictly serialized through an internal synchronization object (<c>_lock</c>). SQLite connections in Microsoft.Data.Sqlite are not thread-safe; this pattern guarantees thread safety across concurrent caller threads.</description></item>
///   <item><description><b>Unlocked Content Hashing:</b> Heavy SHA-256 disk streaming in <see cref="TryEnqueueAsync"/> executes asynchronously <i>outside</i> the database lock, preventing I/O operations from blocking other database queries.</description></item>
///   <item><description><b>High-Performance WAL Mode:</b> Configured with <c>PRAGMA journal_mode = WAL</c> and <c>PRAGMA synchronous = NORMAL</c> to provide crash resilience while minimizing disk write overhead.</description></item>
/// </list>
/// </summary>
public sealed class UploadQueue : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Gets the standard file path for the SQLite queue database in the user's AppData directory.
    /// </summary>
    public static string DefaultDbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ImmichAutoUploader", "upload-queue.db");

    /// <summary>
    /// Initializes a new instance of the <see cref="UploadQueue"/> class, opening the SQLite connection
    /// and executing schema migrations.
    /// </summary>
    /// <param name="dbPath">The absolute file path to the SQLite database file.</param>
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

    /// <summary>
    /// Creates required database tables and indices if they do not exist, and applies schema migrations.
    /// </summary>
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

        // Schema Migration: Ensure 'next_retry_at' column exists on databases created with earlier versions.
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

    /// <summary>
    /// Computes the SHA-256 hash of a media file and attempts to insert it into the queue.
    /// <para/>
    /// <b>Deduplication Logic:</b>
    /// <list type="bullet">
    ///   <item><description>If the hash exists in <c>uploaded</c>, skips insertion (<see cref="EnqueueResult.AlreadyUploaded"/>).</description></item>
    ///   <item><description>If the hash is in <c>queue</c> under the same path, skips insertion (<see cref="EnqueueResult.AlreadyQueued"/>).</description></item>
    ///   <item><description>If the hash is in <c>queue</c> under a path that no longer exists (e.g. file was renamed), updates the path.</description></item>
    ///   <item><description>Otherwise, inserts a new <c>Pending</c> row.</description></item>
    /// </list>
    /// </summary>
    /// <param name="sourcePath">The absolute path to the media file.</param>
    /// <param name="ct">A cancellation token for async hashing.</param>
    /// <returns>An <see cref="EnqueueResult"/> indicating the intake outcome.</returns>
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

        // Perform SHA-256 hashing outside the database lock to avoid blocking readers/writers
        string hash = await ComputeHashAsync(sourcePath, ct).ConfigureAwait(false);

        long? existingId = null;
        string? existingPath = null;

        lock (_lock)
        {
            ThrowIfDisposed();
            if (ExistsInUploaded(hash))
                return EnqueueResult.AlreadyUploaded;

            using var checkCmd = _conn.CreateCommand();
            checkCmd.CommandText = "SELECT id, source_path FROM queue WHERE file_hash = $h AND status IN ('Pending','Uploading','Failed') LIMIT 1;";
            checkCmd.Parameters.AddWithValue("$h", hash);
            using var reader = checkCmd.ExecuteReader();
            if (reader.Read())
            {
                existingId = reader.GetInt64(0);
                existingPath = reader.GetString(1);
            }
        }

        // If the hash is already in the queue, check if the previous file still exists
        if (existingId.HasValue && existingPath is not null)
        {
            if (string.Equals(existingPath, sourcePath, StringComparison.OrdinalIgnoreCase))
                return EnqueueResult.AlreadyQueued;

            bool oldFileExists = false;
            try { oldFileExists = File.Exists(existingPath); }
            catch { /* assume gone if inaccessible */ }

            if (!oldFileExists)
            {
                // File was renamed or moved: update the queue record to the new path
                lock (_lock)
                {
                    ThrowIfDisposed();
                    using var updateCmd = _conn.CreateCommand();
                    updateCmd.CommandText = "UPDATE queue SET source_path = $path, updated_at = $now WHERE id = $id;";
                    updateCmd.Parameters.AddWithValue("$path", sourcePath);
                    updateCmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                    updateCmd.Parameters.AddWithValue("$id", existingId.Value);
                    updateCmd.ExecuteNonQuery();
                    return EnqueueResult.Enqueued;
                }
            }

            return EnqueueResult.AlreadyQueued;
        }

        lock (_lock)
        {
            ThrowIfDisposed();
            if (ExistsInUploaded(hash))
                return EnqueueResult.AlreadyUploaded;

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO queue (source_path, file_hash, file_size, status, detected_at, updated_at)
                VALUES ($path, $hash, $size, 'Pending', $now, $now);";
            cmd.Parameters.AddWithValue("$path", sourcePath);
            cmd.Parameters.AddWithValue("$hash", hash);
            cmd.Parameters.AddWithValue("$size", size);
            string now = DateTime.UtcNow.ToString("o");
            cmd.Parameters.AddWithValue("$now", now);
            int rows = cmd.ExecuteNonQuery();
            return rows > 0 ? EnqueueResult.Enqueued : EnqueueResult.AlreadyQueued;
        }
    }

    /// <summary>
    /// Checks whether the specified SHA-256 hash exists in the permanent <c>uploaded</c> table.
    /// </summary>
    private bool ExistsInUploaded(string hash)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM uploaded WHERE file_hash = $h LIMIT 1;";
        cmd.Parameters.AddWithValue("$h", hash);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Computes the uppercase hex-encoded SHA-256 digest of a file using an 80 KB async buffer.
    /// </summary>
    private static async Task<string> ComputeHashAsync(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, bufferSize: 81920, useAsync: true);
        byte[] hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    // ------------------------------------------------------------------
    // Dequeue / completion
    // ------------------------------------------------------------------

    /// <summary>
    /// Dequeues up to <paramref name="maxCount"/> files ready for upload (status <c>Pending</c> or <c>Failed</c> with expired backoff),
    /// atomically transitioning their status to <c>Uploading</c>.
    /// </summary>
    /// <param name="maxCount">The maximum number of items to dequeue.</param>
    /// <returns>A list of dequeued <see cref="QueuedFile"/> items.</returns>
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
    /// Returns the count of queue items currently eligible for upload (<c>Pending</c> or <c>Failed</c> with expired backoff).
    /// Used by <see cref="UploadEngine"/> to skip unnecessary Tailscale connections when the queue has no due work.
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

    /// <summary>
    /// Records a successful upload in the permanent <c>uploaded</c> audit table and removes the item from <c>queue</c>.
    /// </summary>
    /// <param name="id">The queue record ID.</param>
    /// <param name="immichAssetId">Optional asset ID returned by Immich.</param>
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
    /// Reverts dequeued items whose status is <c>Uploading</c> back to <c>Pending</c>.
    /// Called when an active batch is cancelled, stopped on error, or interrupted.
    /// </summary>
    /// <param name="ids">Collection of queue record IDs to revert.</param>
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

    /// <summary>
    /// Marks a queue item as <c>Failed</c>, increments its attempt counter, records the error detail,
    /// and calculates an exponential backoff timestamp for <c>next_retry_at</c>.
    /// <para/>
    /// <b>Backoff Formula:</b>
    /// <c>delayMinutes = min(5 * 2^(attempts - 1), 240)</c>
    /// Resulting schedule: 5m, 10m, 20m, 40m, 80m, 160m, 240m (capped).
    /// </summary>
    /// <param name="id">The queue record ID.</param>
    /// <param name="error">Diagnostic error message.</param>
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

            // Exponential backoff: 5, 10, 20, 40, 80, 160... capped at 240 minutes (4 hours).
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

    /// <summary>
    /// Resets all <c>Failed</c> queue items back to <c>Pending</c> and clears their backoff timestamps,
    /// making them immediately eligible for the next batch run.
    /// </summary>
    /// <returns>The number of items reset.</returns>
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
    /// Crash Recovery: Identifies any queue items left in <c>Uploading</c> status from a previous process
    /// crash or unexpected system reboot, resetting them to <c>Pending</c> and clearing <c>next_retry_at</c>.
    /// <para/>
    /// Must be invoked once during application startup prior to launching <see cref="UploadEngine"/>.
    /// </summary>
    /// <returns>The number of recovered items.</returns>
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

    /// <summary>
    /// Queries current aggregate statistics across queue tables.
    /// </summary>
    /// <returns>A tuple containing counts for <c>(Pending, Uploading, Uploaded, Failed)</c>.</returns>
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
    /// Permanently deletes a queue record by its identifier (e.g., if the source file was deleted before upload).
    /// </summary>
    /// <param name="id">The queue record ID to delete.</param>
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

    /// <summary>
    /// Closes and disposes the SQLite connection.
    /// </summary>
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
