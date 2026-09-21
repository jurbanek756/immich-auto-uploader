using System.Runtime.InteropServices;

namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// Represents the parameters required to execute a single-file upload using <c>immich-go</c>.
/// </summary>
/// <param name="ExePath">The absolute path to the <c>immich-go.exe</c> binary.</param>
/// <param name="ServerUrl">The destination Immich server URL.</param>
/// <param name="ApiKey">The Immich API key (passed via <c>--api-key</c> and environment variable).</param>
/// <param name="AdminApiKey">Optional Immich admin API key (passed via <c>--admin-api-key</c> and environment variable).</param>
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
/// <c>immich-go</c> natively requires a directory as its input path. To maintain deterministic per-file
/// accounting and isolate errors to individual assets, <see cref="ImmichGoRunner"/> provisions an ephemeral
/// single-asset staging directory for each invocation via zero-cost hard links (with symlink/copy fallbacks),
/// executes <c>immich-go</c> against that isolated directory, and cleans up the staging container on completion.
/// </summary>
public static class ImmichGoRunner
{
    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

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
        string? stagingDir = null;
        try
        {
            string fileDir = Path.GetDirectoryName(req.FilePath) ?? Path.GetTempPath();
            string fileName = Path.GetFileName(req.FilePath);

            try
            {
                // Prefix with '$' so FileWatcherService recognizes it as temporary and ignores it
                stagingDir = Path.Combine(fileDir, $"$immich_stage_{Guid.NewGuid():N}");
                Directory.CreateDirectory(stagingDir);
            }
            catch
            {
                stagingDir = Path.Combine(Path.GetTempPath(), "ImmichAutoUploader", $"$immich_stage_{Guid.NewGuid():N}");
                Directory.CreateDirectory(stagingDir);
            }

            string stagedFile = Path.Combine(stagingDir, fileName);
            if (File.Exists(req.FilePath))
            {
                LinkOrCopy(req.FilePath, stagedFile);

                // Also stage sidecar metadata files if present (e.g., photo.jpg.xmp or photo.xmp)
                string sidecar1 = req.FilePath + ".xmp";
                if (File.Exists(sidecar1))
                {
                    LinkOrCopy(sidecar1, Path.Combine(stagingDir, Path.GetFileName(sidecar1)));
                }
                string sidecar2 = Path.ChangeExtension(req.FilePath, ".xmp");
                if (!string.Equals(sidecar1, sidecar2, StringComparison.OrdinalIgnoreCase) && File.Exists(sidecar2))
                {
                    LinkOrCopy(sidecar2, Path.Combine(stagingDir, Path.GetFileName(sidecar2)));
                }
            }

            var args = new List<string>
            {
                "upload",
                $"--server={req.ServerUrl}",
                $"--api-key={req.ApiKey}",
                "--on-errors=stop", // Force non-zero exit on file failure so the engine detects it
                "--concurrent-tasks=1", // Concurrency is managed at the process level by UploadEngine
                $"--pause-immich-jobs={(req.PauseJobs ? "true" : "false")}",
                "--log-level=WARN",
            };

            if (!string.IsNullOrEmpty(req.AdminApiKey))
            {
                args.Add($"--admin-api-key={req.AdminApiKey}");
            }

            if (!string.IsNullOrWhiteSpace(req.DeviceUuid))
            {
                args.Add($"--device-uuid={req.DeviceUuid}");
            }

            args.Add(stagingDir);

            // Also populate environment variables as secondary fallback
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

            string raw = (r.StdErr + "\n" + r.StdOut).Trim();
            // If immich-go dumped usage help alongside an error, filter out help flags to expose the root error
            var errLines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                              .Where(l => !l.StartsWith("--") &&
                                          !l.StartsWith("Usage:") &&
                                          !l.StartsWith("Flags:") &&
                                          !l.StartsWith("Global Flags:") &&
                                          !l.StartsWith("||") &&
                                          !l.StartsWith(". _") &&
                                          !l.StartsWith("v 2."))
                              .ToList();

            string detail = errLines.Count > 0
                ? string.Join('\n', errLines.TakeLast(6))
                : ProcessHelper.Tail(raw, 6);

            if (string.IsNullOrWhiteSpace(detail))
                detail = $"exit code {r.ExitCode} with no output";
            return new UploadResult(false, r.ExitCode, detail);
        }
        finally
        {
            if (stagingDir != null && Directory.Exists(stagingDir))
            {
                try
                {
                    Directory.Delete(stagingDir, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup
                }
            }
        }
    }

    /// <summary>
    /// Links an asset into the staging directory using zero-cost NTFS hard links, falling back to symlinks or copy.
    /// </summary>
    private static void LinkOrCopy(string sourceFile, string destFile)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (CreateHardLink(destFile, sourceFile, IntPtr.Zero))
                    return;
            }
            catch
            {
                // Fall through to symlink / copy
            }

            try
            {
                File.CreateSymbolicLink(destFile, sourceFile);
                return;
            }
            catch
            {
                // Fall through to copy
            }
        }

        File.Copy(sourceFile, destFile, overwrite: true);
    }
}
