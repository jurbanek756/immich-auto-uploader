namespace ImmichAutoUploader.Core.Models;

/// <summary>
/// Represents an individual file tracked within the persistent upload queue.
/// </summary>
public sealed class QueuedFile
{
    /// <summary>
    /// Gets or sets the primary key identifier in the SQLite queue table.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the absolute file system path to the source media file.
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the uppercase hex-encoded SHA-256 hash of the file contents.
    /// Used as the primary deduplication key.
    /// </summary>
    public string FileHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes at the time of intake.
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// Gets or sets the current lifecycle state of the item:
    /// <c>"Pending"</c>, <c>"Uploading"</c>, or <c>"Failed"</c>.
    /// </summary>
    public string Status { get; set; } = "Pending";

    /// <summary>
    /// Gets or sets the total number of failed upload attempts recorded for this file.
    /// Used to calculate exponential backoff delays.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Gets or sets the error message or exit summary from the most recent failed upload attempt.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when the file was first detected and enqueued.
    /// </summary>
    public DateTime DetectedAt { get; set; }
}

/// <summary>
/// Represents the result of attempting to enqueue a detected file into <see cref="Services.UploadQueue"/>.
/// </summary>
public enum EnqueueResult
{
    /// <summary>
    /// The file was successfully inserted into the queue with <c>Pending</c> status.
    /// </summary>
    Enqueued,

    /// <summary>
    /// The file's content hash already exists in the permanent <c>uploaded</c> audit table.
    /// The file is skipped to prevent duplicate uploads.
    /// </summary>
    AlreadyUploaded,

    /// <summary>
    /// The file's content hash is already tracked in the <c>queue</c> table with an active status.
    /// </summary>
    AlreadyQueued,

    /// <summary>
    /// The file is empty (0 bytes). Skipped because Immich rejects empty files.
    /// </summary>
    SkippedEmptyFile,

    /// <summary>
    /// The file does not have a recognized photo, video, or RAW image extension.
    /// </summary>
    SkippedNotMedia,

    /// <summary>
    /// The file matches temporary, partial, backup, or operating system metadata patterns (e.g., <c>~*</c>, <c>.tmp</c>, <c>thumbs.db</c>).
    /// </summary>
    SkippedTempFile,

    /// <summary>
    /// The file disappeared or was deleted from disk before its size or hash could be inspected.
    /// </summary>
    FileNotFound,
}
