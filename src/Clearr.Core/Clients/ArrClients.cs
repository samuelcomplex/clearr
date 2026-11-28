using System.Text.Json.Nodes;
using Clearr.Core;

namespace Clearr.Clients;

public record ArrItem(int Id, string Title, List<string> TagLabels, string? RootFolder = null);

public record EpisodeFile(int Id, int SeasonNumber, long Size, string? Path);

/// <summary>Shared Sonarr/Radarr bits: status, tags.</summary>
public abstract class ArrClientBase
{
    protected ArrClientBase(HttpClient http, string name, ConnectionSettings c)
    {
        var key = c.ApiKey.Trim();
        Name = name;
        Api = new HttpApi(http, name, c.Url, r => r.Headers.TryAddWithoutValidation("X-Api-Key", key));
    }

    public string Name { get; }

    protected HttpApi Api { get; }

    /// <summary>The root folder an item lives in: rootFolderPath, or the parent of its path.</summary>
    protected static string? RootOf(JsonNode? item)
    {
        var root = item.Str("rootFolderPath");
        if (string.IsNullOrWhiteSpace(root) && item.Str("path") is { Length: > 0 } path)
        {
            var trimmed = path.TrimEnd('/', '\\');
            var cut = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
            root = cut > 0 ? trimmed[..cut] : null;
        }

        return string.IsNullOrWhiteSpace(root) ? null : root;
    }

    /// <summary>Free space per drive, as Sonarr/Radarr see it.</summary>
    public async Task<List<(string Path, long Free)>> DiskSpaceAsync(CancellationToken ct) =>
        (await Api.GetAsync("api/v3/diskspace", ct)).Arr()
            .Select(d => (Path: d.Str("path") ?? "", Free: d.Long("freeSpace")))
            .Where(d => d.Path.Length > 0)
            .ToList();

    /// <summary>The root folders set up in Sonarr/Radarr.</summary>
    public async Task<List<string>> RootFoldersAsync(CancellationToken ct) =>
        (await Api.GetAsync("api/v3/rootfolder", ct)).Arr().Select(r => r.Str("path")).OfType<string>().Where(p => p.Length > 0).ToList();

    protected abstract string ResourcePath { get; }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        var s = await Api.GetAsync("api/v3/system/status", ct);
        return $"Connected to {Name} {s.Str("version")}";
    }

    protected async Task<Dictionary<int, string>> TagLabelsAsync(CancellationToken ct) =>
        (await Api.GetAsync("api/v3/tag", ct)).Arr().ToDictionary(t => t.Int("id")!.Value, t => t.Str("label") ?? "");

    /// <summary>Add or remove a tag (created if missing) on a series/movie.</summary>
    public async Task SetTagAsync(int itemId, string label, bool on, CancellationToken ct)
    {
        label = label.Trim().ToLowerInvariant();
        if (label.Length == 0)
        {
            return;
        }

        var tags = await TagLabelsAsync(ct);
        var tagId = tags.FirstOrDefault(t => string.Equals(t.Value, label, StringComparison.OrdinalIgnoreCase)).Key;
        if (tagId == 0)
        {
            if (!on)
            {
                return;
            }

            var created = await Api.PostAsync("api/v3/tag", new JsonObject { ["label"] = label }, ct);
            tagId = created.Int("id") ?? throw new ApiException($"{Name}: couldn't create tag '{label}'");
        }

        var item = (JsonObject)(await Api.GetAsync($"{ResourcePath}/{itemId}", ct))!;
        var ids = item["tags"] is JsonArray a ? a.Select(x => x!.GetValue<int>()).ToList() : new List<int>();
        var changed = on ? !ids.Contains(tagId) : ids.Remove(tagId);
        if (on && changed)
        {
            ids.Add(tagId);
        }

        if (changed)
        {
            item["tags"] = new JsonArray(ids.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());
            await Api.PutAsync($"{ResourcePath}/{itemId}", item, ct);
        }
    }
}

public class SonarrClient : ArrClientBase
{
    public SonarrClient(HttpClient http, ConnectionSettings c)
        : base(http, "Sonarr", c)
    {
    }

    protected override string ResourcePath => "api/v3/series";

    /// <summary>All series, keyed by "tvdb:…", "tmdb:…" and "imdb:…".</summary>
    public async Task<Dictionary<string, ArrItem>> IndexAsync(CancellationToken ct)
    {
        var tags = await TagLabelsAsync(ct);
        var index = new Dictionary<string, ArrItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in (await Api.GetAsync("api/v3/series", ct)).Arr())
        {
            var item = new ArrItem(s.Int("id")!.Value, s.Str("title") ?? "?", s.Arr("tags").Select(t => tags.GetValueOrDefault(t.GetValue<int>(), "")).ToList(), RootOf(s));
            if ((s.Int("tvdbId") ?? 0) > 0)
            {
                index["tvdb:" + s.Int("tvdbId")] = item;
            }

            if ((s.Int("tmdbId") ?? 0) > 0)
            {
                index["tmdb:" + s.Int("tmdbId")] = item;
            }

            if (!string.IsNullOrEmpty(s.Str("imdbId")))
            {
                index["imdb:" + s.Str("imdbId")] = item;
            }
        }

        return index;
    }

    public async Task<List<EpisodeFile>> EpisodeFilesAsync(int seriesId, CancellationToken ct) =>
        (await Api.GetAsync($"api/v3/episodefile?seriesId={seriesId}", ct)).Arr()
            .Select(f => new EpisodeFile(f.Int("id")!.Value, f.Int("seasonNumber") ?? -1, f.Long("size"), f.Str("relativePath")))
            .ToList();

    /// <summary>Unmonitors one season, or every existing season. The series itself stays monitored.</summary>
    public async Task UnmonitorAsync(int seriesId, int? seasonNumber, CancellationToken ct)
    {
        var series = (JsonObject)(await Api.GetAsync($"api/v3/series/{seriesId}", ct))!;
        foreach (var s in series["seasons"].Arr().OfType<JsonObject>())
        {
            if (seasonNumber is null || s.Int("seasonNumber") == seasonNumber)
            {
                s["monitored"] = false;
            }
        }

        await Api.PutAsync($"api/v3/series/{seriesId}", series, ct);
    }

    public Task DeleteEpisodeFileAsync(int fileId, CancellationToken ct) => Api.DeleteAsync($"api/v3/episodefile/{fileId}", ct);
}

public record RadarrMovie(ArrItem Item, int? FileId, long Size);

public class RadarrClient : ArrClientBase
{
    public RadarrClient(HttpClient http, ConnectionSettings c)
        : base(http, "Radarr", c)
    {
    }

    protected override string ResourcePath => "api/v3/movie";

    /// <summary>All movies, keyed by "tmdb:…" and "imdb:…".</summary>
    public async Task<Dictionary<string, RadarrMovie>> IndexAsync(CancellationToken ct)
    {
        var tags = await TagLabelsAsync(ct);
        var index = new Dictionary<string, RadarrMovie>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in (await Api.GetAsync("api/v3/movie", ct)).Arr())
        {
            var item = new ArrItem(m.Int("id")!.Value, m.Str("title") ?? "?", m.Arr("tags").Select(t => tags.GetValueOrDefault(t.GetValue<int>(), "")).ToList(), RootOf(m));
            var file = m["movieFile"];
            var rm = new RadarrMovie(item, file.Int("id"), file != null ? file.Long("size") : 0);
            if ((m.Int("tmdbId") ?? 0) > 0)
            {
                index["tmdb:" + m.Int("tmdbId")] = rm;
            }

            if (!string.IsNullOrEmpty(m.Str("imdbId")))
            {
                index["imdb:" + m.Str("imdbId")] = rm;
            }
        }

        return index;
    }

    public async Task UnmonitorAsync(int movieId, CancellationToken ct)
    {
        var movie = (JsonObject)(await Api.GetAsync($"api/v3/movie/{movieId}", ct))!;
        movie["monitored"] = false;
        await Api.PutAsync($"api/v3/movie/{movieId}", movie, ct);
    }

    public Task DeleteMovieFileAsync(int fileId, CancellationToken ct) => Api.DeleteAsync($"api/v3/moviefile/{fileId}", ct);
}
