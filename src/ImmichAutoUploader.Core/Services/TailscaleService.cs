using System.Text.Json;
using System.Text.RegularExpressions;
using ImmichAutoUploader.Core.Models;

namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// Manages on-demand Tailscale VPN connectivity before and after upload batches,
/// enabling remote synchronization with an Immich instance from outside the home network.
/// <para/>
/// <b>Connection Ownership Contract:</b>
/// The application strictly tracks whether it established the connection itself (<see cref="EnsureOutcome.ConnectedByUs"/>).
/// If Tailscale was already active prior to a batch (e.g., connected manually by the user), the application
/// will NOT disconnect it when the batch finishes.
/// </summary>
public static class TailscaleService
{
    /// <summary>
    /// Represents the status outcome of attempting to ensure Tailscale connectivity.
    /// </summary>
    public enum EnsureResult
    {
        /// <summary>
        /// Tailscale is connected and operational (<c>BackendState == "Running"</c>).
        /// </summary>
        Connected,

        /// <summary>
        /// The <c>tailscale.exe</c> binary was not found at the configured or standard paths.
        /// </summary>
        NotInstalled,

        /// <summary>
        /// The machine is logged out or requires interactive web authorization.
        /// When available, the authentication URL is provided in <see cref="EnsureOutcome.Detail"/>.
        /// </summary>
        AuthRequired,

        /// <summary>
        /// The connection attempt failed or the backend did not reach running state within the timeout.
        /// </summary>
        Failed,
    }

    /// <summary>
    /// Represents the detailed outcome of an <see cref="EnsureConnectedAsync"/> call.
    /// </summary>
    /// <param name="Result">The high-level status outcome.</param>
    /// <param name="Detail">Diagnostic message, error detail, or login URL.</param>
    /// <param name="ConnectedByUs"><c>true</c> only if this invocation initiated the connection via <c>tailscale up</c>.</param>
    public sealed record EnsureOutcome(EnsureResult Result, string? Detail, bool ConnectedByUs);

    /// <summary>
    /// Discovers the absolute path to <c>tailscale.exe</c> by checking explicit settings,
    /// standard <c>%ProgramFiles%\Tailscale\</c> installation directories, and system PATH via <c>where.exe</c>.
    /// </summary>
    /// <param name="settings">Application configuration holding optional custom paths.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The resolved executable path, or <c>null</c> if not found.</returns>
    public static async Task<string?> ResolveExePathAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(settings.TailscalePath) && File.Exists(settings.TailscalePath))
            return settings.TailscalePath;

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string def = Path.Combine(programFiles, "Tailscale", "tailscale.exe");
        if (File.Exists(def))
            return def;

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string localDef = Path.Combine(localAppData, "Tailscale", "tailscale.exe");
        if (File.Exists(localDef))
            return localDef;

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

    /// <summary>
    /// Verifies that Tailscale is connected, executing <c>tailscale up</c> if disconnected.
    /// </summary>
    /// <param name="exePath">The absolute path to <c>tailscale.exe</c>.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>An <see cref="EnsureOutcome"/> detailing the connection status and ownership.</returns>
    public static async Task<EnsureOutcome> EnsureConnectedAsync(string exePath, CancellationToken ct = default)
    {
        if (!File.Exists(exePath))
            return new EnsureOutcome(EnsureResult.NotInstalled, exePath, false);

        var (state, authUrl) = await GetBackendStateAsync(exePath, ct).ConfigureAwait(false);
        if (state == "Running")
            return new EnsureOutcome(EnsureResult.Connected, null, false);

        if (string.Equals(state, "NeedsLogin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "LoggedOut", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "NeedsMachineAuth", StringComparison.OrdinalIgnoreCase))
        {
            return new EnsureOutcome(EnsureResult.AuthRequired, authUrl, false);
        }

        AppLogger.Info("Tailscale not connected; running 'tailscale up'.");

        ProcessHelper.Result up;
        try
        {
            up = await ProcessHelper.RunAsync(exePath, new[] { "up", "--timeout=10s" }, 15_000, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new EnsureOutcome(EnsureResult.Failed, ex.Message, false);
        }

        string combined = up.StdOut + "\n" + up.StdErr;
        string? loginUrl = ExtractLoginUrl(combined) ?? authUrl;
        if (loginUrl is not null)
            return new EnsureOutcome(EnsureResult.AuthRequired, loginUrl, false);

        var (newState, _) = await GetBackendStateAsync(exePath, ct).ConfigureAwait(false);
        if (newState == "Running")
            return new EnsureOutcome(EnsureResult.Connected, null, true);

        string detail = ProcessHelper.Tail(combined, 5);
        if (string.IsNullOrWhiteSpace(detail))
            detail = $"tailscale is not connected (state: {newState ?? "unknown"})";
        return new EnsureOutcome(EnsureResult.Failed, detail, false);
    }

    /// <summary>
    /// Disconnects Tailscale by executing <c>tailscale down</c>.
    /// <para/>
    /// Callers must only invoke this when <see cref="EnsureOutcome.ConnectedByUs"/> is <c>true</c>.
    /// </summary>
    /// <param name="exePath">The absolute path to <c>tailscale.exe</c>.</param>
    /// <param name="ct">A cancellation token.</param>
    public static async Task DisconnectAsync(string exePath, CancellationToken ct = default)
    {
        try
        {
            AppLogger.Info("Running 'tailscale down' (this batch connected it).");
            var r = await ProcessHelper.RunAsync(exePath, new[] { "down" }, 30_000, ct).ConfigureAwait(false);
            var (state, _) = await GetBackendStateAsync(exePath, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Queries <c>tailscale status --json</c> to extract the current backend state and optional auth URL.
    /// </summary>
    private static async Task<(string? State, string? AuthUrl)> GetBackendStateAsync(string exePath, CancellationToken ct)
    {
        try
        {
            var r = await ProcessHelper.RunAsync(exePath, new[] { "status", "--json" }, 15_000, ct).ConfigureAwait(false);
            if (r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.StdOut))
            {
                using var doc = JsonDocument.Parse(r.StdOut);
                string? state = doc.RootElement.TryGetProperty("BackendState", out var bs) ? bs.GetString() : null;
                string? authUrl = doc.RootElement.TryGetProperty("AuthURL", out var au) ? au.GetString() : null;
                return (state, authUrl);
            }
        }
        catch { /* fall through to text heuristic */ }

        // Fallback: plain `tailscale status` exits non-zero / mentions login when logged out.
        try
        {
            var r = await ProcessHelper.RunAsync(exePath, new[] { "status" }, 15_000, ct).ConfigureAwait(false);
            string text = r.StdOut + r.StdErr;
            if (text.Contains("Logged out", StringComparison.OrdinalIgnoreCase))
                return ("LoggedOut", ExtractLoginUrl(text));
            if (r.ExitCode == 0)
                return ("Running", null); // best-effort guess
        }
        catch { }

        return (null, null);
    }

    /// <summary>
    /// Parses CLI output using regular expressions to extract Tailscale browser authorization URLs.
    /// </summary>
    /// <param name="text">Console output text from <c>tailscale up</c> or <c>tailscale status</c>.</param>
    /// <returns>The matched URL string, or <c>null</c> if none matched.</returns>
    internal static string? ExtractLoginUrl(string text)
    {
        var m = Regex.Match(text, @"https?://login\.tailscale\.com\S+", RegexOptions.IgnoreCase);
        return m.Success ? m.Value.TrimEnd('.', ',', ')') : null;
    }
}
