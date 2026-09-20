using ImmichAutoUploader.Core.Services;
using Xunit;

namespace ImmichAutoUploader.Core.Tests;

public class ProcessHelperTests
{
    [Fact]
    public async Task RunAsync_SuccessfulProcess_CapturesExitCodeAndOutput()
    {
        var result = await ProcessHelper.RunAsync("cmd.exe", new[] { "/c", "echo ProcessHelperTestOutput" }, 10_000);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("ProcessHelperTestOutput", result.StdOut);
    }

    [Fact]
    public async Task RunAsync_TimedOutProcess_PreservesOutputProducedBeforeTimeout()
    {
        // Emit output immediately, then sleep past the timeout
        var result = await ProcessHelper.RunAsync(
            "cmd.exe",
            new[] { "/c", "echo TailscaleLoginUrlSimulated: https://login.tailscale.com/a/test1234 & ping 127.0.0.1 -n 6 > nul" },
            1_500);

        Assert.True(result.TimedOut);
        Assert.Contains("TailscaleLoginUrlSimulated", result.StdOut);
        Assert.Contains("https://login.tailscale.com/a/test1234", result.StdOut);
    }

    [Fact]
    public void Tail_ReturnsLastNLines()
    {
        string input = "Line 1\nLine 2\nLine 3\nLine 4\nLine 5";
        string result = ProcessHelper.Tail(input, 2);

        Assert.Equal("Line 4\nLine 5", result);
    }
}
