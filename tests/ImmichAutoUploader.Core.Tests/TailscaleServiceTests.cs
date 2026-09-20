using ImmichAutoUploader.Core.Services;
using Xunit;

namespace ImmichAutoUploader.Core.Tests;

public class TailscaleServiceTests
{
    [Theory]
    [InlineData("To authenticate, visit: https://login.tailscale.com/a/abc1234\n", "https://login.tailscale.com/a/abc1234")]
    [InlineData("Please visit https://login.tailscale.com/a/xyz9876. to complete login.", "https://login.tailscale.com/a/xyz9876")]
    [InlineData("Open (https://login.tailscale.com/a/parentheses) in your browser", "https://login.tailscale.com/a/parentheses")]
    [InlineData("No login needed. Already connected.", null)]
    public void ExtractLoginUrl_ParsesUrlsCorrectly(string input, string? expected)
    {
        string? url = TailscaleService.ExtractLoginUrl(input);
        Assert.Equal(expected, url);
    }
}
