using System.Security.Cryptography;
using System.Text;

namespace ImmichAutoUploader.Core.Security;

/// <summary>
/// Windows DPAPI credential store (CurrentUser scope).
/// Secrets are encrypted by the OS and stored as opaque blobs under %AppData%.
/// Only the same Windows user on the same machine can decrypt them.
/// </summary>
public sealed class DpapiCredentialStore : ICredentialStore
{
    private static string SecretsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ImmichAutoUploader", "secrets");

    private static string PathFor(string name) => Path.Combine(SecretsDir, name + ".bin");

    public void Save(string name, string secret)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI credential store requires Windows.");

        Directory.CreateDirectory(SecretsDir);
        byte[] protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret), optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(name), protectedBytes);
    }

    public string? Load(string name)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI credential store requires Windows.");

        string path = PathFor(name);
        if (!File.Exists(path))
            return null;

        byte[] protectedBytes = File.ReadAllBytes(path);
        byte[] clearBytes = ProtectedData.Unprotect(
            protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(clearBytes);
    }

    public void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path))
            File.Delete(path);
    }
}
