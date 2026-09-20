using System.Text.Json;
using ImmichAutoUploader.Core.Models;

namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// Manages serialization and deserialization of non-secret application settings to and from
/// <c>%AppData%\ImmichAutoUploader\settings.json</c>.
/// <para/>
/// Sensitive credentials (API keys) are excluded and managed by <see cref="Security.ICredentialStore"/>.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Gets the standard file system path for application settings in the user's AppData directory.
    /// </summary>
    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ImmichAutoUploader", "settings.json");

    /// <summary>
    /// Gets the active file path targeted by this service instance.
    /// </summary>
    public string SettingsPath { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsService"/> class.
    /// </summary>
    /// <param name="customPath">Optional custom file path for settings. If null, <see cref="DefaultSettingsPath"/> is used.</param>
    public SettingsService(string? customPath = null)
    {
        SettingsPath = customPath ?? DefaultSettingsPath;
    }

    /// <summary>
    /// Reads and deserializes application settings from disk.
    /// </summary>
    /// <returns>
    /// The loaded <see cref="AppSettings"/> instance, or a new instance with default values
    /// if the file does not exist or cannot be deserialized.
    /// </returns>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is not null)
                    return settings;
            }
        }
        catch
        {
            // Corrupt or inaccessible settings file: fall through to defaults rather than crashing.
        }
        return new AppSettings();
    }

    /// <summary>
    /// Serializes and writes application settings to disk using an atomic temporary-file swap.
    /// </summary>
    /// <param name="settings">The <see cref="AppSettings"/> instance to persist.</param>
    public void Save(AppSettings settings)
    {
        string? dir = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string tempPath = SettingsPath + ".tmp";
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, SettingsPath, overwrite: true);
    }
}
