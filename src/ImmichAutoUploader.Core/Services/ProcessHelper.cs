using System.Diagnostics;

namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// Small helper for running external processes (immich-go, tailscale).
/// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/> so no
/// manual quoting is needed — important because API keys may contain
/// characters the shell would mangle.
/// </summary>
internal static class ProcessHelper
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr, bool TimedOut);

    public static async Task<Result> RunAsync(
        string exePath,
        IEnumerable<string> args,
        int timeoutMs,
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

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            if (!process.Start())
                return new Result(-1, string.Empty, "Failed to start process.", false);

            // Kill the process if we're cancelled or the timeout fires.
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
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { /* already gone */ }
            }

            string stdOut = await stdOutTask.ConfigureAwait(false);
            string stdErr = await stdErrTask.ConfigureAwait(false);

            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            bool timedOut = timeoutCts.IsCancellationRequested;
            int exitCode = process.HasExited ? process.ExitCode : -1;
            return new Result(exitCode, stdOut, stdErr, timedOut);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
    }

    /// <summary>Last N non-empty lines of a captured stream, for error reporting.</summary>
    public static string Tail(string text, int lines)
    {
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('\n', all.TakeLast(Math.Max(1, lines)));
    }
}
