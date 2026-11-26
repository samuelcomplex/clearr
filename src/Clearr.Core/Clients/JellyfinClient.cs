using System.Text.Json.Nodes;
using Clearr.Core;

namespace Clearr.Clients;

/// <summary>Reads users, shows, seasons, episodes, movies and each user's played/resume state.</summary>
public class JellyfinClient
{
    private const int Page = 1000;
    private readonly HttpApi _api;

    public JellyfinClient(HttpClient http, ConnectionSettings c)
    {
        var key = c.ApiKey.Trim();
        _api = new HttpApi(http, "Jellyfin", c.Url, r => r.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{key}\""));
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        var info = await _api.GetAsync("System/Info", ct);
        return $"Connected to Jellyfin {info.Str("Version")} ({info.Str("ServerName")})";
    }

    public async Task<List<MediaUser>> GetUsersAsync(CancellationToken ct)
    {
        var users = await _api.GetAsync("Users", ct);
        return users.Arr()
            .Where(u => !u["Policy"].Bool("IsDisabled"))
            .Select(u => new MediaUser(u.Str("Id")!, u.Str("Name") ?? "?"))
            .ToList();
    }

    public Task RefreshLibraryAsync(CancellationToken ct) => _api.PostAsync("Library/Refresh", null, ct);

    public async Task<LibrarySnapshot> LoadAsync(CancellationToken ct)
    {
        var lib = new LibrarySnapshot { Users = await GetUsersAsync(ct) };

        // Structure
        var series = await ItemsAsync("IncludeItemTypes=Series&Fields=ProviderIds,Tags", ct);
        var seasons = await ItemsAsync("IncludeItemTypes=Season&Fields=Tags", ct);
        var episodes = await ItemsAsync("IncludeItemTypes=Episode&excludeLocationTypes=Virtual", ct);
        var movies = await ItemsAsync("IncludeItemTypes=Movie&Fields=ProviderIds,Tags&excludeLocationTypes=Virtual", ct);

        var seasonNodes = seasons.ToDictionary(s => s.Str("Id")!, s => s);
        var showById = new Dictionary<string, ShowInfo>();
        foreach (var s in series)
        {
            var show = new ShowInfo
            {
                Id = s.Str("Id")!,
                Name = s.Str("Name") ?? "?",
                Status = s.Str("Status"),
                Tags = s.Strings("Tags"),
                ProviderIds = s.Dict("ProviderIds"),
            };
            showById[show.Id] = show;
            lib.Shows.Add(show);
        }

        foreach (var g in episodes.GroupBy(e => e.Str("SeriesId") ?? ""))
        {
            if (!showById.TryGetValue(g.Key, out var show))
            {
                continue;
            }

            // Group by season id; episodes without one fall back to their season number.
            foreach (var sg in g.GroupBy(e => e.Str("SeasonId") ?? ("num:" + (e.Int("ParentIndexNumber")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"))))
            {
                seasonNodes.TryGetValue(sg.Key, out var sn);
                var number = sn.Int("IndexNumber") ?? sg.First().Int("ParentIndexNumber");
                show.Seasons.Add(new SeasonInfo
                {
                    Id = sn.Str("Id") ?? $"{show.Id}:{sg.Key}",
                    Number = number,
                    Name = sn.Str("Name") ?? (number.HasValue ? $"Season {number}" : "Unknown season"),
                    Tags = sn.Strings("Tags"),
                    Episodes = sg
                        .Select(e => new EpisodeInfo { Id = e.Str("Id")!, Season = e.Int("ParentIndexNumber") ?? number, Number = e.Int("IndexNumber") })
                        .OrderBy(e => e.Number ?? int.MaxValue)
                        .ToList(),
                });
            }
        }

        foreach (var m in movies)
        {
            lib.Movies.Add(new MovieInfo
            {
                Id = m.Str("Id")!,
                Name = m.Str("Name") ?? "?",
                Year = m.Int("ProductionYear"),
                Tags = m.Strings("Tags"),
                ProviderIds = m.Dict("ProviderIds"),
            });
        }

        // Per-user state: only items each user has played or partly played.
        foreach (var u in lib.Users)
        {
            foreach (var filter in new[] { "IsPlayed", "IsResumable" })
            {
                var items = await ItemsAsync($"userId={u.Id}&IncludeItemTypes=Episode,Movie&excludeLocationTypes=Virtual&EnableUserData=true&Filters={filter}", ct);
                foreach (var it in items)
                {
                    var ud = it["UserData"];
                    if (ud == null)
                    {
                        continue;
                    }

                    var id = it.Str("Id")!;
                    if (!lib.UserData.TryGetValue(id, out var byUser))
                    {
                        lib.UserData[id] = byUser = new Dictionary<string, PlayState>(StringComparer.OrdinalIgnoreCase);
                    }

                    byUser[u.Id] = new PlayState(ud.Bool("Played"), ud.Long("PlaybackPositionTicks"), ud.Date("LastPlayedDate"));
                }
            }
        }

        return lib;
    }

    private async Task<List<JsonNode>> ItemsAsync(string query, CancellationToken ct)
    {
        var all = new List<JsonNode>();
        for (var start = 0; ; start += Page)
        {
            var page = await _api.GetAsync($"Items?Recursive=true&EnableImages=false&StartIndex={start}&Limit={Page}&{query}", ct);
            var items = page.Arr("Items").ToList();
            all.AddRange(items);
            var total = page.Int("TotalRecordCount") ?? 0;
            if (items.Count < Page || all.Count >= total)
            {
                return all;
            }
        }
    }
}
