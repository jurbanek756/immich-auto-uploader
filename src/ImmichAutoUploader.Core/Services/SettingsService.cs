using System.Text.Json;
using ImmichAutoUploader.Core.Models;

namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// Loads/saves non-secret settings as JSON in %AppData%\ImmichAutoUploader\settings.json.
/// Secrets (API keys) are handled separately by <see cref="Security.ICredentialStore"/>.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ImmichAutoUploader", "settings.json");

    public string SettingsPath { get; }

    public SettingsService(string? customPath = null)
    {
        SettingsPath = customPath ?? DefaultSettingsPath;
    }

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
            // Corrupt settings file: fall through to defaults rather than crashing.
        }
        return new AppSettings();
    }

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
