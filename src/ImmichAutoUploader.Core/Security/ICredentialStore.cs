namespace ImmichAutoUploader.Core.Security;

/// <summary>
/// Well-known credential slot names.
/// </summary>
public static class CredentialNames
{
    public const string ApiKey = "immich-api-key";
    public const string AdminApiKey = "immich-admin-api-key";
    public const string JellyfinApiKey = "jellyfin-api-key";
}

/// <summary>
/// Abstraction over secret storage so the rest of the app never handles
/// plaintext persistence details.
/// </summary>
public interface ICredentialStore
{
    void Save(string name, string secret);
    string? Load(string name);
    void Delete(string name);
}
