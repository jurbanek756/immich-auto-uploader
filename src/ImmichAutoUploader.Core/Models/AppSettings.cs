namespace ImmichAutoUploader.Core.Models;

/// <summary>
/// Non-secret application settings. Persisted as JSON in %AppData%.
/// API keys are NOT stored here — they live in <see cref="Security.ICredentialStore"/>
/// (Windows DPAPI), never in plaintext.
/// </summary>
public sealed class AppSettings
{
    // ---- Immich server ----
    public string ImmichUrl { get; set; } = "https://immich.turbaneks.duckdns.org";

    // ---- Folders ----
    /// <summary>Folder being watched. New photos/videos appearing here get uploaded.</summary>
    public string WatchFolder { get; set; } = string.Empty;

    /// <summary>
    /// Files are MOVED here after a successful upload (never deleted outright).
    /// </summary>
    public string DoneFolder { get; set; } = string.Empty;

    // ---- Jellyfin (optional) ----
    /// <summary>
    /// Optional. When set, a Jellyfin library refresh is triggered after each
    /// successful upload batch so new photos appear without a manual scan.
    /// </summary>
    public string JellyfinUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional. Exact Jellyfin library name to refresh (e.g. "Family Photos").
    /// Blank = full library scan (POST /Library/Refresh).
    /// </summary>
    public string JellyfinLibraryName { get; set; } = string.Empty;

    // ---- Tailscale (optional) ----
    /// <summary>
    /// When true, the app ensures Tailscale is connected before each upload batch
    /// (runs "tailscale up" if needed) and disconnects it afterwards ("tailscale down"),
    /// but only when the app itself established the connection.
    /// </summary>
    public bool UseTailscale { get; set; } = false;

    /// <summary>
    /// Optional. Immich server URL to use when Tailscale is active
    /// (e.g. http://100.x.y.z:2283 or a MagicDNS name). Blank = keep using ImmichUrl.
    /// </summary>
    public string ImmichUrlViaTailscale { get; set; } = string.Empty;

    /// <summary>Optional. Override path to tailscale.exe; auto-detected if blank.</summary>
    public string TailscalePath { get; set; } = string.Empty;

    // ---- immich-go (bundled + pinned) ----
    public string ImmichGoPath { get; set; } = string.Empty;
    public string ImmichGoVersion { get; set; } = string.Empty; // e.g. "v0.0.0" pinned by build.ps1

    // ---- Upload engine tuning ----
    public int ConcurrentTasks { get; set; } = 4;
    public string OnErrors { get; set; } = "continue"; // "continue" | "stop"
    public bool PauseImmichJobs { get; set; } = true;  // needs admin API key
    public int BatchIntervalMinutes { get; set; } = 5;
    public int MaxFilesPerBatch { get; set; } = 100;

    /// <summary>
    /// Stable per-PC device identifier sent to Immich as --device-uuid.
    /// Auto-generated on first run so this PC is distinguishable from
    /// manual immich-go runs and other machines.
    /// </summary>
    public string DeviceUuid { get; set; } = string.Empty;

    // ---- General ----
    public bool StartWithWindows { get; set; } = true;
}
