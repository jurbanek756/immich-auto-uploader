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

            var stdOutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stdErrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
            await Task.WhenAll(stdOutTask, stdErrTask).ConfigureAwait(false);
            string stdOut = await stdOutTask.ConfigureAwait(false);
            string stdErr = await stdErrTask.ConfigureAwait(false);
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);

            bool timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            return new Result(process.ExitCode, stdOut, stdErr, timedOut);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new Result(-1, string.Empty, "Process timed out.", true);
        }
    }

    /// <summary>Last N non-empty lines of a captured stream, for error reporting.</summary>
    public static string Tail(string text, int lines)
    {
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('\n', all.TakeLast(Math.Max(1, lines)));
    }
}
