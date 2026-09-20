namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// One immich-go invocation for a single file.
/// </summary>
public sealed record UploadRequest(
    string ExePath,
    string ServerUrl,
    string ApiKey,
    string? AdminApiKey,
    bool PauseJobs,
    string DeviceUuid,
    string FilePath);

public sealed record UploadResult(bool Success, int ExitCode, string ErrorDetail);

/// <summary>
/// Runs the bundled immich-go, one file per process.
/// <para/>
/// Per-file (rather than one run per batch over a staging dir) is deliberate:
/// attribution is exact — exit code 0 means this file is in Immich (uploaded
/// or already-exists), anything else is a failure for this file only. One bad
/// file can never take down the rest of the batch, which was the original pain.
/// <para/>
/// <c>--on-errors stop</c> is always used internally so a failed file surfaces
/// as a non-zero exit; the app-level OnErrors setting ("continue"/"stop") is
/// applied by the engine across files instead.
/// <para/>
/// Secrets go on the command line: this immich-go fork documents no
/// environment-variable alternative for the API keys.
/// </summary>
public static class ImmichGoRunner
{
    private const int PerFileTimeoutMs = 30 * 60 * 1000; // hung uploads must not linger forever

    public static async Task<UploadResult> UploadSingleAsync(UploadRequest req, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "upload",
            "--server", req.ServerUrl,
            "--api-key", req.ApiKey,
            "--on-errors", "stop",
            "--concurrent-tasks", "1",
            "--pause-immich-jobs", req.PauseJobs ? "true" : "false",
            "--device-uuid", req.DeviceUuid,
            "--log-level", "WARN",
        };
        if (!string.IsNullOrEmpty(req.AdminApiKey))
        {
            args.Add("--admin-api-key");
            args.Add(req.AdminApiKey);
        }
        args.Add(req.FilePath);

        ProcessHelper.Result r;
        try
        {
            r = await ProcessHelper.RunAsync(req.ExePath, args, PerFileTimeoutMs, ct).ConfigureAwait(false);
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
