using ImmichAutoUploader.Core.Services;
using Xunit;

namespace ImmichAutoUploader.Core.Tests;

public class FileClassificationTests
{
    [Theory]
    [InlineData("test.jpg", true)]
    [InlineData("test.JPEG", true)]
    [InlineData("test.png", true)]
    [InlineData("test.heic", true)]
    [InlineData("test.cr2", true)]
    [InlineData("test.dng", true)]
    [InlineData("test.mp4", true)]
    [InlineData("test.mov", true)]
    [InlineData("test.mkv", true)]
    [InlineData("test.txt", false)]
    [InlineData("test.pdf", false)]
    [InlineData("test.exe", false)]
    [InlineData("test.zip", false)]
    public void IsMediaFile_IdentifiesSupportedExtensions(string fileName, bool expected)
    {
        bool result = FileWatcherService.IsMediaFile(fileName);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("~photo.jpg", true)]
    [InlineData("$temp.png", true)]
    [InlineData("thumbs.db", true)]
    [InlineData("desktop.ini", true)]
    [InlineData(".DS_Store", true)]
    [InlineData("video.mp4.tmp", true)]
    [InlineData("video.mp4.part", true)]
    [InlineData("video.mp4.crdownload", true)]
    [InlineData("photo.jpg", false)]
    [InlineData("video.mp4", false)]
    public void IsTempFile_IdentifiesTemporaryAndSystemFiles(string fileName, bool expected)
    {
        bool result = FileWatcherService.IsTempFile(fileName);
        Assert.Equal(expected, result);
    }
}
