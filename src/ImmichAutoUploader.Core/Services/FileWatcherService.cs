using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// Monitors a target directory for new or updated photo and video files, debounces rapid file-system events,
/// verifies file write completion, and feeds settled files into <see cref="UploadQueue"/>.
/// <para/>
/// <b>Reliability & Concurrency Design:</b>
/// <list type="bullet">
///   <item><description><b>Settle Loop Debounce:</b> A file must remain quiet for <c>_settleDelay</c> (5 seconds) AND maintain an unchanged file size across consecutive timer checks before intake.</description></item>
///   <item><description><b>Exclusive Lock Detection:</b> Files being actively written by camera ingestion or file transfer tools are tested with a non-locking read stream before proceeding.</description></item>
///   <item><description><b>Bounded Channel Pipeline:</b> Settled file paths are buffered into a bounded channel (capacity 2000) consumed by 4 concurrent background worker tasks, isolating file-system events from SQLite operations.</description></item>
///   <item><description><b>Modification Retention During Hashing:</b> Uses a sentinel timestamp (<see cref="DateTime.MaxValue"/>) to prevent duplicate intake while preserving new events if a file is rewritten during hashing.</description></item>
///   <item><description><b>Zero-Byte Handling:</b> Files with zero bytes are caught early and skipped with an informational warning instead of failing during upload.</description></item>
///   <item><description><b>Buffer Overflow Recovery:</b> If the 64 KB <see cref="FileSystemWatcher"/> internal buffer overflows, an automatic asynchronous rescan ensures no files are lost.</description></item>
///   <item><description><b>Initial Startup Scan:</b> Runs an asynchronous depth-first search on startup to ingest files added while the application was closed.</description></item>
/// </list>
/// </summary>
public sealed class FileWatcherService : IDisposable
{
    private static readonly string[] TempExtensions =
        { ".tmp", ".part", ".crdownload", ".!ut", ".download", ".bak" };
    private static readonly string[] TempNames =
        { "thumbs.db", "desktop.ini", ".ds_store" };

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp", ".tiff", ".tif", ".bmp", ".gif",
        // RAW Formats
        ".cr2", ".cr3", ".nef", ".arw", ".dng", ".rw2", ".orf", ".raf",
        // Videos
        ".mp4", ".mov", ".avi", ".mkv", ".m4v", ".3gp", ".3g2", ".webm",
        ".mts", ".m2ts", ".mpg", ".mpeg",
    };

    private readonly string _watchFolder;
    private readonly string _doneFolder;
    private readonly UploadQueue _queue;
    private readonly FileSystemWatcher _watcher;

    /// <summary>
    /// Tracks files undergoing debounce settling.
    /// Key: Normalized file path.
    /// Value: (lastEvent: UTC time of last event or sentinel, lastSize: observed byte length, isAvailable: lock check passed).
    /// </summary>
    private readonly ConcurrentDictionary<string, (DateTime lastEvent, long lastSize, bool isAvailable)> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _cts = new();
    private Task? _initialScanTask;
    private Task? _settleTask;
    private readonly TimeSpan _settleDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Bounded producer-consumer channel decoupling the settle loop from the hashing/enqueueing workers.
    /// </summary>
    private readonly Channel<string> _enqueueChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(2000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = true,
    });

    private readonly List<Task> _workerTasks = new();
    private int _isScanning;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileWatcherService"/> class.
    /// </summary>
    /// <param name="watchFolder">The directory to watch recursively for media files.</param>
    /// <param name="doneFolder">The destination directory for completed uploads (ignored by the watcher to prevent feedback loops).</param>
    /// <param name="queue">The persistent upload queue to receive settled files.</param>
    public FileWatcherService(string watchFolder, string doneFolder, UploadQueue queue)
    {
        _watchFolder = Path.GetFullPath(watchFolder);
        _doneFolder = string.IsNullOrWhiteSpace(doneFolder)
            ? string.Empty
            : Path.GetFullPath(doneFolder);
        _queue = queue;

        _watcher = new FileSystemWatcher(_watchFolder)
        {
            IncludeSubdirectories = true,
            Filter = "*.*",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                           NotifyFilters.Size | NotifyFilters.DirectoryName,
            InternalBufferSize = 65536, // Maximum recommended buffer size (64 KB) to mitigate event drops
        };
        _watcher.Created += (_, e) => Note(e.FullPath);
        _watcher.Changed += (_, e) => Note(e.FullPath);
        _watcher.Renamed += (_, e) => Note(e.FullPath);
        _watcher.Error += (_, e) =>
        {
            AppLogger.Warn($"File watcher buffer overflow or error ({e.GetException().Message}); triggering directory rescan.");
            TriggerRescan();
        };
    }

    /// <summary>
    /// Starts the file watcher, launches the background settle loop, spawns worker tasks,
    /// and begins an asynchronous initial directory scan.
    /// </summary>
    public void Start()
    {
        _watcher.EnableRaisingEvents = true;
        _settleTask = SettleLoopAsync(_cts.Token);
        for (int i = 0; i < 4; i++)
        {
            _workerTasks.Add(Task.Run(() => EnqueueWorkerLoopAsync(_cts.Token)));
        }
        AppLogger.Info($"Watching folder: {_watchFolder}");
        _initialScanTask = Task.Run(async () =>
        {
            if (Interlocked.CompareExchange(ref _isScanning, 1, 0) == 0)
            {
                try { await InitialScanAsync(_cts.Token).ConfigureAwait(false); }
                finally { Volatile.Write(ref _isScanning, 0); }
            }
        });
    }

    /// <summary>
    /// Triggers an asynchronous directory scan if one is not already actively in progress.
    /// </summary>
    private void TriggerRescan()
    {
        if (Interlocked.CompareExchange(ref _isScanning, 1, 0) == 0)
        {
            Task.Run(async () =>
            {
                try { await InitialScanAsync(_cts.Token).ConfigureAwait(false); }
                finally { Volatile.Write(ref _isScanning, 0); }
            });
        }
    }

    // ------------------------------------------------------------------
    // Classification helpers (also used by UploadQueue)
    // ------------------------------------------------------------------

    /// <summary>
    /// Determines whether the specified file path represents a temporary, lock, or operating system metadata file.
    /// </summary>
    /// <param name="path">The file path to test.</param>
    /// <returns><c>true</c> if the file matches known temporary patterns; otherwise, <c>false</c>.</returns>
    public static bool IsTempFile(string path)
    {
        string name = Path.GetFileName(path);
        if (name.StartsWith("~") || name.StartsWith("$"))
            return true;
        if (TempNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return true;
        return TempExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether the specified file path has a supported photo, video, or RAW image extension.
    /// </summary>
    /// <param name="path">The file path to test.</param>
    /// <returns><c>true</c> if the extension is recognized as media; otherwise, <c>false</c>.</returns>
    public static bool IsMediaFile(string path) =>
        MediaExtensions.Contains(Path.GetExtension(path));

    // ------------------------------------------------------------------
    // Event intake + settle loop
    // ------------------------------------------------------------------

    /// <summary>
    /// Records a file-system event for a path, placing or updating it in <see cref="_pending"/>.
    /// </summary>
    /// <param name="path">The path that experienced an event.</param>
    private void Note(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return; // Directories are ignored; subdirectories are monitored via recursive watcher
        }
        catch { return; }

        if (IsTempFile(path) || !IsMediaFile(path) || IsUnderDoneFolder(path))
            return;

        // Reset the event timestamp and availability state.
        // NOTE: If the file is currently being hashed in EnqueueAndLogAsync (which sets lastEvent = DateTime.MaxValue),
        // a new file write event will overwrite it with DateTime.UtcNow.
        // This ensures the file will NOT be removed by RemovePendingIfSentinel and will be re-evaluated.
        _pending[path] = (DateTime.UtcNow, GetSizeSafe(path), false);
    }

    /// <summary>
    /// Periodic loop that inspects <see cref="_pending"/> files every 2 seconds to evaluate debounce criteria.
    /// </summary>
    private async Task SettleLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                ProcessSettled();
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            AppLogger.Error("Settle loop crashed.", ex);
        }
    }

    /// <summary>
    /// Iterates through pending files and verifies whether they have fully settled and are ready for enqueueing.
    /// </summary>
    private void ProcessSettled()
    {
        DateTime now = DateTime.UtcNow;
        foreach (var kvp in _pending)
        {
            string path = kvp.Key;
            var (lastEvent, lastSize, isAvailable) = kvp.Value;

            // 1. Time Debounce: File must experience no new events for at least _settleDelay (5 seconds).
            // (Note: If lastEvent is DateTime.MaxValue sentinel, now - lastEvent is negative, correctly skipping it).
            if (now - lastEvent < _settleDelay)
                continue;

            long size = GetSizeSafe(path);
            if (size < 0)
            {
                // File was deleted or became inaccessible before it settled
                _pending.TryRemove(path, out _);
                continue;
            }

            // 2. Size Debounce: File size must match the size observed on the previous tick.
            // If it changed, the file is still actively being copied or written; update size and continue waiting.
            if (size != lastSize)
            {
                _pending[path] = (now, size, false);
                continue;
            }

            // 3. Safety Check: Verify the file is not within the Done directory
            if (IsUnderDoneFolder(path))
            {
                _pending.TryRemove(path, out _);
                continue;
            }

            // 4. Attributes Check: Ignore directories, hidden files, and system files
            var attrs = GetAttributesSafe(path);
            if (attrs.HasFlag(FileAttributes.Directory) ||
                attrs.HasFlag(FileAttributes.Hidden) ||
                attrs.HasFlag(FileAttributes.System))
            {
                _pending.TryRemove(path, out _);
                continue;
            }

            // 5. Exclusive Lock Check: Test if another process holds an exclusive write lock
            if (!isAvailable)
            {
                if (!IsFileAvailable(path))
                {
                    _pending[path] = (now, size, false); // Writer still has the file open; wait for next tick
                    continue;
                }
            }

            // 6. Enqueue Hand-off: Write to the bounded channel.
            // Mark lastEvent = DateTime.MaxValue as a sentinel BEFORE writing to the channel.
            // This eliminates the race where a worker finishes before the sentinel is set.
            _pending[path] = (DateTime.MaxValue, size, true);
            if (!_enqueueChannel.Writer.TryWrite(path))
            {
                // Channel is full (backpressure). Revert sentinel so we re-evaluate on next tick without re-opening handles.
                _pending[path] = (now - _settleDelay, size, true);
            }
        }
    }

    /// <summary>
    /// Attempts to open a file with shared read-write access to verify it is not exclusively locked by another process.
    /// </summary>
    /// <param name="path">The file path to test.</param>
    /// <returns><c>true</c> if the file can be opened for reading; otherwise, <c>false</c>.</returns>
    private static bool IsFileAvailable(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Consumer loop executed by worker tasks, reading settled file paths from the bounded channel.
    /// </summary>
    private async Task EnqueueWorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _enqueueChannel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_enqueueChannel.Reader.TryRead(out var path))
                {
                    if (ct.IsCancellationRequested) return;
                    await EnqueueAndLogAsync(path, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            AppLogger.Error("Enqueue worker crashed.", ex);
        }
    }

    /// <summary>
    /// Hashes the settled file and enqueues it into <see cref="UploadQueue"/>.
    /// Manages sentinel cleanup and modification retention.
    /// </summary>
    private async Task EnqueueAndLogAsync(string path, CancellationToken ct)
    {
        try
        {
            var result = await _queue.TryEnqueueAsync(path, ct).ConfigureAwait(false);
            switch (result)
            {
                case Models.EnqueueResult.Enqueued:
                    AppLogger.Info($"Queued for upload: {path}");
                    RemovePendingIfSentinel(path);
                    break;
                case Models.EnqueueResult.SkippedEmptyFile:
                    AppLogger.Warn($"Skipped (empty file, Immich would reject it): {path}");
                    RemovePendingIfSentinel(path);
                    break;
                case Models.EnqueueResult.AlreadyUploaded:
                    AppLogger.Info($"Skipped (already uploaded before): {path}");
                    RemovePendingIfSentinel(path);
                    break;
                case Models.EnqueueResult.AlreadyQueued:
                    AppLogger.Info($"Skipped (already queued): {path}");
                    RemovePendingIfSentinel(path);
                    break;
                case Models.EnqueueResult.SkippedNotMedia:
                case Models.EnqueueResult.SkippedTempFile:
                case Models.EnqueueResult.FileNotFound:
                    RemovePendingIfSentinel(path);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down: Reset timestamp so the file is re-evaluated on next application startup
            _pending[path] = (DateTime.UtcNow, GetSizeSafe(path), false);
        }
        catch (IOException ex)
        {
            // Transient lock or sharing violation: Reset timestamp to trigger retry on next settle check
            AppLogger.Warn($"Transient I/O error queueing {path}, will retry: {ex.Message}");
            _pending[path] = (DateTime.UtcNow, GetSizeSafe(path), false);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to queue {path}.", ex);
            RemovePendingIfSentinel(path);
        }
    }

    /// <summary>
    /// Removes the file from <see cref="_pending"/> only if its timestamp matches the sentinel (<see cref="DateTime.MaxValue"/>).
    /// If the timestamp was overwritten by <see cref="Note"/> due to a file modification while hashing,
    /// the entry is retained so the updated content will be settled and queued.
    /// </summary>
    /// <param name="path">The file path to check and remove.</param>
    private void RemovePendingIfSentinel(string path)
    {
        if (_pending.TryGetValue(path, out var current) && current.lastEvent == DateTime.MaxValue)
        {
            _pending.TryRemove(new KeyValuePair<string, (DateTime, long, bool)>(path, current));
        }
    }

    /// <summary>
    /// Performs an asynchronous depth-first search of the watch directory to discover files added while offline.
    /// </summary>
    private async Task InitialScanAsync(CancellationToken ct)
    {
        try
        {
            int found = 0;
            var stack = new Stack<string>();
            stack.Push(_watchFolder);

            while (stack.Count > 0 && !ct.IsCancellationRequested)
            {
                string dir = stack.Pop();
                string[] subdirs, files;
                try { subdirs = Directory.GetDirectories(dir); }
                catch { continue; }
                try { files = Directory.GetFiles(dir); }
                catch { continue; }

                foreach (string sub in subdirs)
                {
                    if (!IsUnderDoneFolder(sub))
                        stack.Push(sub);
                }
                foreach (string f in files)
                {
                    if (ct.IsCancellationRequested) return;
                    if (IsTempFile(f) || !IsMediaFile(f) || IsUnderDoneFolder(f))
                        continue;
                    // Do not reset the sentinel timestamp if the file is currently being hashed in-flight
                    if (_pending.TryGetValue(f, out var existing) && existing.lastEvent == DateTime.MaxValue)
                        continue;
                    _pending[f] = (DateTime.UtcNow, GetSizeSafe(f), false);
                    if (++found % 500 == 0)
                        await Task.Yield(); // Yield control periodically during massive directory scans
                }
            }

            AppLogger.Info($"Initial scan found {found} files under {_watchFolder} (settle-checking).");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Initial scan failed.", ex);
        }
    }

    /// <summary>
    /// Determines whether the specified path resides within or is equal to the Done folder.
    /// </summary>
    /// <param name="path">The file or directory path to check.</param>
    /// <returns><c>true</c> if the path is inside or identical to the Done directory; otherwise, <c>false</c>.</returns>
    internal bool IsUnderDoneFolder(string path)
    {
        if (string.IsNullOrEmpty(_doneFolder))
            return false;

        string cleanDone = Path.TrimEndingDirectorySeparator(_doneFolder);
        string cleanPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        if (string.Equals(cleanPath, cleanDone, StringComparison.OrdinalIgnoreCase))
            return true;

        string normalizedDone = cleanDone + Path.DirectorySeparatorChar;
        return cleanPath.StartsWith(normalizedDone, StringComparison.OrdinalIgnoreCase);
    }

    private static long GetSizeSafe(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return -1; }
    }

    private static FileAttributes GetAttributesSafe(string path)
    {
        try { return File.GetAttributes(path); }
        catch { return (FileAttributes)0; }
    }

    /// <summary>
    /// Disposes the file watcher, completes the channel, and awaits background worker task shutdown.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _watcher.EnableRaisingEvents = false; } catch { /* best effort */ }
        _cts.Cancel();
        _enqueueChannel.Writer.TryComplete();
        try { _settleTask?.Wait(TimeSpan.FromSeconds(3)); }
        catch { /* best effort */ }
        try { _initialScanTask?.Wait(TimeSpan.FromSeconds(3)); }
        catch { /* best effort */ }
        try { Task.WaitAll(_workerTasks.ToArray(), TimeSpan.FromSeconds(5)); }
        catch { /* best effort */ }
        _watcher.Dispose();
        _cts.Dispose();
        AppLogger.Info("File watcher stopped.");
    }
}
