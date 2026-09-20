using ImmichAutoUploader.Core.Services;
using Xunit;

namespace ImmichAutoUploader.Core.Tests;

public class FileWatcherServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _watchDir;
    private readonly string _doneDir;
    private readonly string _dbPath;
    private readonly UploadQueue _queue;

    public FileWatcherServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ImmichAutoUploader_WatcherTests_" + Guid.NewGuid().ToString("N"));
        _watchDir = Path.Combine(_tempDir, "watch");
        _doneDir = Path.Combine(_tempDir, "done");
        Directory.CreateDirectory(_watchDir);
        Directory.CreateDirectory(_doneDir);
        _dbPath = Path.Combine(_tempDir, "test-queue.db");
        _queue = new UploadQueue(_dbPath);
    }

    public void Dispose()
    {
        _queue.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void Service_StartsAndDisposesCleanly_NoObjectDisposedException()
    {
        // Creates and starts the service
        var watcher = new FileWatcherService(_watchDir, _doneDir, _queue);
        watcher.Start();

        // Disposes immediately while scan and workers are active
        var ex = Record.Exception(() => watcher.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Service_RapidRestartCycles_DoesNotCrash()
    {
        for (int i = 0; i < 5; i++)
        {
            var watcher = new FileWatcherService(_watchDir, _doneDir, _queue);
            watcher.Start();
            watcher.Dispose();
        }
    }

    [Fact]
    public void IsTempFile_HandlesVariousFormats()
    {
        Assert.True(FileWatcherService.IsTempFile("C:\\watch\\~synctest.tmp"));
        Assert.True(FileWatcherService.IsTempFile("C:\\watch\\$recycle.bin"));
        Assert.True(FileWatcherService.IsTempFile("C:\\watch\\thumbs.db"));
        Assert.True(FileWatcherService.IsTempFile("C:\\watch\\photo.jpg.part"));
        Assert.False(FileWatcherService.IsTempFile("C:\\watch\\photo.jpg"));
    }

    [Fact]
    public void IsMediaFile_HandlesCaseInsensitiveMedia()
    {
        Assert.True(FileWatcherService.IsMediaFile("photo.JPG"));
        Assert.True(FileWatcherService.IsMediaFile("video.MP4"));
        Assert.True(FileWatcherService.IsMediaFile("raw.CR3"));
        Assert.False(FileWatcherService.IsMediaFile("document.pdf"));
    }
}
