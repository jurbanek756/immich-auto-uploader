using System.Security.Cryptography;
using System.Text;

namespace ImmichAutoUploader.Core.Security;

/// <summary>
/// Windows Data Protection API (DPAPI) implementation of <see cref="ICredentialStore"/>.
/// <para/>
/// Credentials are encrypted using the current user's Windows credentials via
/// <see cref="DataProtectionScope.CurrentUser"/> and stored as opaque binary files in
/// <c>%AppData%\ImmichAutoUploader\secrets\</c>. Only processes running under the
/// same Windows user account on the same physical computer can decrypt them.
/// </summary>
public sealed class DpapiCredentialStore : ICredentialStore
{
    /// <summary>
    /// Gets the absolute directory path where encrypted credential blobs are stored.
    /// </summary>
    private static string SecretsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ImmichAutoUploader", "secrets");

    /// <summary>
    /// Computes the absolute file path for a named credential blob.
    /// </summary>
    /// <param name="name">The unique credential slot name.</param>
    /// <returns>The path ending in <c>.bin</c> within <see cref="SecretsDir"/>.</returns>
    private static string PathFor(string name) => Path.Combine(SecretsDir, name + ".bin");

    /// <summary>
    /// Encrypts the provided secret using Windows DPAPI and atomically writes it to disk.
    /// </summary>
    /// <param name="name">The unique identifier for the credential.</param>
    /// <param name="secret">The plaintext secret value to encrypt.</param>
    /// <exception cref="PlatformNotSupportedException">Thrown when executed on a non-Windows operating system.</exception>
    public void Save(string name, string secret)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI credential store requires Windows.");

        Directory.CreateDirectory(SecretsDir);
        byte[] protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret), optionalEntropy: null, DataProtectionScope.CurrentUser);
        string destPath = PathFor(name);
        string tempPath = destPath + ".tmp";
        File.WriteAllBytes(tempPath, protectedBytes);
        File.Move(tempPath, destPath, overwrite: true);
    }

    /// <summary>
    /// Reads and decrypts a DPAPI-protected secret from disk.
    /// </summary>
    /// <param name="name">The unique identifier for the credential.</param>
    /// <returns>The decrypted plaintext string, or <c>null</c> if the credential file does not exist or decryption fails.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown when executed on a non-Windows operating system.</exception>
    public string? Load(string name)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI credential store requires Windows.");

        string path = PathFor(name);
        if (!File.Exists(path))
            return null;

        try
        {
            byte[] protectedBytes = File.ReadAllBytes(path);
            if (protectedBytes.Length == 0)
                return null;

            byte[] clearBytes = ProtectedData.Unprotect(
                protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clearBytes);
        }
        catch (CryptographicException)
        {
            // Decryption failed (e.g., file was transferred from another machine or user).
            return null;
        }
    }

    /// <summary>
    /// Deletes the encrypted credential file associated with the specified name.
    /// </summary>
    /// <param name="name">The unique identifier for the credential.</param>
    public void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path))
            File.Delete(path);
    }
}
