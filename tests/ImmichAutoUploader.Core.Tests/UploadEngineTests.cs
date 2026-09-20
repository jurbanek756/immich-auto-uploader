using ImmichAutoUploader.Core.Models;
using ImmichAutoUploader.Core.Services;
using Xunit;

namespace ImmichAutoUploader.Core.Tests;

public class UploadEngineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _watchDir;
    private readonly string _doneDir;

    public UploadEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ImmichAutoUploader_EngineTests_" + Guid.NewGuid().ToString("N"));
        _watchDir = Path.Combine(_tempDir, "watch");
        _doneDir = Path.Combine(_tempDir, "done");
        Directory.CreateDirectory(_watchDir);
        Directory.CreateDirectory(_doneDir);
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
    public void RedactSecrets_RedactsAllConfiguredKeys()
    {
        var creds = new EngineCredentials("secret-immich-key", "secret-admin-key", "secret-jellyfin-key");
        string input = "Errors: key=secret-immich-key, admin=secret-admin-key, jf=secret-jellyfin-key in output";

        string redacted = UploadEngine.RedactSecrets(input, creds);

        Assert.DoesNotContain("secret-immich-key", redacted);
        Assert.DoesNotContain("secret-admin-key", redacted);
        Assert.DoesNotContain("secret-jellyfin-key", redacted);
        Assert.Equal("Errors: key=[REDACTED], admin=[REDACTED], jf=[REDACTED] in output", redacted);
    }

    [Fact]
    public async Task MoveToDoneAsync_PreservesRelativePath()
    {
        string subDir = Path.Combine(_watchDir, "2026", "vacation");
        Directory.CreateDirectory(subDir);
        string sourceFile = Path.Combine(subDir, "photo.jpg");
        File.WriteAllBytes(sourceFile, new byte[] { 1, 2, 3 });

        var settings = new AppSettings
        {
            WatchFolder = _watchDir,
            DoneFolder = _doneDir,
        };

        await UploadEngine.MoveToDoneAsync(sourceFile, settings);

        string expectedDest = Path.Combine(_doneDir, "2026", "vacation", "photo.jpg");
        Assert.False(File.Exists(sourceFile));
        Assert.True(File.Exists(expectedDest));
    }

    [Fact]
    public async Task MoveToDoneAsync_HandlesNameCollisions()
    {
        string sourceFile1 = Path.Combine(_watchDir, "photo.jpg");
        File.WriteAllBytes(sourceFile1, new byte[] { 1 });

        var settings = new AppSettings
        {
            WatchFolder = _watchDir,
            DoneFolder = _doneDir,
        };

        await UploadEngine.MoveToDoneAsync(sourceFile1, settings);

        // Place a second file with the same name in the watch folder
        string sourceFile2 = Path.Combine(_watchDir, "photo.jpg");
        File.WriteAllBytes(sourceFile2, new byte[] { 2 });

        await UploadEngine.MoveToDoneAsync(sourceFile2, settings);

        string dest1 = Path.Combine(_doneDir, "photo.jpg");
        string dest2 = Path.Combine(_doneDir, "photo (2).jpg");
        Assert.True(File.Exists(dest1));
        Assert.True(File.Exists(dest2));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(dest1));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(dest2));
    }

    [Fact]
    public async Task MoveToDoneAsync_HandlesRootedOrOutsidePaths_FlatMoves()
    {
        // Simulate a file outside the watch folder
        string outsideDir = Path.Combine(_tempDir, "outside");
        Directory.CreateDirectory(outsideDir);
        string sourceFile = Path.Combine(outsideDir, "external.jpg");
        File.WriteAllBytes(sourceFile, new byte[] { 9, 9 });

        var settings = new AppSettings
        {
            WatchFolder = _watchDir,
            DoneFolder = _doneDir,
        };

        await UploadEngine.MoveToDoneAsync(sourceFile, settings);

        string expectedDest = Path.Combine(_doneDir, "external.jpg");
        Assert.False(File.Exists(sourceFile));
        Assert.True(File.Exists(expectedDest));
    }

    [Fact]
    public async Task MoveToDoneAsync_WhenDoneFolderIsConfigured_MovesAndCleansSource()
    {
        string sourceFile = Path.Combine(_watchDir, "test_photo.jpg");
        File.WriteAllBytes(sourceFile, new byte[] { 42, 43, 44 });

        var settings = new AppSettings
        {
            WatchFolder = _watchDir,
            DoneFolder = _doneDir,
        };

        await UploadEngine.MoveToDoneAsync(sourceFile, settings);

        string expectedDest = Path.Combine(_doneDir, "test_photo.jpg");
        Assert.False(File.Exists(sourceFile));
        Assert.True(File.Exists(expectedDest));
        Assert.Equal(new byte[] { 42, 43, 44 }, File.ReadAllBytes(expectedDest));
    }

    [Fact]
    public async Task MoveToDoneAsync_WhenSourceFileLocked_DoesNotCreateCascadingDuplicates()
    {
        string sourceFile = Path.Combine(_watchDir, "locked_photo.jpg");
        File.WriteAllBytes(sourceFile, new byte[] { 10, 20, 30 });

        var settings = new AppSettings
        {
            WatchFolder = _watchDir,
            DoneFolder = _doneDir,
        };

        // Lock the source file with FileShare.Read so File.Delete will fail with IOException
        using (var lockStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await UploadEngine.MoveToDoneAsync(sourceFile, settings);
        }

        // Verify that no cascading duplicates (photo (2).jpg, photo (3).jpg...) were created in Done folder
        var doneFiles = Directory.GetFiles(_doneDir);
        Assert.True(doneFiles.Length <= 1, $"Expected at most 1 file in Done folder, but found {doneFiles.Length}: {string.Join(", ", doneFiles)}");
    }
}
