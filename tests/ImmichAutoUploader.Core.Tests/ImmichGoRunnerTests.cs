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
}
