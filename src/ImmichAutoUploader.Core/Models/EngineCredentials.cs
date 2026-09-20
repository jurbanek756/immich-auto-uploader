namespace ImmichAutoUploader.Core.Models;

/// <summary>
/// Encapsulates sensitive API keys required by the upload engine for an execution batch.
/// <para/>
/// Credentials are resolved dynamically from <see cref="Security.ICredentialStore"/>
/// immediately prior to batch execution so updates take effect without an application restart.
/// Instances of this record are never written to disk or emitted into log files.
/// </summary>
/// <param name="ImmichApiKey">The primary API key used to authenticate against the Immich server.</param>
/// <param name="ImmichAdminApiKey">Optional admin API key required to pause background server jobs during upload.</param>
/// <param name="JellyfinApiKey">Optional API token used to trigger post-upload Jellyfin library refreshes.</param>
public sealed record EngineCredentials(
    string ImmichApiKey,
    string? ImmichAdminApiKey,
    string? JellyfinApiKey);
