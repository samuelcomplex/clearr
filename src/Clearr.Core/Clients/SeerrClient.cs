using System.Globalization;
using Clearr.Core;

namespace Clearr.Clients;

/// <summary>Jellyseerr / Overseerr / Seerr (same API). Used to mark removed shows and movies as no longer available.</summary>
public class SeerrClient
{
    private readonly HttpApi _api;

    public SeerrClient(HttpClient http, ConnectionSettings c)
    {
        var key = c.ApiKey.Trim();
        _api = new HttpApi(http, "Seerr", c.Url, r => r.Headers.TryAddWithoutValidation("X-Api-Key", key));
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        await _api.GetAsync("api/v1/settings/main", ct); // needs the admin API key
        var s = await _api.GetAsync("api/v1/status", ct);
        return $"Connected to Seerr {s.Str("version")}";
    }

    /// <summary>Removes a movie ("movie") or show ("tv") by its TMDB id. False when Seerr doesn't know it.</summary>
    public async Task<bool> RemoveAsync(string kind, int tmdbId, CancellationToken ct)
    {
        System.Text.Json.Nodes.JsonNode? details;
        try
        {
            details = await _api.GetAsync($"api/v1/{kind}/{tmdbId.ToString(CultureInfo.InvariantCulture)}", ct);
        }
        catch (ApiException ex) when (ex.Status == 404)
        {
            return false;
        }

        if (details?["mediaInfo"].Int("id") is not int mediaId)
        {
            return false;
        }

        await _api.DeleteAsync($"api/v1/media/{mediaId.ToString(CultureInfo.InvariantCulture)}", ct);
        return true;
    }
}
