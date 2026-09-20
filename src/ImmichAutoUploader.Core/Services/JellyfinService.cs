using System.Text.Json;

namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// Triggers a Jellyfin library refresh after a successful upload batch so new
/// photos show up without a manual scan.
/// <para/>
/// Auth uses the <c>Authorization: MediaBrowser Token="…"</c> header — the
/// <c>?api_key=</c> and <c>X-Emby-Token</c> variants return 401 on this instance.
/// <para/>
/// Failures are logged, never thrown: a Jellyfin hiccup must not fail uploads.
/// </summary>
public static class JellyfinService
{
    public static async Task TriggerRefreshAsync(
        string baseUrl, string apiKey, string libraryName, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Add("Authorization", $"MediaBrowser Token=\"{apiKey}\"");
            string base_ = baseUrl.TrimEnd('/');

            if (!string.IsNullOrWhiteSpace(libraryName))
            {
                string? libraryId = await FindLibraryIdAsync(http, base_, libraryName, ct).ConfigureAwait(false);
                if (libraryId is null)
                {
                    AppLogger.Warn($"Jellyfin: library '{libraryName}' not found; skipping refresh.");
                    return;
                }
                using var resp = await http.PostAsync(
                    $"{base_}/Items/{libraryId}/Refresh?Recursive=true", null, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    AppLogger.Info($"Jellyfin: refresh triggered for library '{libraryName}'.");
                else
                    AppLogger.Warn($"Jellyfin: library refresh returned HTTP {(int)resp.StatusCode}.");
            }
            else
            {
                using var resp = await http.PostAsync($"{base_}/Library/Refresh", null, ct).ConfigureAwait(false);
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

    private static async Task<string?> FindLibraryIdAsync(
        HttpClient http, string baseUrl, string libraryName, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"{baseUrl}/Library/MediaFolders", ct).ConfigureAwait(false);
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
