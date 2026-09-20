namespace ImmichAutoUploader.Core.Security;

/// <summary>
/// Defines well-known credential identifier constants used throughout the application.
/// </summary>
public static class CredentialNames
{
    /// <summary>
    /// Identifier for the primary Immich server API key.
    /// </summary>
    public const string ApiKey = "immich-api-key";

    /// <summary>
    /// Identifier for the optional Immich server administrator API key (used to pause jobs).
    /// </summary>
    public const string AdminApiKey = "immich-admin-api-key";

    /// <summary>
    /// Identifier for the optional Jellyfin server API key (used to trigger library refreshes).
    /// </summary>
    public const string JellyfinApiKey = "jellyfin-api-key";
}

/// <summary>
/// Provides an abstraction for securely storing, retrieving, and deleting sensitive credentials.
/// Implementations must guarantee that secrets are encrypted at rest.
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// Encrypts and persists a secret under the specified credential name.
    /// </summary>
    /// <param name="name">The unique identifier for the credential (e.g., <see cref="CredentialNames.ApiKey"/>).</param>
    /// <param name="secret">The plaintext secret value to protect.</param>
    void Save(string name, string secret);

    /// <summary>
    /// Decrypts and retrieves a secret by its credential name.
    /// </summary>
    /// <param name="name">The unique identifier for the credential.</param>
    /// <returns>The decrypted plaintext secret, or <c>null</c> if no credential exists under that name.</returns>
    string? Load(string name);

    /// <summary>
    /// Removes a persisted secret from storage if it exists.
    /// </summary>
    /// <param name="name">The unique identifier for the credential.</param>
    void Delete(string name);
}
