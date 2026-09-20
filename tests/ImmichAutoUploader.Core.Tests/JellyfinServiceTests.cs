using System.Net;
using System.Text;
using ImmichAutoUploader.Core.Services;
using Xunit;

namespace ImmichAutoUploader.Core.Tests;

public class JellyfinServiceTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseContent;

        public MockHttpMessageHandler(HttpStatusCode statusCode, string responseContent)
        {
            _statusCode = statusCode;
            _responseContent = responseContent;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseContent, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task FindLibraryIdAsync_MatchesInArrayResponse()
    {
        string json = @"[
            { ""Name"": ""Movies"", ""Id"": ""mov-1"" },
            { ""Name"": ""Family Photos"", ""Id"": ""photo-123"" }
        ]";

        using var handler = new MockHttpMessageHandler(HttpStatusCode.OK, json);
        using var client = new HttpClient(handler);

        string? id = await JellyfinService.FindLibraryIdAsync(client, "http://localhost:8096", "api-key", "Family Photos", CancellationToken.None);

        Assert.Equal("photo-123", id);
    }

    [Fact]
    public async Task FindLibraryIdAsync_MatchesInItemsObjectResponse()
    {
        string json = @"{
            ""Items"": [
                { ""Name"": ""Photos"", ""ItemId"": ""item-999"" }
            ]
        }";

        using var handler = new MockHttpMessageHandler(HttpStatusCode.OK, json);
        using var client = new HttpClient(handler);

        string? id = await JellyfinService.FindLibraryIdAsync(client, "http://localhost:8096", "api-key", "photos", CancellationToken.None);

        Assert.Equal("item-999", id);
    }

    [Fact]
    public async Task FindLibraryIdAsync_ReturnsNullWhenNotFound()
    {
        string json = @"[ { ""Name"": ""Movies"", ""Id"": ""mov-1"" } ]";

        using var handler = new MockHttpMessageHandler(HttpStatusCode.OK, json);
        using var client = new HttpClient(handler);

        string? id = await JellyfinService.FindLibraryIdAsync(client, "http://localhost:8096", "api-key", "NonExistent", CancellationToken.None);

        Assert.Null(id);
    }

    [Fact]
    public async Task FindLibraryIdAsync_ReturnsNullOnHttpError()
    {
        using var handler = new MockHttpMessageHandler(HttpStatusCode.Unauthorized, "Unauthorized");
        using var client = new HttpClient(handler);

        string? id = await JellyfinService.FindLibraryIdAsync(client, "http://localhost:8096", "api-key", "Photos", CancellationToken.None);

        Assert.Null(id);
    }
}
