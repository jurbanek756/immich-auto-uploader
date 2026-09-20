using System.Text.Json;
using System.Text.RegularExpressions;
using ImmichAutoUploader.Core.Models;

namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// Ensures Tailscale is connected before an upload batch, so the app can
/// reach the Immich server from outside the home LAN, and disconnects it
/// again once the batch (including the Jellyfin refresh) is done.
/// <para/>
/// Deliberate limit: the app only ever tears down a connection it established
/// itself (see <see cref="EnsureOutcome.ConnectedByUs"/>). If Tailscale was
/// already up — e.g. the user connected it manually — the app leaves it alone.
/// It never logs the device out or changes other VPN configuration.
/// </summary>
public static class TailscaleService
{
    public enum EnsureResult
    {
        Connected,
        NotInstalled,
        AuthRequired, // device isn't logged in; Detail carries the login URL when known
        Failed,
    }

    /// <summary>
    /// <c>ConnectedByUs</c> is true only when this call ran <c>tailscale up</c>
    /// itself — the caller should run <c>tailscale down</c> afterwards.
    /// </summary>
    public sealed record EnsureOutcome(EnsureResult Result, string? Detail, bool ConnectedByUs);

    /// <summary>
    /// Locate tailscale.exe: explicit setting, then the default install
    /// location, then PATH.
    /// </summary>
    public static async Task<string?> ResolveExePathAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(settings.TailscalePath) && File.Exists(settings.TailscalePath))
            return settings.TailscalePath;

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string def = Path.Combine(programFiles, "Tailscale", "tailscale.exe");
        if (File.Exists(def))
            return def;

        try
        {
            var r = await ProcessHelper.RunAsync("where.exe", new[] { "tailscale" }, 10_000, ct).ConfigureAwait(false);
            if (r.ExitCode == 0)
            {
                string? first = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                if (first is not null && File.Exists(first))
                    return first;
            }
        }
        catch { /* fall through */ }

        return null;
    }

    public static async Task<EnsureOutcome> EnsureConnectedAsync(string exePath, CancellationToken ct = default)
    {
        if (!File.Exists(exePath))
            return new EnsureOutcome(EnsureResult.NotInstalled, exePath, false);

        string? state = await GetBackendStateAsync(exePath, ct).ConfigureAwait(false);
        if (state == "Running")
            return new EnsureOutcome(EnsureResult.Connected, null, false);

        AppLogger.Info("Tailscale not connected; running 'tailscale up'.");

        ProcessHelper.Result up;
        try
        {
            up = await ProcessHelper.RunAsync(exePath, new[] { "up" }, 90_000, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new EnsureOutcome(EnsureResult.Failed, ex.Message, false);
        }

        string combined = up.StdOut + "\n" + up.StdErr;
        string? loginUrl = ExtractLoginUrl(combined);
        if (loginUrl is not null)
            return new EnsureOutcome(EnsureResult.AuthRequired, loginUrl, false);

        state = await GetBackendStateAsync(exePath, ct).ConfigureAwait(false);
        if (state == "Running")
            return new EnsureOutcome(EnsureResult.Connected, null, true);

        string detail = ProcessHelper.Tail(combined, 5);
        if (string.IsNullOrWhiteSpace(detail))
            detail = $"tailscale is not connected (state: {state ?? "unknown"})";
        return new EnsureOutcome(EnsureResult.Failed, detail, false);
    }

    /// <summary>
    /// Bring Tailscale back down after a batch. Only call this when the app
    /// itself ran <c>tailscale up</c> (see <see cref="EnsureOutcome.ConnectedByUs"/>) —
    /// never tear down a connection the user established themselves.
    /// Failures are logged, never thrown.
    /// </summary>
    public static async Task DisconnectAsync(string exePath, CancellationToken ct = default)
    {
        try
        {
            AppLogger.Info("Running 'tailscale down' (this batch connected it).");
            var r = await ProcessHelper.RunAsync(exePath, new[] { "down" }, 30_000, ct).ConfigureAwait(false);
            string? state = await GetBackendStateAsync(exePath, ct).ConfigureAwait(false);
            if (r.ExitCode == 0 && !string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase))
                AppLogger.Info("Tailscale disconnected.");
            else
                AppLogger.Warn($"'tailscale down' may not have completed (exit {r.ExitCode}, state: {state ?? "unknown"}).");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Could not disconnect Tailscale: {ex.Message}");
        }
    }

    private static async Task<string?> GetBackendStateAsync(string exePath, CancellationToken ct)
    {
        try
        {
            var r = await ProcessHelper.RunAsync(exePath, new[] { "status", "--json" }, 15_000, ct).ConfigureAwait(false);
            if (r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.StdOut))
            {
                using var doc = JsonDocument.Parse(r.StdOut);
                if (doc.RootElement.TryGetProperty("BackendState", out var bs))
                    return bs.GetString();
            }
        }
        catch { /* fall through to text heuristic */ }

        // Fallback: plain `tailscale status` exits non-zero / mentions login when logged out.
        try
        {
            var r = await ProcessHelper.RunAsync(exePath, new[] { "status" }, 15_000, ct).ConfigureAwait(false);
            string text = r.StdOut + r.StdErr;
            if (text.Contains("Logged out", StringComparison.OrdinalIgnoreCase))
                return "LoggedOut";
            if (r.ExitCode == 0)
                return "Running"; // best-effort guess
        }
        catch { }

        return null;
    }

    private static string? ExtractLoginUrl(string text)
    {
        var m = Regex.Match(text, @"https?://login\.tailscale\.com\S+", RegexOptions.IgnoreCase);
        return m.Success ? m.Value.TrimEnd('.', ',', ')') : null;
    }
}
