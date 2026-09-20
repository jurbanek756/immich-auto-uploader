using System.Diagnostics;
using ImmichAutoUploader.Core.Models;

namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// Background orchestration engine that periodically dequeues files from <see cref="UploadQueue"/>
/// and executes per-file uploads via <see cref="ImmichGoRunner"/>.
/// <para/>
/// <b>Batch Lifecycle Workflow:</b>
/// <list type="number">
///   <item><description><b>Tailscale Pre-Flight:</b> If enabled and work is due, ensures Tailscale is connected prior to starting uploads.</description></item>
///   <item><description><b>Batch Dequeue:</b> Retrieves up to <see cref="AppSettings.MaxFilesPerBatch"/> items that are pending or due for retry.</description></item>
///   <item><description><b>Parallel Uploads:</b> Spawns up to <see cref="AppSettings.ConcurrentTasks"/> concurrent worker tasks, each invoking a distinct <c>immich-go</c> process.</description></item>
///   <item><description><b>Move-to-Done:</b> Upon exit code 0, atomically moves the source file to <see cref="AppSettings.DoneFolder"/>, preserving subfolder hierarchy and handling cross-volume moves.</description></item>
///   <item><description><b>Exponential Backoff:</b> Upon failure, records the error and computes an exponential backoff timestamp.</description></item>
///   <item><description><b>Jellyfin Sync:</b> If at least one file uploaded successfully, triggers a library refresh.</description></item>
///   <item><description><b>Tailscale Post-Flight:</b> Disconnects Tailscale only if this batch established the connection.</description></item>
/// </list>
/// </summary>
public sealed class UploadEngine : IDisposable
{
    private readonly UploadQueue _queue;
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<EngineCredentials> _getCredentials;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _batchLock = new(1, 1);
    private readonly object _batchTaskLock = new();
    private Task? _activeBatchTask;
    private Task? _loopTask;
    private bool _disposed;

    /// <summary>
    /// Callback invoked to surface actionable alerts to the user (e.g., VPN login required, batch failures).
    /// </summary>
    public Action<string>? NotifyUser { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="UploadEngine"/> class.
    /// </summary>
    /// <param name="queue">The persistent SQLite upload queue.</param>
    /// <param name="getSettings">Delegate returning fresh application settings on each batch execution.</param>
    /// <param name="getCredentials">Delegate resolving fresh DPAPI credentials on each batch execution.</param>
    public UploadEngine(
        UploadQueue queue,
        Func<AppSettings> getSettings,
        Func<EngineCredentials> getCredentials)
    {
        _queue = queue;
        _getSettings = getSettings;
        _getCredentials = getCredentials;
    }

    /// <summary>
    /// Starts the background periodic execution loop.
    /// </summary>
    public void Start()
    {
        _loopTask = LoopAsync(_cts.Token);
        AppLogger.Info("Upload engine started.");
    }

    /// <summary>
    /// Triggers an immediate upload batch on demand.
    /// </summary>
    /// <returns><c>true</c> if a batch was started; <c>false</c> if a batch is already in progress.</returns>
    public async Task<bool> TriggerNowAsync()
    {
        if (!await _batchLock.WaitAsync(0).ConfigureAwait(false))
            return false;
        Task batchTask;
        lock (_batchTaskLock)
        {
            batchTask = RunBatchCoreAsync(_cts.Token);
            _activeBatchTask = batchTask;
        }
        try
        {
            await batchTask.ConfigureAwait(false);
            return true;
        }
        finally
        {
            lock (_batchTaskLock) { _activeBatchTask = null; }
            try { _batchLock.Release(); }
            catch (ObjectDisposedException) { /* shutting down */ }
        }
    }

    /// <summary>
    /// Periodic timer loop that triggers batches according to <see cref="AppSettings.BatchIntervalMinutes"/>.
    /// </summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int minutes = Math.Max(1, _getSettings().BatchIntervalMinutes);
                await Task.Delay(TimeSpan.FromMinutes(minutes), ct).ConfigureAwait(false);

                await _batchLock.WaitAsync(ct).ConfigureAwait(false);
                Task batchTask;
                lock (_batchTaskLock)
                {
                    batchTask = RunBatchCoreAsync(ct);
                    _activeBatchTask = batchTask;
                }
                try
                {
                    await batchTask.ConfigureAwait(false);
                }
                finally
                {
                    lock (_batchTaskLock) { _activeBatchTask = null; }
                    try { _batchLock.Release(); }
                    catch (ObjectDisposedException) { /* shutting down */ }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal engine shutdown
        }
        catch (Exception ex)
        {
            AppLogger.Error("Upload engine loop crashed.", ex);
        }
    }

    private enum ProcessOutcome
    {
        Uploaded,
        Failed,
        Skipped,
    }

    /// <summary>
    /// Coordinates the full lifecycle of an upload batch.
    /// </summary>
    private async Task RunBatchCoreAsync(CancellationToken ct)
    {
        AppSettings s;
        EngineCredentials creds;
        try
        {
            s = _getSettings();
            creds = _getCredentials();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Could not read settings/credentials for upload batch.", ex);
            return;
        }

        if (string.IsNullOrWhiteSpace(s.ImmichUrl) || string.IsNullOrWhiteSpace(creds.ImmichApiKey))
        {
            AppLogger.Info("Upload batch skipped: Immich URL or API key not configured.");
            return;
        }
        if (string.IsNullOrWhiteSpace(s.ImmichGoPath) || !File.Exists(s.ImmichGoPath))
        {
            AppLogger.Warn("Upload batch skipped: immich-go not found. Run build.ps1, then rebuild.");
            return;
        }

        // 1. Tailscale pre-flight hook: Only run if there is active work due in the queue.
        string serverUrl = s.ImmichUrl;
        string? tailscaleExe = null;
        bool tailscaleConnectedByUs = false;
        if (s.UseTailscale)
        {
            if (_queue.CountDue() == 0)
            {
                AppLogger.Info("Upload batch skipped: queue is empty; leaving Tailscale untouched.");
                return;
            }
            tailscaleExe = await TailscaleService.ResolveExePathAsync(s, ct).ConfigureAwait(false);
            if (tailscaleExe is null)
            {
                AppLogger.Warn("Upload batch skipped: Tailscale is enabled but tailscale.exe was not found.");
                return;
            }
            var outcome = await TailscaleService.EnsureConnectedAsync(tailscaleExe, ct).ConfigureAwait(false);
            switch (outcome.Result)
            {
                case TailscaleService.EnsureResult.Connected:
                    tailscaleConnectedByUs = outcome.ConnectedByUs;
                    break;
                case TailscaleService.EnsureResult.AuthRequired:
                    AppLogger.Warn("Upload batch skipped: Tailscale needs login.");
                    NotifyUser?.Invoke($"Tailscale needs login before uploads can run:\n{outcome.Detail}");
                    return;
                default:
                    AppLogger.Warn($"Upload batch skipped: Tailscale not connected ({outcome.Detail}).");
                    return;
            }
            if (!string.IsNullOrWhiteSpace(s.ImmichUrlViaTailscale))
                serverUrl = s.ImmichUrlViaTailscale;
        }

        try
        {
            // 2. Dequeue files ready for upload
            var batch = _queue.DequeueBatch(Math.Max(1, s.MaxFilesPerBatch));
            if (batch.Count == 0)
                return;

            if (s.PauseImmichJobs && string.IsNullOrEmpty(creds.ImmichAdminApiKey))
                AppLogger.Warn("PauseImmichJobs is on but no admin API key is set; server jobs will not be paused.");

            // 3. Process files in parallel up to ConcurrentTasks
            AppLogger.Info($"Upload batch started: {batch.Count} file(s) → {serverUrl}.");
            int uploaded = 0, failed = 0;
            var sw = Stopwatch.StartNew();
            int parallelism = Math.Clamp(s.ConcurrentTasks, 1, 20);
            using var gate = new SemaphoreSlim(parallelism, parallelism);
            int stopRequested = 0;
            var processedIds = new System.Collections.Concurrent.ConcurrentBag<long>();

            var tasks = batch.Select(async file =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref stopRequested) == 1 || ct.IsCancellationRequested)
                        return;

                    var outcome = await ProcessFileAsync(file, s, creds, serverUrl, ct).ConfigureAwait(false);
                    processedIds.Add(file.Id);
                    switch (outcome)
                    {
                        case ProcessOutcome.Uploaded:
                            Interlocked.Increment(ref uploaded);
                            break;
                        case ProcessOutcome.Failed:
                            Interlocked.Increment(ref failed);
                            if (string.Equals(s.OnErrors, "stop", StringComparison.OrdinalIgnoreCase))
                                Interlocked.Exchange(ref stopRequested, 1);
                            break;
                        case ProcessOutcome.Skipped:
                            break;
                    }
                }
                finally
                {
                    gate.Release();
                }
            });
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Batch interrupted or shutting down; uncompleted rows are safely restored below
            }
            finally
            {
                // Revert any dequeued files that were not processed (due to cancellation or 'stop on error') back to Pending
                var unprocessedIds = batch.Select(b => b.Id).Except(processedIds);
                _queue.RevertToPending(unprocessedIds);
            }
            sw.Stop();

            AppLogger.Info($"Upload batch complete: {uploaded} uploaded, {failed} failed ({sw.Elapsed:mm\\:ss}).");
            if (failed > 0)
                NotifyUser?.Invoke($"Upload batch finished: {uploaded} uploaded, {failed} failed. Check the log for details.");

            // 4. Jellyfin post-upload hook: Trigger refresh only if at least one file was uploaded
            if (uploaded > 0 && !string.IsNullOrWhiteSpace(s.JellyfinUrl))
            {
                if (string.IsNullOrWhiteSpace(creds.JellyfinApiKey))
                {
                    AppLogger.Warn("Jellyfin URL is set but no API key is stored; skipping refresh.");
                }
                else
                {
                    await JellyfinService.TriggerRefreshAsync(
                        s.JellyfinUrl, creds.JellyfinApiKey, s.JellyfinLibraryName, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // 5. Tailscale post-flight teardown:
            // Tear down connection only if this batch was the one that initiated it.
            // Uses a fresh timeout token to guarantee cleanup even if the batch cancellation token fired.
            if (tailscaleConnectedByUs && tailscaleExe is not null)
            {
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await TailscaleService.DisconnectAsync(tailscaleExe, cleanupCts.Token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Validates, uploads, and archives an individual file.
    /// </summary>
    private async Task<ProcessOutcome> ProcessFileAsync(
        QueuedFile file, AppSettings s, EngineCredentials creds, string serverUrl, CancellationToken ct)
    {
        // Re-validate the file before spawning child process: it may have been moved or deleted since enqueued
        FileInfo fi;
        try
        {
            fi = new FileInfo(file.SourcePath);
            if (!fi.Exists)
            {
                _queue.Remove(file.Id);
                AppLogger.Info($"Dropped from queue (file no longer exists): {file.SourcePath}");
                return ProcessOutcome.Skipped;
            }
            if (fi.Length == 0)
            {
                _queue.MarkFailed(file.Id, "File is empty (0 bytes).");
                return ProcessOutcome.Failed;
            }
        }
        catch (Exception ex)
        {
            _queue.MarkFailed(file.Id, $"Cannot access file: {ex.Message}");
            return ProcessOutcome.Failed;
        }

        bool pauseJobs = s.PauseImmichJobs && !string.IsNullOrWhiteSpace(creds.ImmichAdminApiKey);
        var req = new UploadRequest(
            s.ImmichGoPath, serverUrl, creds.ImmichApiKey, creds.ImmichAdminApiKey,
            pauseJobs, s.DeviceUuid, file.SourcePath);

        UploadResult result;
        try
        {
            result = await ImmichGoRunner.UploadSingleAsync(req, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _queue.RevertToPending(new[] { file.Id });
            throw;
        }
        catch (Exception ex)
        {
            _queue.MarkFailed(file.Id, $"Runner error: {ex.Message}");
            return ProcessOutcome.Failed;
        }

        if (!result.Success)
        {
            string redactedDetail = RedactSecrets(result.ErrorDetail, creds);
            string err = $"immich-go exit {result.ExitCode}: {redactedDetail}";
            _queue.MarkFailed(file.Id, err);
            AppLogger.Warn($"Upload failed: {file.SourcePath} — {redactedDetail}");
            return ProcessOutcome.Failed;
        }

        _queue.MarkUploaded(file.Id);
        try
        {
            using var moveCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await MoveToDoneAsync(file.SourcePath, s, moveCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Uploaded, but could not move to Done folder: {file.SourcePath} — {ex.Message}");
        }
        AppLogger.Info($"Uploaded: {file.SourcePath}");
        return ProcessOutcome.Uploaded;
    }

    /// <summary>
    /// Scans text output and masks API keys with <c>[REDACTED]</c> before writing to logs or UI.
    /// </summary>
    /// <param name="text">The string to sanitize.</param>
    /// <param name="creds">The active credentials to match and redact.</param>
    /// <returns>The sanitized string with all secrets masked.</returns>
    public static string RedactSecrets(string text, EngineCredentials creds)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (!string.IsNullOrEmpty(creds.ImmichApiKey))
            text = text.Replace(creds.ImmichApiKey, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(creds.ImmichAdminApiKey))
            text = text.Replace(creds.ImmichAdminApiKey, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(creds.JellyfinApiKey))
            text = text.Replace(creds.JellyfinApiKey, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    /// <summary>
    /// Moves an uploaded media file to <see cref="AppSettings.DoneFolder"/> while preserving its relative subfolder path.
    /// <para/>
    /// <b>Cross-Volume &amp; Locking Resilience:</b>
    /// <list type="bullet">
    ///   <item><description><b>Path Preservation:</b> Resolves relative path against <see cref="AppSettings.WatchFolder"/>.</description></item>
    ///   <item><description><b>Collision Handling:</b> Appends numeric suffixes (<c> (2)</c>, <c> (3)</c>) if a file with the same name already exists in the destination.</description></item>
    ///   <item><description><b>Cross-Volume Copy-Delete:</b> If source and destination reside on different volumes (or across network mounts), falls back to copy + delete with up to 5 retries.</description></item>
    ///   <item><description><b>Orphan Cleanup:</b> If the source file cannot be deleted after copying (e.g. due to a persistent lock), the copied target is removed to avoid leaving duplicate files.</description></item>
    ///   <item><description><b>Data Safety Guarantee:</b> If moving fails completely, the file remains in the watch folder. Because its hash is already in the <c>uploaded</c> table, it is safely skipped on future scans.</description></item>
    /// </list>
    /// </summary>
    /// <param name="sourcePath">The absolute path of the uploaded file.</param>
    /// <param name="s">Current application configuration containing watch and done folder paths.</param>
    /// <param name="ct">A cancellation token.</param>
    public static async Task MoveToDoneAsync(string sourcePath, AppSettings s, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(s.DoneFolder))
            return;
        try
        {
            // 1. Calculate relative destination path to preserve directory structure
            string relative = string.IsNullOrWhiteSpace(s.WatchFolder)
                ? Path.GetFileName(sourcePath)
                : Path.GetRelativePath(s.WatchFolder, sourcePath);
            if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
                relative = Path.GetFileName(sourcePath); // Cross-volume or outside watch folder: fall back to flat move

            string dest = Path.Combine(s.DoneFolder, relative);
            string? dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            bool copySucceeded = false;
            string? lastTarget = null;
            string target = DedupePath(dest);

            // 2. Retry loop (up to 5 attempts) to accommodate antivirus scanners or transient sharing violations
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!copySucceeded)
                        target = DedupePath(dest);

                    // Check whether source and target share the same drive volume
                    bool isCrossVolume = !string.Equals(
                        Path.GetPathRoot(Path.GetFullPath(sourcePath)),
                        Path.GetPathRoot(Path.GetFullPath(target)),
                        StringComparison.OrdinalIgnoreCase);

                    if (!isCrossVolume)
                    {
                        File.Move(sourcePath, target);
                        return; // Fast atomic move succeeded on same volume
                    }

                    // Cross-volume: copy the file first, then delete original
                    if (!copySucceeded)
                    {
                        File.Copy(sourcePath, target, overwrite: false);
                        copySucceeded = true;
                        lastTarget = target;
                    }

                    // Delete the original source file after successful copy
                    File.Delete(sourcePath);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    // Exponential delay between retries: 100ms, 200ms, 300ms, 400ms
                    await Task.Delay(100 * (attempt + 1), ct).ConfigureAwait(false);
                }
            }

            // 3. Orphan Cleanup: If source deletion failed after all retries on cross-volume, remove the copied target
            // to avoid leaving orphaned duplicate files on the destination drive.
            if (copySucceeded && File.Exists(sourcePath) && lastTarget is not null)
            {
                try { File.Delete(lastTarget); }
                catch { /* best effort cleanup */ }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Upload already succeeded — the file stays put and will be skipped as
            // already-uploaded on the next scan. Never lose a photo over a move error.
            AppLogger.Warn($"Uploaded, but could not move to Done folder: {sourcePath} — {ex.Message}");
        }
    }

    /// <summary>
    /// Generates a non-colliding file path by appending numeric suffixes (<c> (2)</c>, <c> (3)</c>) if the destination file already exists.
    /// </summary>
    private static string DedupePath(string dest)
    {
        if (!File.Exists(dest))
            return dest;
        string dir = Path.GetDirectoryName(dest) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(dest);
        string ext = Path.GetExtension(dest);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Cancels active loops and waits for active batch tasks to complete.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();

        Task? active;
        lock (_batchTaskLock) { active = _activeBatchTask; }
        var tasksToWait = new[] { _loopTask, active }.Where(t => t != null).Cast<Task>().ToArray();
        try { Task.WaitAll(tasksToWait, TimeSpan.FromSeconds(35)); }
        catch { /* best effort */ }

        try
        {
            if (_batchLock.Wait(TimeSpan.FromSeconds(5)))
            {
                _batchLock.Dispose();
            }
        }
        catch (ObjectDisposedException) { }

        _cts.Dispose();
        AppLogger.Info("Upload engine stopped.");
    }
}
