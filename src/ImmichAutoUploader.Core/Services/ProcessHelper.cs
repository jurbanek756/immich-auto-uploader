using System.Diagnostics;

namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// Utility helper for executing external command-line executables (<c>immich-go</c>, <c>tailscale</c>, <c>where.exe</c>).
/// <para/>
/// <b>Safety Features:</b>
/// <list type="bullet">
///   <item><description>All arguments are passed through <see cref="ProcessStartInfo.ArgumentList"/> to eliminate command-line injection and quoting bugs.</description></item>
///   <item><description>Custom environment variables can be injected without exposing them to system-wide scopes.</description></item>
///   <item><description>Supports linked cancellation tokens and timeout limits, killing the entire process tree on cancellation.</description></item>
/// </list>
/// </summary>
internal static class ProcessHelper
{
    /// <summary>
    /// Encapsulates the execution results of an external process.
    /// </summary>
    /// <param name="ExitCode">The process exit code, or -1 if the process failed to start or timed out.</param>
    /// <param name="StdOut">Captured standard output stream text.</param>
    /// <param name="StdErr">Captured standard error stream text.</param>
    /// <param name="TimedOut"><c>true</c> if execution was aborted due to exceeding the timeout threshold; otherwise, <c>false</c>.</param>
    public sealed record Result(int ExitCode, string StdOut, string StdErr, bool TimedOut);

    /// <summary>
    /// Asynchronously runs an external process with a specified timeout.
    /// </summary>
    /// <param name="exePath">The absolute path or executable name to launch.</param>
    /// <param name="args">The list of discrete command-line arguments.</param>
    /// <param name="timeoutMs">The maximum execution duration in milliseconds before terminating the process tree.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A <see cref="Result"/> containing exit code and captured output streams.</returns>
    public static Task<Result> RunAsync(
        string exePath,
        IEnumerable<string> args,
        int timeoutMs,
        CancellationToken ct = default) =>
        RunAsync(exePath, args, timeoutMs, environment: null, ct);

    /// <summary>
    /// Asynchronously runs an external process with custom environment variables and a timeout.
    /// </summary>
    /// <param name="exePath">The absolute path or executable name to launch.</param>
    /// <param name="args">The list of discrete command-line arguments.</param>
    /// <param name="timeoutMs">The maximum execution duration in milliseconds before terminating the process tree.</param>
    /// <param name="environment">Optional dictionary of environment variables to set for the child process.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A <see cref="Result"/> containing exit code and captured output streams.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public static async Task<Result> RunAsync(
        string exePath,
        IEnumerable<string> args,
        int timeoutMs,
        IDictionary<string, string>? environment,
        CancellationToken ct = default)
    {
        using var process = new Process();
        process.StartInfo.FileName = exePath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        foreach (string a in args)
            process.StartInfo.ArgumentList.Add(a);

        if (environment != null)
        {
            foreach (var (k, v) in environment)
                process.StartInfo.EnvironmentVariables[k] = v;
        }

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            try
            {
                if (!process.Start())
                    return new Result(-1, string.Empty, "Failed to start process.", false);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return new Result(-1, string.Empty, $"Failed to start process: {ex.Message}", false);
            }

            // Terminate the process tree if cancellation or timeout occurs
            using var _ = linkedCts.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
            });

            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // When cancellation/timeout fires, the register callback kills the process tree.
                // Ensure the process has completely exited so output pipes close cleanly.
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch { /* already gone or timed out */ }
            }

            string stdOut = string.Empty;
            string stdErr = string.Empty;
            try
            {
                stdOut = await stdOutTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch { /* best effort on timeout / cancellation */ }

            try
            {
                stdErr = await stdErrTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch { /* best effort on timeout / cancellation */ }

            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            bool timedOut = timeoutCts.IsCancellationRequested;
            int exitCode = timedOut ? -1 : (process.HasExited ? process.ExitCode : -1);
            return new Result(exitCode, stdOut, stdErr, timedOut);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
    }

    /// <summary>
    /// Extracts the last N non-empty lines of text from a captured stream for compact diagnostic reporting.
    /// </summary>
    /// <param name="text">The stream text to trim.</param>
    /// <param name="lines">The maximum number of trailing lines to return.</param>
    /// <returns>A string containing at most <paramref name="lines"/> trailing lines joined by newlines.</returns>
    public static string Tail(string text, int lines)
    {
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('\n', all.TakeLast(Math.Max(1, lines)));
    }
}
