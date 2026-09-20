using System.Text.Json;
using System.Text.RegularExpressions;
using ImmichAutoUploader.Core.Models;

namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// Ensures Tailscale is connected before an upload batch, so the app can
/// reach the Immich server from outside the home LAN.
/// <para/>
/// Deliberate limits: the app only ever brings Tailscale <i>up</i> — it never
/// runs <c>tailscale down</c> or changes the user's VPN configuration.
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

    public sealed record EnsureOutcome(EnsureResult Result, string? Detail);

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
            return new EnsureOutcome(EnsureResult.NotInstalled, exePath);

        string? state = await GetBackendStateAsync(exePath, ct).ConfigureAwait(false);
        if (state == "Running")
            return new EnsureOutcome(EnsureResult.Connected, null);

        AppLogger.Info("Tailscale not connected; running 'tailscale up'.");

        ProcessHelper.Result up;
        try
        {
            up = await ProcessHelper.RunAsync(exePath, new[] { "up" }, 90_000, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new EnsureOutcome(EnsureResult.Failed, ex.Message);
        }

        string combined = up.StdOut + "\n" + up.StdErr;
        string? loginUrl = ExtractLoginUrl(combined);
        if (loginUrl is not null)
            return new EnsureOutcome(EnsureResult.AuthRequired, loginUrl);

        state = await GetBackendStateAsync(exePath, ct).ConfigureAwait(false);
        if (state == "Running")
            return new EnsureOutcome(EnsureResult.Connected, null);

        string detail = ProcessHelper.Tail(combined, 5);
        if (string.IsNullOrWhiteSpace(detail))
            detail = $"tailscale is not connected (state: {state ?? "unknown"})";
        return new EnsureOutcome(EnsureResult.Failed, detail);
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
