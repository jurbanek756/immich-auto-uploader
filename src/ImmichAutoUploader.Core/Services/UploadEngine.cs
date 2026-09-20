using System.Diagnostics;
using ImmichAutoUploader.Core.Models;

namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// Background upload engine: every <see cref="AppSettings.BatchIntervalMinutes"/>
/// (or on demand) it takes up to <see cref="AppSettings.MaxFilesPerBatch"/> files
/// from the queue and uploads each with its own immich-go process.
/// <para/>
/// Batch flow:
/// <list type="number">
///   <item>Tailscale: if enabled and the queue has due work, ensure it's connected
///   first — then disconnect it again after the batch (Jellyfin refresh included),
///   but only if this batch was the one that brought it up.</item>
///   <item>Dequeue files whose retry backoff has expired.</item>
///   <item>Upload each file (up to <see cref="AppSettings.ConcurrentTasks"/> at once).</item>
///   <item>Success → mark uploaded, move the original to the Done folder
///         (relative path preserved), count it.</item>
///   <item>Failure → mark failed with exponential backoff; the file stays queued.</item>
///   <item>If anything was uploaded and Jellyfin is configured → trigger a refresh.</item>
/// </list>
/// Settings and credentials are read fresh for every batch, so Save applies
/// without restarting the app.
/// </summary>
public sealed class UploadEngine : IDisposable
{
    private readonly UploadQueue _queue;
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<EngineCredentials> _getCredentials;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _batchLock = new(1, 1);
    private Task? _loopTask;
    private bool _disposed;

    /// <summary>Called for things the user should see (auth needed, batch failures).</summary>
    public Action<string>? NotifyUser { get; set; }

    public UploadEngine(
        UploadQueue queue,
        Func<AppSettings> getSettings,
        Func<EngineCredentials> getCredentials)
    {
        _queue = queue;
        _getSettings = getSettings;
        _getCredentials = getCredentials;
    }

    public void Start()
    {
        _loopTask = LoopAsync(_cts.Token);
        AppLogger.Info("Upload engine started.");
    }

    /// <summary>
    /// Run a batch right now. Returns false if a batch is already running.
    /// </summary>
    public async Task<bool> TriggerNowAsync()
    {
        if (!await _batchLock.WaitAsync(0).ConfigureAwait(false))
            return false;
        try
        {
            await RunBatchCoreAsync(_cts.Token).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _batchLock.Release();
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int minutes = Math.Max(1, _getSettings().BatchIntervalMinutes);
                await Task.Delay(TimeSpan.FromMinutes(minutes), ct).ConfigureAwait(false);

                await _batchLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await RunBatchCoreAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    _batchLock.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            AppLogger.Error("Upload engine loop crashed.", ex);
        }
    }

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

        // 1. Tailscale hook — only when there is actually something to upload.
        //    The connection is torn down again in the finally below, but only
        //    if this batch was the one that brought it up.
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

        if (s.PauseImmichJobs && string.IsNullOrEmpty(creds.ImmichAdminApiKey))
            AppLogger.Warn("PauseImmichJobs is on but no admin API key is set; server jobs will not be paused.");

        try
        {
            // 2. Dequeue
            var batch = _queue.DequeueBatch(Math.Max(1, s.MaxFilesPerBatch));
            if (batch.Count == 0)
                return;

            // 3. Upload
            AppLogger.Info($"Upload batch started: {batch.Count} file(s) → {serverUrl}.");
            int uploaded = 0, failed = 0;
            var sw = Stopwatch.StartNew();
            int parallelism = Math.Clamp(s.ConcurrentTasks, 1, 20);
            using var gate = new SemaphoreSlim(parallelism, parallelism);
            bool stopRequested = false;

            var tasks = batch.Select(async file =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (stopRequested || ct.IsCancellationRequested)
                        return;
                    bool ok = await ProcessFileAsync(file, s, creds, serverUrl, ct).ConfigureAwait(false);
                    if (ok)
                        Interlocked.Increment(ref uploaded);
                    else
                    {
                        Interlocked.Increment(ref failed);
                        if (string.Equals(s.OnErrors, "stop", StringComparison.OrdinalIgnoreCase))
                            stopRequested = true;
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
                // shutting down mid-batch; queued rows stay consistent
            }
            sw.Stop();

            AppLogger.Info($"Upload batch complete: {uploaded} uploaded, {failed} failed ({sw.Elapsed:mm\\:ss}).");
            if (failed > 0)
                NotifyUser?.Invoke($"Upload batch finished: {uploaded} uploaded, {failed} failed. Check the log for details.");

            // 4. Jellyfin hook — only when something actually landed on the server.
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
            // 5. Tailscale teardown — after uploads AND the Jellyfin refresh,
            //    and only when this batch connected it.
            if (tailscaleConnectedByUs && tailscaleExe is not null)
                await TailscaleService.DisconnectAsync(tailscaleExe, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> ProcessFileAsync(
        QueuedFile file, AppSettings s, EngineCredentials creds, string serverUrl, CancellationToken ct)
    {
        // Re-validate: the file may have been moved or deleted since it was queued.
        FileInfo fi;
        try
        {
            fi = new FileInfo(file.SourcePath);
            if (!fi.Exists)
            {
                _queue.Remove(file.Id);
                AppLogger.Info($"Dropped from queue (file no longer exists): {file.SourcePath}");
                return true; // not a failure — nothing to upload
            }
            if (fi.Length == 0)
            {
                _queue.MarkFailed(file.Id, "File is empty (0 bytes).");
                return false;
            }
        }
        catch (Exception ex)
        {
            _queue.MarkFailed(file.Id, $"Cannot access file: {ex.Message}");
            return false;
        }

        var req = new UploadRequest(
            s.ImmichGoPath, serverUrl, creds.ImmichApiKey, creds.ImmichAdminApiKey,
            s.PauseImmichJobs, s.DeviceUuid, file.SourcePath);

        UploadResult result;
        try
        {
            result = await ImmichGoRunner.UploadSingleAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _queue.MarkFailed(file.Id, $"Runner error: {ex.Message}");
            return false;
        }

        if (!result.Success)
        {
            string err = $"immich-go exit {result.ExitCode}: {result.ErrorDetail}";
            _queue.MarkFailed(file.Id, err);
            AppLogger.Warn($"Upload failed: {file.SourcePath} — {result.ErrorDetail}");
            return false;
        }

        _queue.MarkUploaded(file.Id);
        MoveToDone(file.SourcePath, s);
        AppLogger.Info($"Uploaded: {file.SourcePath}");
        return true;
    }

    private static void MoveToDone(string sourcePath, AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.DoneFolder))
            return;
        try
        {
            string relative = Path.GetRelativePath(s.WatchFolder, sourcePath);
            if (relative.StartsWith("..", StringComparison.Ordinal))
                relative = Path.GetFileName(sourcePath); // not under the watch folder; flat move
            string dest = Path.Combine(s.DoneFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            dest = DedupePath(dest);
            File.Move(sourcePath, dest);
        }
        catch (Exception ex)
        {
            // Upload already succeeded — the file stays put and will be skipped as
            // already-uploaded on the next scan. Never lose a photo over a move error.
            AppLogger.Warn($"Uploaded, but could not move to Done folder: {sourcePath} — {ex.Message}");
        }
    }

    private static string DedupePath(string dest)
    {
        if (!File.Exists(dest))
            return dest;
        string dir = Path.GetDirectoryName(dest)!;
        string name = Path.GetFileNameWithoutExtension(dest);
        string ext = Path.GetExtension(dest);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(20)); }
        catch { /* best effort */ }
        _batchLock.Dispose();
        _cts.Dispose();
        AppLogger.Info("Upload engine stopped.");
    }
}
