namespace ImmichAutoUploader.Core.Models;

/// <summary>
/// One file waiting for (or finished with) upload.
/// </summary>
public sealed class QueuedFile
{
    public long Id { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty; // SHA-256 hex of content
    public long FileSize { get; set; }
    public string Status { get; set; } = "Pending"; // Pending | Uploading | Failed
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime DetectedAt { get; set; }
}

/// <summary>
/// Outcome of trying to add a file to the queue.
/// </summary>
public enum EnqueueResult
{
    Enqueued,
    AlreadyUploaded,   // same content hash uploaded before -> skip, no duplicate
    AlreadyQueued,     // same content hash already pending -> skip
    SkippedEmptyFile,  // zero-byte file: would be rejected by Immich anyway
    SkippedNotMedia,   // extension isn't a photo/video type we handle
    SkippedTempFile,   // ~file, .tmp, .part, thumbs.db, ...
    FileNotFound,
}
