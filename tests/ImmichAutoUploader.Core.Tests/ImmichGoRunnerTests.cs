using ImmichAutoUploader.Core.Services;
using Xunit;

namespace ImmichAutoUploader.Core.Tests;

public class ImmichGoRunnerTests
{
    [Fact]
    public async Task UploadSingleAsync_NonExistentBinary_ReturnsFailureGracefully()
    {
        var req = new UploadRequest(
            ExePath: "C:\\nonexistent\\immich-go-fake.exe",
            ServerUrl: "http://localhost:2283",
            ApiKey: "test-key",
            AdminApiKey: null,
            PauseJobs: false,
            DeviceUuid: "TestDevice",
            FilePath: "C:\\dummy\\photo.jpg"
        );

        var result = await ImmichGoRunner.UploadSingleAsync(req);

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("Failed to start process", result.ErrorDetail);
    }

    [Fact]
    public async Task UploadSingleAsync_CleansUpStagingDirectory()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"immich_test_{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(tempFile, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

        try
        {
            var req = new UploadRequest(
                ExePath: "C:\\nonexistent\\immich-go-fake.exe",
                ServerUrl: "http://localhost:2283",
                ApiKey: "test-key",
                AdminApiKey: null,
                PauseJobs: false,
                DeviceUuid: "TestDevice",
                FilePath: tempFile
            );

            await ImmichGoRunner.UploadSingleAsync(req);

            // Check that no lingering $immich_stage_ directories remain in temp
            var lingering = Directory.GetDirectories(Path.GetTempPath(), "$immich_stage_*");
            Assert.Empty(lingering);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
