using System.Text.Json;

namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// Provides post-upload media library synchronization with a Jellyfin media server.
/// <para/>
/// <b>Authentication &amp; Resilience Details:</b>
/// <list type="bullet">
///   <item><description>Requests authenticate using the required <c>Authorization: MediaBrowser Token="..."</c> header format.</description></item>
///   <item><description>Uses a shared, socket-pooled <see cref="HttpClient"/> with a 15-minute connection lifetime to prevent socket exhaustion.</description></item>
///   <item><description>All network and parsing exceptions are logged as warnings and never propagate, ensuring media server hiccups never fail uploads.</description></item>
/// </list>
/// </summary>
public static class JellyfinService
{
    /// <summary>
    /// Reusable HTTP client instance configured with connection pooling and a 30-second timeout.
    /// </summary>
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15)
    }) { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Asynchronously requests a library refresh on the configured Jellyfin server.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Jellyfin server (e.g. <c>https://jellyfin.example.com</c>).</param>
    /// <param name="apiKey">The Jellyfin API token.</param>
    /// <param name="libraryName">
    /// The exact name of the media library to refresh (e.g., "Photos").
    /// If null or whitespace, a full server library scan (<c>POST /Library/Refresh</c>) is initiated.
    /// </param>
    /// <param name="ct">A cancellation token for the HTTP operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static async Task TriggerRefreshAsync(
        string baseUrl, string apiKey, string libraryName, CancellationToken ct = default)
    {
        try
        {
            string base_ = baseUrl.TrimEnd('/');

            if (!string.IsNullOrWhiteSpace(libraryName))
            {
                string? libraryId = await FindLibraryIdAsync(SharedClient, base_, apiKey, libraryName, ct).ConfigureAwait(false);
                if (libraryId is null)
                {
                    AppLogger.Warn($"Jellyfin: library '{libraryName}' not found; skipping refresh.");
                    return;
                }
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{base_}/Items/{libraryId}/Refresh?Recursive=true");
                req.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{apiKey}\"");
                using var resp = await SharedClient.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    AppLogger.Info($"Jellyfin: refresh triggered for library '{libraryName}'.");
                else
                    AppLogger.Warn($"Jellyfin: library refresh returned HTTP {(int)resp.StatusCode}.");
            }
            else
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{base_}/Library/Refresh");
                req.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{apiKey}\"");
                using var resp = await SharedClient.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    AppLogger.Info("Jellyfin: full library refresh triggered.");
                else
                    AppLogger.Warn($"Jellyfin: full library refresh returned HTTP {(int)resp.StatusCode}.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Jellyfin refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Queries the Jellyfin server's media folders to resolve a library's unique identifier by its display name.
    /// </summary>
    /// <param name="http">The HTTP client to use for the request.</param>
    /// <param name="baseUrl">The normalized base URL of the Jellyfin server.</param>
    /// <param name="apiKey">The Jellyfin API token.</param>
    /// <param name="libraryName">The display name of the library to match.</param>
    /// <param name="ct">A cancellation token for the HTTP request.</param>
    /// <returns>The string identifier of the matching library, or <c>null</c> if not found or if the request fails.</returns>
    internal static async Task<string?> FindLibraryIdAsync(
        HttpClient http, string baseUrl, string apiKey, string libraryName, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/Library/MediaFolders");
        req.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{apiKey}\"");
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            AppLogger.Warn($"Jellyfin: could not list libraries (HTTP {(int)resp.StatusCode}).");
            return null;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        JsonElement itemsElement;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            itemsElement = doc.RootElement;
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                 doc.RootElement.TryGetProperty("Items", out var items) &&
                 items.ValueKind == JsonValueKind.Array)
        {
            itemsElement = items;
        }
        else
        {
            return null;
        }

        foreach (var lib in itemsElement.EnumerateArray())
        {
            if (lib.TryGetProperty("Name", out var name) &&
                string.Equals(name.GetString(), libraryName, StringComparison.OrdinalIgnoreCase))
            {
                if (lib.TryGetProperty("Id", out var id))
                    return id.GetString();
                if (lib.TryGetProperty("ItemId", out var itemId))
                    return itemId.GetString();
            }
        }
        return null;
    }
}
