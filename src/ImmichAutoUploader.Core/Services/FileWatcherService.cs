using System.Collections.Concurrent;

namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// Watches the watch folder for new photos/videos and feeds them into the <see cref="UploadQueue"/>.
/// <para/>
/// Reliability details (learned the hard way):
/// <list type="bullet">
///   <item>Events are debounced: a file must sit quiet for <c>_settleDelay</c> AND have a
///         stable size before it's touched — a 4 GB video still copying is left alone.</item>
///   <item>Zero-byte files are skipped with a warning (Immich rejects them; they used to
///         kill whole upload runs).</item>
///   <item>Temp files (~name, .tmp, .part, thumbs.db, …) and non-media extensions are ignored.</item>
///   <item>On watcher buffer overflow, a full rescan runs so nothing is missed.</item>
///   <item>Startup does an initial scan, catching files added while the app was closed.</item>
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
        // images
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp", ".tiff", ".tif", ".bmp", ".gif",
        // RAW
        ".cr2", ".cr3", ".nef", ".arw", ".dng", ".rw2", ".orf", ".raf",
        // video
        ".mp4", ".mov", ".avi", ".mkv", ".m4v", ".3gp", ".3g2", ".webm",
        ".mts", ".m2ts", ".mpg", ".mpeg",
    };

    private readonly string _watchFolder;
    private readonly string _doneFolder;
    private readonly UploadQueue _queue;
    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentDictionary<string, (DateTime lastEvent, long lastSize)> _pending =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _settleTask;
    private readonly TimeSpan _settleDelay = TimeSpan.FromSeconds(5);
    private bool _disposed;

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
            InternalBufferSize = 65536,
        };
        _watcher.Created += (_, e) => Note(e.FullPath);
        _watcher.Changed += (_, e) => Note(e.FullPath);
        _watcher.Renamed += (_, e) => Note(e.FullPath);
        _watcher.Error += (_, e) =>
        {
            AppLogger.Warn($"File watcher error ({e.GetException().Message}); running full rescan.");
            Task.Run(() => InitialScanAsync(_cts.Token));
        };

        _settleTask = SettleLoopAsync(_cts.Token);
    }

    public void Start()
    {
        _watcher.EnableRaisingEvents = true;
        AppLogger.Info($"Watching folder: {_watchFolder}");
        Task.Run(() => InitialScanAsync(_cts.Token));
    }

    // ------------------------------------------------------------------
    // Classification helpers (also used by UploadQueue)
    // ------------------------------------------------------------------

    public static bool IsTempFile(string path)
    {
        string name = Path.GetFileName(path);
        if (name.StartsWith("~") || name.StartsWith("$"))
            return true;
        if (TempNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return true;
        return TempExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsMediaFile(string path) =>
        MediaExtensions.Contains(Path.GetExtension(path));

    // ------------------------------------------------------------------
    // Event intake + settle loop
    // ------------------------------------------------------------------

    private void Note(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return; // we only care about files
        }
        catch { return; }
        _pending[path] = (DateTime.UtcNow, GetSizeSafe(path));
    }

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
            // shutting down
        }
        catch (Exception ex)
        {
            AppLogger.Error("Settle loop crashed.", ex);
        }
    }

    private void ProcessSettled()
    {
        DateTime now = DateTime.UtcNow;
        foreach (var kvp in _pending)
        {
            string path = kvp.Key;
            var (lastEvent, lastSize) = kvp.Value;

            if (now - lastEvent < _settleDelay)
                continue;

            long size = GetSizeSafe(path);
            if (size < 0)
            {
                _pending.TryRemove(path, out _); // deleted before it settled
                continue;
            }
            if (size != lastSize)
            {
                _pending[path] = (now, size); // still being written; wait more
                continue;
            }
            if (IsUnderDoneFolder(path))
            {
                _pending.TryRemove(path, out _);
                continue;
            }

            var attrs = GetAttributesSafe(path);
            if (attrs.HasFlag(FileAttributes.Directory) ||
                attrs.HasFlag(FileAttributes.Hidden) ||
                attrs.HasFlag(FileAttributes.System))
            {
                _pending.TryRemove(path, out _);
                continue;
            }

            _pending.TryRemove(path, out _);
            _ = Task.Run(() => EnqueueAndLogAsync(path)); // hashing can be slow; don't block the loop
        }
    }

    private async Task EnqueueAndLogAsync(string path)
    {
        try
        {
            var result = await _queue.TryEnqueueAsync(path, _cts.Token).ConfigureAwait(false);
            switch (result)
            {
                case Models.EnqueueResult.Enqueued:
                    AppLogger.Info($"Queued for upload: {path}");
                    break;
                case Models.EnqueueResult.SkippedEmptyFile:
                    AppLogger.Warn($"Skipped (empty file, Immich would reject it): {path}");
                    break;
                case Models.EnqueueResult.AlreadyUploaded:
                    AppLogger.Info($"Skipped (already uploaded before): {path}");
                    break;
                case Models.EnqueueResult.AlreadyQueued:
                    AppLogger.Info($"Skipped (already queued): {path}");
                    break;
                // Temp / non-media / missing files are not worth log spam.
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to queue {path}.", ex);
        }
    }

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

                foreach (string sub in subdirs) stack.Push(sub);
                foreach (string f in files)
                {
                    if (ct.IsCancellationRequested) return;
                    _pending[f] = (DateTime.UtcNow, GetSizeSafe(f));
                    if (++found % 500 == 0)
                        await Task.Yield();
                }
            }

            AppLogger.Info($"Initial scan found {found} files under {_watchFolder} (settle-checking).");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Initial scan failed.", ex);
        }
    }

    private bool IsUnderDoneFolder(string path)
    {
        if (string.IsNullOrEmpty(_doneFolder))
            return false;
        string full = Path.GetFullPath(path);
        return full.StartsWith(_doneFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _settleTask.Wait(TimeSpan.FromSeconds(5)); }
        catch { /* best effort */ }
        _watcher.Dispose();
        _cts.Dispose();
        AppLogger.Info("File watcher stopped.");
    }
}
