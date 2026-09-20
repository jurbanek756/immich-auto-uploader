using ImmichAutoUploader.Core.Models;
using ImmichAutoUploader.Core.Services;
using Xunit;

namespace ImmichAutoUploader.Core.Tests;

public class UploadQueueTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly string _tempTestDir;
    private readonly UploadQueue _queue;

    public UploadQueueTests()
    {
        _tempTestDir = Path.Combine(Path.GetTempPath(), "ImmichAutoUploader_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempTestDir);
        _tempDbPath = Path.Combine(_tempTestDir, "test-queue.db");
        _queue = new UploadQueue(_tempDbPath);
    }

    public void Dispose()
    {
        _queue.Dispose();
        try
        {
            if (Directory.Exists(_tempTestDir))
                Directory.Delete(_tempTestDir, recursive: true);
        }
        catch { }
    }

    private string CreateTempMediaFile(string name, byte[] content)
    {
        string path = Path.Combine(_tempTestDir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task TryEnqueueAsync_EnqueuesValidMediaFile()
    {
        string file = CreateTempMediaFile("photo.jpg", new byte[] { 1, 2, 3, 4 });
        var result = await _queue.TryEnqueueAsync(file);

        Assert.Equal(EnqueueResult.Enqueued, result);
        var stats = _queue.GetStats();
        Assert.Equal(1, stats.Pending);
        Assert.Equal(0, stats.Uploading);
    }

    [Fact]
    public async Task TryEnqueueAsync_SkipsDuplicatesWithSameHash()
    {
        string file1 = CreateTempMediaFile("photo1.jpg", new byte[] { 10, 20, 30 });
        string file2 = CreateTempMediaFile("photo2.jpg", new byte[] { 10, 20, 30 });

        var res1 = await _queue.TryEnqueueAsync(file1);
        var res2 = await _queue.TryEnqueueAsync(file2);

        Assert.Equal(EnqueueResult.Enqueued, res1);
        Assert.Equal(EnqueueResult.AlreadyQueued, res2);

        var stats = _queue.GetStats();
        Assert.Equal(1, stats.Pending);
    }

    [Fact]
    public async Task TryEnqueueAsync_SkipsEmptyFile()
    {
        string file = CreateTempMediaFile("empty.jpg", Array.Empty<byte>());
        var result = await _queue.TryEnqueueAsync(file);

        Assert.Equal(EnqueueResult.SkippedEmptyFile, result);
    }

    [Fact]
    public async Task TryEnqueueAsync_SkipsNonMediaFile()
    {
        string file = CreateTempMediaFile("doc.txt", new byte[] { 1, 2, 3 });
        var result = await _queue.TryEnqueueAsync(file);

        Assert.Equal(EnqueueResult.SkippedNotMedia, result);
    }

    [Fact]
    public async Task DequeueBatch_UpdatesStatusToUploading()
    {
        string file = CreateTempMediaFile("photo.png", new byte[] { 5, 6, 7 });
        await _queue.TryEnqueueAsync(file);

        var batch = _queue.DequeueBatch(10);
        Assert.Single(batch);
        Assert.Equal("Uploading", batch[0].Status);

        var stats = _queue.GetStats();
        Assert.Equal(0, stats.Pending);
        Assert.Equal(1, stats.Uploading);
    }

    [Fact]
    public async Task RevertToPending_RevertsUnprocessedItems()
    {
        string file1 = CreateTempMediaFile("photo1.jpg", new byte[] { 1 });
        string file2 = CreateTempMediaFile("photo2.jpg", new byte[] { 2 });
        await _queue.TryEnqueueAsync(file1);
        await _queue.TryEnqueueAsync(file2);

        var batch = _queue.DequeueBatch(10);
        Assert.Equal(2, batch.Count);

        // Revert only the second item
        _queue.RevertToPending(new[] { batch[1].Id });

        var stats = _queue.GetStats();
        Assert.Equal(1, stats.Pending);
        Assert.Equal(1, stats.Uploading);
    }

    [Fact]
    public async Task MarkUploaded_MovesItemToUploadedTableAtomically()
    {
        string file = CreateTempMediaFile("photo.jpg", new byte[] { 8, 9, 10 });
        await _queue.TryEnqueueAsync(file);

        var batch = _queue.DequeueBatch(10);
        Assert.Single(batch);

        _queue.MarkUploaded(batch[0].Id, immichAssetId: "asset-123");

        var stats = _queue.GetStats();
        Assert.Equal(0, stats.Pending);
        Assert.Equal(0, stats.Uploading);
        Assert.Equal(1, stats.Uploaded);

        // Trying to enqueue the same file again reports AlreadyUploaded
        var result = await _queue.TryEnqueueAsync(file);
        Assert.Equal(EnqueueResult.AlreadyUploaded, result);
    }

    [Fact]
    public async Task MarkFailed_SetsBackoffAndPreventsDuplicateEnqueues()
    {
        string file = CreateTempMediaFile("photo.jpg", new byte[] { 11, 12, 13 });
        await _queue.TryEnqueueAsync(file);

        var batch = _queue.DequeueBatch(10);
        Assert.Single(batch);

        _queue.MarkFailed(batch[0].Id, "Network error");

        var stats = _queue.GetStats();
        Assert.Equal(1, stats.Failed);

        // Enqueuing the same file while it is in Failed status should not duplicate it
        var result = await _queue.TryEnqueueAsync(file);
        Assert.Equal(EnqueueResult.AlreadyQueued, result);

        stats = _queue.GetStats();
        Assert.Equal(1, stats.Failed);
        Assert.Equal(0, stats.Pending);
    }

    [Fact]
    public async Task DequeueBatch_ReturnsFailedItemsWhenRetryBackoffExpired()
    {
        string file = CreateTempMediaFile("retry.jpg", new byte[] { 21, 22, 23 });
        await _queue.TryEnqueueAsync(file);

        var batch1 = _queue.DequeueBatch(10);
        Assert.Single(batch1);

        _queue.MarkFailed(batch1[0].Id, "Temporary timeout");

        // Immediately after failure, retry time is in the future (+5 min); should not be due
        Assert.Equal(0, _queue.CountDue());
        var batch2 = _queue.DequeueBatch(10);
        Assert.Empty(batch2);

        // Simulate backoff expiration by setting next_retry_at in the past
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_tempDbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE queue SET next_retry_at = $past WHERE id = $id;";
            cmd.Parameters.AddWithValue("$past", DateTime.UtcNow.AddMinutes(-1).ToString("o"));
            cmd.Parameters.AddWithValue("$id", batch1[0].Id);
            cmd.ExecuteNonQuery();
        }

        // Now it should be due and dequeued
        Assert.Equal(1, _queue.CountDue());
        var batch3 = _queue.DequeueBatch(10);
        Assert.Single(batch3);
        Assert.Equal(batch1[0].Id, batch3[0].Id);
        Assert.Equal("Uploading", batch3[0].Status);
    }

    [Fact]
    public async Task TryEnqueueAsync_FileRenamedWhileQueued_UpdatesSourcePath()
    {
        string file1 = CreateTempMediaFile("initial.jpg", new byte[] { 31, 32, 33 });
        var res1 = await _queue.TryEnqueueAsync(file1);
        Assert.Equal(EnqueueResult.Enqueued, res1);

        string file2 = Path.Combine(_tempTestDir, "renamed.jpg");
        File.Move(file1, file2);

        var res2 = await _queue.TryEnqueueAsync(file2);
        Assert.Equal(EnqueueResult.Enqueued, res2);

        var batch = _queue.DequeueBatch(10);
        Assert.Single(batch);
        Assert.Equal(file2, batch[0].SourcePath);
    }

    [Fact]
    public async Task RequeueFailed_ResetsFailedItemsToPending()
    {
        string file = CreateTempMediaFile("failed.jpg", new byte[] { 41, 42, 43 });
        await _queue.TryEnqueueAsync(file);

        var batch = _queue.DequeueBatch(10);
        _queue.MarkFailed(batch[0].Id, "Fatal connection error");

        Assert.Equal(1, _queue.GetStats().Failed);

        int requeued = _queue.RequeueFailed();
        Assert.Equal(1, requeued);

        var stats = _queue.GetStats();
        Assert.Equal(1, stats.Pending);
        Assert.Equal(0, stats.Failed);
    }

    [Fact]
    public async Task TryEnqueueAsync_SamePathModifiedContent_UpdatesHashAndResetsPending()
    {
        string file = CreateTempMediaFile("modified.jpg", new byte[] { 1, 2, 3 });
        var res1 = await _queue.TryEnqueueAsync(file);
        Assert.Equal(EnqueueResult.Enqueued, res1);

        // Modify the file contents on disk
        File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4, 5, 6 });

        var res2 = await _queue.TryEnqueueAsync(file);
        Assert.Equal(EnqueueResult.Enqueued, res2);

        var stats = _queue.GetStats();
        Assert.Equal(1, stats.Pending);

        var batch = _queue.DequeueBatch(10);
        Assert.Single(batch);
        Assert.Equal(6, batch[0].FileSize);
    }

    [Fact]
    public async Task TryEnqueueAsync_FileRenamedWhileFailed_ResetsStatusToPending()
    {
        string file1 = CreateTempMediaFile("orig_failed.jpg", new byte[] { 7, 8, 9 });
        await _queue.TryEnqueueAsync(file1);

        var batch = _queue.DequeueBatch(10);
        _queue.MarkFailed(batch[0].Id, "Transient network drop");

        Assert.Equal(1, _queue.GetStats().Failed);
        Assert.Equal(0, _queue.CountDue());

        // Rename the file while in Failed state
        string file2 = Path.Combine(_tempTestDir, "renamed_after_fail.jpg");
        File.Move(file1, file2);

        var res = await _queue.TryEnqueueAsync(file2);
        Assert.Equal(EnqueueResult.Enqueued, res);

        // Status should be reset to Pending and immediately due (not blocked by backoff)
        var stats = _queue.GetStats();
        Assert.Equal(1, stats.Pending);
        Assert.Equal(0, stats.Failed);
        Assert.Equal(1, _queue.CountDue());

        var batch2 = _queue.DequeueBatch(10);
        Assert.Single(batch2);
        Assert.Equal(file2, batch2[0].SourcePath);
    }

    [Fact]
    public async Task GetStats_ReturnsAccurateCountsGrouped()
    {
        string f1 = CreateTempMediaFile("s1.jpg", new byte[] { 1 });
        string f2 = CreateTempMediaFile("s2.jpg", new byte[] { 2 });
        string f3 = CreateTempMediaFile("s3.jpg", new byte[] { 3 });

        await _queue.TryEnqueueAsync(f1);
        await _queue.TryEnqueueAsync(f2);
        await _queue.TryEnqueueAsync(f3);

        var batch = _queue.DequeueBatch(2); // f1, f2 -> Uploading
        _queue.MarkUploaded(batch[0].Id);   // f1 -> Uploaded
        _queue.MarkFailed(batch[1].Id, "err"); // f2 -> Failed

        // f3 is still Pending
        var stats = _queue.GetStats();
        Assert.Equal(1, stats.Pending);
        Assert.Equal(0, stats.Uploading);
        Assert.Equal(1, stats.Uploaded);
        Assert.Equal(1, stats.Failed);
    }

    [Fact]
    public async Task TryEnqueueAsync_NonExistentFile_ReturnsFileNotFound()
    {
        string nonExistent = Path.Combine(_tempTestDir, "ghost.jpg");
        var result = await _queue.TryEnqueueAsync(nonExistent);
        Assert.Equal(EnqueueResult.FileNotFound, result);
    }

    [Fact]
    public async Task DequeueBatch_PreservesUtcDateTimeKindOnDetectedAt()
    {
        string file = CreateTempMediaFile("utc_check.jpg", new byte[] { 99, 98 });
        await _queue.TryEnqueueAsync(file);

        var batch = _queue.DequeueBatch(1);
        Assert.Single(batch);
        Assert.Equal(DateTimeKind.Utc, batch[0].DetectedAt.Kind);
    }

    [Fact]
    public async Task TryEnqueueAsync_SamePathModifiedContent_ClashesWithAnotherQueuedFile_ResolvesClashWithoutConstraintException()
    {
        string fileA = CreateTempMediaFile("photoA.jpg", new byte[] { 1, 2, 3 });
        string fileB = CreateTempMediaFile("photoB.jpg", new byte[] { 4, 5, 6 });

        var resA = await _queue.TryEnqueueAsync(fileA);
        var resB = await _queue.TryEnqueueAsync(fileB);
        Assert.Equal(EnqueueResult.Enqueued, resA);
        Assert.Equal(EnqueueResult.Enqueued, resB);

        // Overwrite fileA with fileB's content (identical hash)
        File.WriteAllBytes(fileA, new byte[] { 4, 5, 6 });

        // Enqueuing fileA should resolve the clash cleanly without throwing SqliteException
        var resClash = await _queue.TryEnqueueAsync(fileA);
        Assert.Equal(EnqueueResult.AlreadyQueued, resClash);

        var stats = _queue.GetStats();
        Assert.Equal(1, stats.Pending);
    }

    [Fact]
    public async Task RevertToPending_ResetsNextRetryAtToNull()
    {
        string file = CreateTempMediaFile("retry_revert.jpg", new byte[] { 7, 7, 7 });
        await _queue.TryEnqueueAsync(file);

        var batch = _queue.DequeueBatch(1);
        _queue.MarkFailed(batch[0].Id, "Transient network failure");

        // Expire retry delay
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_tempDbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE queue SET next_retry_at = $past WHERE id = $id;";
            cmd.Parameters.AddWithValue("$past", DateTime.UtcNow.AddMinutes(-1).ToString("o"));
            cmd.Parameters.AddWithValue("$id", batch[0].Id);
            cmd.ExecuteNonQuery();
        }

        // Dequeue for retry
        var retryBatch = _queue.DequeueBatch(1);
        Assert.Single(retryBatch);

        // Revert to pending
        _queue.RevertToPending(new[] { retryBatch[0].Id });

        // Verify next_retry_at is now NULL
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_tempDbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT next_retry_at, status FROM queue WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", retryBatch[0].Id);
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(0));
            Assert.Equal("Pending", reader.GetString(1));
        }
    }

    [Fact]
    public async Task DequeueBatch_WithZeroOrNegativeCount_ReturnsEmpty()
    {
        string file = CreateTempMediaFile("zero_batch.jpg", new byte[] { 88 });
        await _queue.TryEnqueueAsync(file);

        var batch0 = _queue.DequeueBatch(0);
        Assert.Empty(batch0);

        var batchNeg = _queue.DequeueBatch(-5);
        Assert.Empty(batchNeg);

        var stats = _queue.GetStats();
        Assert.Equal(1, stats.Pending);
    }
}
