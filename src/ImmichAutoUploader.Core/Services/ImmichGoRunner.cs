namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// Represents the parameters required to execute a single-file upload using <c>immich-go</c>.
/// </summary>
/// <param name="ExePath">The absolute path to the <c>immich-go.exe</c> binary.</param>
/// <param name="ServerUrl">The destination Immich server URL.</param>
/// <param name="ApiKey">The Immich API key (passed via environment variable <c>IMMICH_API_KEY</c>).</param>
/// <param name="AdminApiKey">Optional Immich admin API key (passed via environment variable <c>IMMICH_ADMIN_API_KEY</c>).</param>
/// <param name="PauseJobs">Whether to pause server background jobs during upload.</param>
/// <param name="DeviceUuid">The unique, stable device identifier sent via <c>--device-uuid</c>.</param>
/// <param name="FilePath">The absolute path to the media file being uploaded.</param>
public sealed record UploadRequest(
    string ExePath,
    string ServerUrl,
    string ApiKey,
    string? AdminApiKey,
    bool PauseJobs,
    string DeviceUuid,
    string FilePath);

/// <summary>
/// Encapsulates the execution outcome of an <c>immich-go</c> process.
/// </summary>
/// <param name="Success"><c>true</c> if the process completed with exit code 0; otherwise, <c>false</c>.</param>
/// <param name="ExitCode">The process exit code (0 for success, non-zero for errors, -1 for timeouts or start failures).</param>
/// <param name="ErrorDetail">Diagnostic error details captured from standard error or standard output.</param>
public sealed record UploadResult(bool Success, int ExitCode, string ErrorDetail);

/// <summary>
/// Manages execution of the bundled <c>immich-go</c> CLI binary, maintaining strict per-file process isolation.
/// <para/>
/// <b>Architectural Rationale:</b>
/// Executing one process per file (rather than running against a staging directory) guarantees exact attribution:
/// exit code 0 deterministically proves that this specific file is safely in Immich (newly ingested or existing).
/// A non-zero exit code represents a failure for that single file only. A corrupted photo or unsupported video
/// codec can never cause an entire batch to abort.
/// <para/>
/// <b>Security:</b>
/// <c>IMMICH_API_KEY</c> and <c>IMMICH_ADMIN_API_KEY</c> are passed via process environment variables rather
/// than command-line arguments, shielding them from Task Manager, Process Explorer, and Windows Event ID 4688 logs.
/// </summary>
public static class ImmichGoRunner
{
    /// <summary>
    /// Maximum allowed execution time for a single file upload (30 minutes) before the process is killed.
    /// Prevents hung network connections or stalled child processes from blocking workers indefinitely.
    /// </summary>
    private const int PerFileTimeoutMs = 30 * 60 * 1000;

    /// <summary>
    /// Spawns an <c>immich-go</c> child process to upload a single file and awaits its completion.
    /// </summary>
    /// <param name="req">The upload execution parameters.</param>
    /// <param name="ct">A cancellation token that can abort execution and terminate the child process tree.</param>
    /// <returns>An <see cref="UploadResult"/> indicating success or containing failure details.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public static async Task<UploadResult> UploadSingleAsync(UploadRequest req, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "upload",
            "--server", req.ServerUrl,
            "--on-errors", "stop", // Force non-zero exit on file failure so the engine detects it
            "--concurrent-tasks", "1", // Concurrency is managed at the process level by UploadEngine
            "--pause-immich-jobs", req.PauseJobs ? "true" : "false",
            "--log-level", "WARN",
        };

        if (!string.IsNullOrWhiteSpace(req.DeviceUuid))
        {
            args.Add("--device-uuid");
            args.Add(req.DeviceUuid);
        }

        args.Add(req.FilePath);

        // Pass secrets via environment variables to avoid command-line argument exposure
        var env = new Dictionary<string, string>
        {
            ["IMMICH_API_KEY"] = req.ApiKey
        };
        if (!string.IsNullOrEmpty(req.AdminApiKey))
        {
            env["IMMICH_ADMIN_API_KEY"] = req.AdminApiKey;
        }

        ProcessHelper.Result r;
        try
        {
            r = await ProcessHelper.RunAsync(req.ExePath, args, PerFileTimeoutMs, env, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UploadResult(false, -1, $"could not start immich-go: {ex.Message}");
        }

        if (r.TimedOut)
            return new UploadResult(false, -1, "immich-go timed out after 30 minutes");
        if (r.ExitCode == 0)
            return new UploadResult(true, 0, string.Empty);

        string detail = ProcessHelper.Tail(r.StdErr + "\n" + r.StdOut, 12);
        if (string.IsNullOrWhiteSpace(detail))
            detail = $"exit code {r.ExitCode} with no output";
        return new UploadResult(false, r.ExitCode, detail);
    }
}
