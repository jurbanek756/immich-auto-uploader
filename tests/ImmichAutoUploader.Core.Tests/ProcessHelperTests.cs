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
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("TailscaleLoginUrlSimulated", result.StdOut);
        Assert.Contains("https://login.tailscale.com/a/test1234", result.StdOut);
    }

    [Fact]
    public async Task RunAsync_NonExistentExecutable_ReturnsExitCodeMinusOneWithoutThrowing()
    {
        var result = await ProcessHelper.RunAsync("non_existent_executable_12345.exe", Array.Empty<string>(), 5_000);

        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("Failed to start process", result.StdErr);
    }

    [Fact]
    public void Tail_ReturnsLastNLines()
    {
        string input = "Line 1\nLine 2\nLine 3\nLine 4\nLine 5";
        string result = ProcessHelper.Tail(input, 2);

        Assert.Equal("Line 4\nLine 5", result);
    }
}
