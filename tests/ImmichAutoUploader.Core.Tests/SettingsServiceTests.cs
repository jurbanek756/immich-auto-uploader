using ImmichAutoUploader.Core.Models;
using ImmichAutoUploader.Core.Services;
using Xunit;

namespace ImmichAutoUploader.Core.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsFile;
    private readonly SettingsService _service;

    public SettingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ImmichSettingsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsFile = Path.Combine(_tempDir, "settings.json");
        _service = new SettingsService(_settingsFile);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PersistsSettingsCorrectly()
    {
        var settings = new AppSettings
        {
            ImmichUrl = "https://my-immich.example.com",
            WatchFolder = @"C:\Watch",
            DoneFolder = @"C:\Done",
            ConcurrentTasks = 8,
            BatchIntervalMinutes = 10,
            OnErrors = "stop",
            UseTailscale = true,
            ImmichUrlViaTailscale = "http://100.64.0.1:2283",
        };

        _service.Save(settings);

        Assert.True(File.Exists(_settingsFile));
        Assert.False(File.Exists(_settingsFile + ".tmp"));

        var loaded = _service.Load();

        Assert.Equal("https://my-immich.example.com", loaded.ImmichUrl);
        Assert.Equal(@"C:\Watch", loaded.WatchFolder);
        Assert.Equal(@"C:\Done", loaded.DoneFolder);
        Assert.Equal(8, loaded.ConcurrentTasks);
        Assert.Equal(10, loaded.BatchIntervalMinutes);
        Assert.Equal("stop", loaded.OnErrors);
        Assert.True(loaded.UseTailscale);
        Assert.Equal("http://100.64.0.1:2283", loaded.ImmichUrlViaTailscale);
    }

    [Fact]
    public void Load_CorruptedJson_FallsBackToDefaultSettings()
    {
        File.WriteAllText(_settingsFile, "{ this is corrupted json }}}");

        var loaded = _service.Load();

        Assert.NotNull(loaded);
        Assert.Equal(new AppSettings().ImmichUrl, loaded.ImmichUrl);
    }
}
