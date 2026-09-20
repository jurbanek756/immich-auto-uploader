namespace ImmichAutoUploader.Core.Models;

/// <summary>
/// Represents non-secret application configuration settings persisted as JSON in
/// <c>%AppData%\ImmichAutoUploader\settings.json</c>.
/// <para/>
/// Sensitive secrets (API keys) are deliberately excluded from this class and are
/// managed exclusively by <see cref="Security.ICredentialStore"/> using Windows DPAPI.
/// </summary>
public sealed class AppSettings
{
    // ---- Immich server ----

    /// <summary>
    /// Gets or sets the primary base URL of the Immich server (e.g., <c>https://immich.example.com</c>).
    /// </summary>
    public string ImmichUrl { get; set; } = "http://localhost:2283";

    // ---- Folders ----

    /// <summary>
    /// Gets or sets the absolute path to the directory being watched.
    /// Newly added or modified media files appearing here are queued for upload.
    /// </summary>
    public string WatchFolder { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the absolute path to the directory where files are moved
    /// upon successful upload. Subdirectory structures relative to <see cref="WatchFolder"/>
    /// are preserved. Files are never deleted outright.
    /// </summary>
    public string DoneFolder { get; set; } = string.Empty;

    // ---- Jellyfin (optional) ----

    /// <summary>
    /// Gets or sets the optional base URL for a Jellyfin media server.
    /// When configured, a library refresh is automatically requested following each successful batch.
    /// </summary>
    public string JellyfinUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional exact Jellyfin library name to refresh (e.g., "Photos").
    /// If empty, a full server-wide library refresh (<c>POST /Library/Refresh</c>) is triggered.
    /// </summary>
    public string JellyfinLibraryName { get; set; } = string.Empty;

    // ---- Tailscale (optional) ----

    /// <summary>
    /// Gets or sets a value indicating whether Tailscale should be connected on-demand
    /// before an upload batch and disconnected afterward (only if initiated by this application).
    /// </summary>
    public bool UseTailscale { get; set; } = false;

    /// <summary>
    /// Gets or sets the optional Immich server URL to use when Tailscale is active
    /// (e.g., a Tailscale MagicDNS name or 100.x.y.z IP address).
    /// If blank, <see cref="ImmichUrl"/> is used.
    /// </summary>
    public string ImmichUrlViaTailscale { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional override path to <c>tailscale.exe</c>.
    /// If left blank, the application auto-detects standard installation paths and checks the system PATH.
    /// </summary>
    public string TailscalePath { get; set; } = string.Empty;

    // ---- immich-go (bundled + pinned) ----

    /// <summary>
    /// Gets or sets the path to the <c>immich-go.exe</c> executable.
    /// Defaults to the bundled version located in the application's <c>tools\immich-go\</c> subdirectory.
    /// </summary>
    public string ImmichGoPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the pinned version string of <c>immich-go</c> recorded by <c>build.ps1</c>.
    /// </summary>
    public string ImmichGoVersion { get; set; } = string.Empty;

    // ---- Upload engine tuning ----

    /// <summary>
    /// Gets or sets the maximum number of concurrent <c>immich-go</c> upload processes spawned during a batch.
    /// Clamped between 1 and 20. Defaults to 4.
    /// </summary>
    public int ConcurrentTasks { get; set; } = 4;

    /// <summary>
    /// Gets or sets the batch error handling policy across files:
    /// <list type="bullet">
    ///   <item><term>"continue"</term><description>Log the failure and proceed with remaining files in the batch.</description></item>
    ///   <item><term>"stop"</term><description>Halt the current batch immediately upon the first failure.</description></item>
    /// </list>
    /// </summary>
    public string OnErrors { get; set; } = "continue";

    /// <summary>
    /// Gets or sets a value indicating whether Immich background jobs (thumbnails, ML encoding)
    /// should be paused during upload via <c>--pause-immich-jobs</c>. Requires an Admin API key.
    /// </summary>
    public bool PauseImmichJobs { get; set; } = true;

    /// <summary>
    /// Gets or sets the interval in minutes between automated upload batches. Minimum is 1.
    /// </summary>
    public int BatchIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum number of files dequeued in a single batch execution.
    /// </summary>
    public int MaxFilesPerBatch { get; set; } = 100;

    /// <summary>
    /// Gets or sets the stable per-PC device identifier sent to Immich as <c>--device-uuid</c>.
    /// Generated on initial launch to distinguish uploads from this machine.
    /// </summary>
    public string DeviceUuid { get; set; } = string.Empty;

    // ---- General ----

    /// <summary>
    /// Gets or sets a value indicating whether the application automatically launches
    /// on Windows logon via the CurrentUser Run registry key.
    /// </summary>
    public bool StartWithWindows { get; set; } = true;
}
