using System.Text.Json.Nodes;
using Clearr.Core;

namespace Clearr.Clients;

/// <summary>Reads users, shows, seasons, episodes, movies and each user's played/resume state.</summary>
public class JellyfinClient
{
    private const int Page = 1000;

    /// <summary>Requests in flight at once: fast on big libraries, gentle on small servers.</summary>
    private const int Parallel = 4;
    private static readonly string[] StateFilters = { "IsPlayed", "IsResumable" };
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
        using var gate = new SemaphoreSlim(Parallel, Parallel);
        var usersTask = GetUsersAsync(ct);

        // Structure (fetched side by side)
        var seriesTask = ItemsAsync("IncludeItemTypes=Series&Fields=ProviderIds,Tags", gate, ct);
        var seasonsTask = ItemsAsync("IncludeItemTypes=Season&Fields=Tags", gate, ct);
        var episodesTask = ItemsAsync("IncludeItemTypes=Episode&excludeLocationTypes=Virtual", gate, ct);
        var moviesTask = ItemsAsync("IncludeItemTypes=Movie&Fields=ProviderIds,Tags&excludeLocationTypes=Virtual", gate, ct);
        var structure = new Task[] { seriesTask, seasonsTask, episodesTask, moviesTask };
        List<MediaUser> users;
        try
        {
            users = await usersTask;
        }
        catch
        {
            await Task.WhenAll(structure).ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            throw;
        }

        var lib = new LibrarySnapshot { Users = users };

        // Per-user state: only items each user has played or partly played.
        var stateTasks = lib.Users
            .SelectMany(u => StateFilters.Select(filter => (User: u, Task: ItemsAsync($"userId={u.Id}&IncludeItemTypes=Episode,Movie&excludeLocationTypes=Virtual&EnableUserData=true&Filters={filter}", gate, ct))))
            .ToList();
        var favoriteTasks = lib.Users
            .Select(u => (User: u, Task: ItemsAsync($"userId={u.Id}&IncludeItemTypes=Series,Season,Movie&Filters=IsFavorite", gate, ct)))
            .ToList();

        // Wait for everything (even after a failure), so no request outlives this call.
        await Task.WhenAll(structure.Concat(stateTasks.Select(x => x.Task)).Concat(favoriteTasks.Select(x => x.Task)));
        var series = seriesTask.Result;
        var seasons = seasonsTask.Result;
        var episodes = episodesTask.Result;
        var movies = moviesTask.Result;

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

        foreach (var (u, task) in favoriteTasks)
        {
            foreach (var it in await task)
            {
                lib.AddFavorite(it.Str("Id")!, u.Id);
            }
        }

        foreach (var (u, task) in stateTasks)
        {
            foreach (var it in await task)
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

        return lib;
    }

    /// <summary>All pages of an Items query: the first page tells the total, the rest are fetched side by side.</summary>
    private async Task<List<JsonNode>> ItemsAsync(string query, SemaphoreSlim gate, CancellationToken ct)
    {
        async Task<JsonNode> PageAsync(int start)
        {
            await gate.WaitAsync(ct);
            try
            {
                return await _api.GetAsync($"Items?Recursive=true&EnableImages=false&EnableImageTypes=None&StartIndex={start}&Limit={Page}&{query}", ct) ?? new JsonObject();
            }
            finally
            {
                gate.Release();
            }
        }

        var first = await PageAsync(0);
        var all = first.Arr("Items").ToList();
        var total = first.Int("TotalRecordCount") ?? 0;
        if (all.Count < Page || all.Count >= total)
        {
            return all;
        }

        var rest = await Task.WhenAll(Enumerable.Range(1, (total - 1) / Page).Select(i => PageAsync(i * Page)));
        foreach (var page in rest)
        {
            all.AddRange(page.Arr("Items"));
        }

        return all;
    }
}
