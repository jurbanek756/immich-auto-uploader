namespace ImmichAutoUploader.Core.Models;

/// <summary>
/// Secrets the upload engine needs for one batch. Resolved fresh from the
/// credential store for every batch so a Save takes effect immediately.
/// Never persisted to disk or logs.
/// </summary>
public sealed record EngineCredentials(
    string ImmichApiKey,
    string? ImmichAdminApiKey,
    string? JellyfinApiKey);
