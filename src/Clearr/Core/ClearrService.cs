using Clearr.Clients;
using Clearr.Storage;

namespace Clearr.Core;

/// <summary>Builds reports from Jellyfin + Sonarr/Radarr and carries out (or previews) cleanups.</summary>
public class ClearrService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IHttpClientFactory _http;
    private readonly SettingsStore _settings;
    private readonly KeepStore _keep;
    private readonly HistoryStore _history;
    private readonly ILogger<ClearrService> _log;

    public ClearrService(IHttpClientFactory http, SettingsStore settings, KeepStore keep, HistoryStore history, ILogger<ClearrService> log)
    {
        _http = http;
        _settings = settings;
        _keep = keep;
        _history = history;
        _log = log;
    }

    private HttpClient Http()
    {
        var c = _http.CreateClient("clearr");
        c.Timeout = TimeSpan.FromSeconds(100);
        return c;
    }

    // ---------------------------------------------------------------- report

    public async Task<(LibrarySnapshot Lib, WatchReport Report, Dictionary<string, ArrItem> Sonarr, Dictionary<string, RadarrMovie> Radarr)> BuildAsync(ClearrSettings s, CancellationToken ct)
    {
        if (!s.Jellyfin.IsConfigured)
        {
            throw new ApiException("Jellyfin isn't set up yet. Add its URL and API key in Settings.");
        }

        var lib = await new JellyfinClient(Http(), s.Jellyfin).LoadAsync(ct);
        var keepTag = (s.KeepTag ?? "").Trim();
        var sonarr = new Dictionary<string, ArrItem>();
        var radarr = new Dictionary<string, RadarrMovie>();

        if (s.Sonarr.IsConfigured)
        {
            try
            {
                sonarr = await new SonarrClient(Http(), s.Sonarr).IndexAsync(ct);
                lib.SonarrConnected = true;
                foreach (var show in lib.Shows)
                {
                    var hit = Match(show.ProviderIds, sonarr, "Tvdb", "Tmdb", "Imdb");
                    show.ArrId = hit?.Id;
                    show.ArrKeep = hit != null && keepTag.Length > 0 && hit.TagLabels.Any(t => string.Equals(t, keepTag, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (Exception ex) when (ex is ApiException or ArgumentException)
            {
                lib.Warnings.Add(ex.Message);
            }
        }

        if (s.Radarr.IsConfigured)
        {
            try
            {
                radarr = await new RadarrClient(Http(), s.Radarr).IndexAsync(ct);
                lib.RadarrConnected = true;
                foreach (var movie in lib.Movies)
                {
                    var hit = Match(movie.ProviderIds, radarr, "Tmdb", "Imdb");
                    movie.ArrId = hit?.Item.Id;
                    movie.ArrHasFile = hit?.FileId != null;
                    movie.ArrKeep = hit != null && keepTag.Length > 0 && hit.Item.TagLabels.Any(t => string.Equals(t, keepTag, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (Exception ex) when (ex is ApiException or ArgumentException)
            {
                lib.Warnings.Add(ex.Message);
            }
        }

        var report = Analyzer.Build(lib, s, _keep.Contains, DateTime.UtcNow);
        return (lib, report, sonarr, radarr);
    }

    private static T? Match<T>(Dictionary<string, string> providerIds, Dictionary<string, T> index, params string[] providers)
        where T : class
    {
        foreach (var p in providers)
        {
            if (providerIds.TryGetValue(p, out var v) && index.TryGetValue(p.ToLowerInvariant() + ":" + v, out var hit))
            {
                return hit;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- keep

    public async Task<string> SetKeepAsync(string id, string kind, bool keep, CancellationToken ct)
    {
        _keep.Set(id, keep);
        var s = _settings.Get();
        var tag = (s.KeepTag ?? "").Trim();
        if (tag.Length == 0 || kind == "season")
        {
            return keep ? "Kept" : "Removed keep";
        }

        // Mirror show/movie keeps as a tag in Sonarr/Radarr, the *arr way.
        try
        {
            var (_, report, _, _) = await BuildAsync(s, ct);
            if (kind == "show" && report.Shows.FirstOrDefault(x => x.ShowId == id) is { ArrId: int sid } && s.Sonarr.IsConfigured)
            {
                await new SonarrClient(Http(), s.Sonarr).SetTagAsync(sid, tag, keep, ct);
                return keep ? "Kept (also tagged in Sonarr)" : "Removed keep (and the Sonarr tag)";
            }

            if (kind == "movie" && report.Movies.FirstOrDefault(x => x.MovieId == id) is { ArrId: int mid } && s.Radarr.IsConfigured)
            {
                await new RadarrClient(Http(), s.Radarr).SetTagAsync(mid, tag, keep, ct);
                return keep ? "Kept (also tagged in Radarr)" : "Removed keep (and the Radarr tag)";
            }
        }
        catch (Exception ex) when (ex is ApiException or ArgumentException)
        {
            _log.LogWarning("Keep tag not mirrored: {Message}", ex.Message);
            return (keep ? "Kept" : "Removed keep") + " in Clearr (couldn't update the *arr tag: " + ex.Message + ")";
        }

        return keep ? "Kept" : "Removed keep";
    }

    // ---------------------------------------------------------------- cleanup

    private record Work(string Kind, string Title, string? SeasonName, int? SeasonNumber, string Reason, int ArrId, ShowInfo? Show, RadarrMovie? Movie);

    public async Task<CleanupResult> CleanupAsync(bool dryRun, bool record, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var s = _settings.Get();
            if (!dryRun && s.Mode != CleanupMode.Delete)
            {
                return new CleanupResult { DryRun = true, Error = $"Nothing was deleted: cleanup mode is {s.Mode}. Set it to Delete in Settings first." };
            }

            var (lib, report, _, radarrIndex) = await BuildAsync(s, ct);
            var result = new CleanupResult { DryRun = dryRun };
            var work = new List<Work>();

            foreach (var sr in report.Shows)
            {
                var show = lib.Shows.First(x => x.Id == sr.ShowId);
                if (s.DeleteShows && sr.Eligible && sr.ArrId is int id)
                {
                    work.Add(new Work("Show", sr.Name, null, null, sr.Reason, id, show, null));
                }
                else if (s.DeleteSeasons && sr.ArrId is int sid)
                {
                    work.AddRange(sr.Seasons.Where(x => x.Eligible && x.Number.HasValue)
                        .Select(x => new Work("Season", sr.Name, x.Name, x.Number, x.Reason, sid, show, null)));
                }
            }

            if (s.DeleteMovies)
            {
                foreach (var mr in report.Movies.Where(m => m.Eligible && m.ArrId.HasValue))
                {
                    var movie = lib.Movies.First(x => x.Id == mr.MovieId);
                    var rm = radarrIndex.Values.FirstOrDefault(v => v.Item.Id == mr.ArrId);
                    work.Add(new Work("Movie", mr.Name + (mr.Year.HasValue ? $" ({mr.Year})" : ""), null, null, mr.Reason, mr.ArrId!.Value, null, rm));
                }
            }

            var sonarr = s.Sonarr.IsConfigured ? new SonarrClient(Http(), s.Sonarr) : null;
            var radarr = s.Radarr.IsConfigured ? new RadarrClient(Http(), s.Radarr) : null;
            var cap = s.MaxDeleteGbPerRun > 0 ? s.MaxDeleteGbPerRun * 1024L * 1024L * 1024L : long.MaxValue;
            long planned = 0;
            var deletedAny = false;

            foreach (var w in work)
            {
                ct.ThrowIfCancellationRequested();
                var entry = new HistoryEntry { Date = DateTime.UtcNow, Kind = w.Kind, Title = w.Title, SeasonName = w.SeasonName, Detail = w.Reason };
                try
                {
                    // ---- plan: what exactly would be removed, and how big is it?
                    List<EpisodeFile> files = new();
                    if (w.Kind == "Movie")
                    {
                        if (w.Movie?.FileId is null)
                        {
                            Skip(entry, "No file in Radarr");
                            result.Actions.Add(entry);
                            continue;
                        }

                        entry.Bytes = w.Movie.Size;
                    }
                    else
                    {
                        var all = await sonarr!.EpisodeFilesAsync(w.ArrId, ct);
                        files = w.SeasonNumber is int n ? all.Where(f => f.SeasonNumber == n).ToList() : all;
                        if (files.Count == 0)
                        {
                            Skip(entry, "No files in Sonarr");
                            result.Actions.Add(entry);
                            continue;
                        }

                        // Safety: never delete files Jellyfin hasn't seen yet (e.g. an episode that just downloaded).
                        var gap = UnseenFiles(w.Show!, files);
                        if (gap != null)
                        {
                            Skip(entry, gap);
                            result.Actions.Add(entry);
                            continue;
                        }

                        entry.Bytes = files.Sum(f => f.Size);
                    }

                    if (planned > 0 && planned + entry.Bytes > cap)
                    {
                        Skip(entry, $"Per-run limit of {s.MaxDeleteGbPerRun} GB reached; will be handled on a later run");
                        result.Actions.Add(entry);
                        continue;
                    }

                    planned += entry.Bytes;

                    if (dryRun)
                    {
                        entry.Action = "WouldDelete";
                        result.TotalBytes += entry.Bytes;
                        result.Actions.Add(entry);
                        continue;
                    }

                    // ---- act: unmonitor first; if that fails nothing is deleted.
                    if (w.Kind == "Movie")
                    {
                        await radarr!.UnmonitorAsync(w.ArrId, ct);
                        await radarr.DeleteMovieFileAsync(w.Movie!.FileId!.Value, ct);
                        entry.Detail += " · unmonitored and file deleted in Radarr";
                    }
                    else
                    {
                        await sonarr!.UnmonitorAsync(w.ArrId, w.SeasonNumber, ct);
                        var done = 0;
                        try
                        {
                            foreach (var f in files)
                            {
                                await sonarr.DeleteEpisodeFileAsync(f.Id, ct);
                                done++;
                            }
                        }
                        catch (ApiException ex)
                        {
                            throw new ApiException($"{ex.Message} (after deleting {done} of {files.Count} files)");
                        }

                        entry.Detail += w.SeasonNumber is null
                            ? $" · {files.Count} files deleted in Sonarr; existing seasons unmonitored, show still monitored for new seasons"
                            : $" · {files.Count} files deleted in Sonarr; season unmonitored";
                    }

                    entry.Action = "Deleted";
                    result.TotalBytes += entry.Bytes;
                    deletedAny = true;
                    _log.LogInformation("Deleted {Kind} {Title} {Season} ({Bytes} bytes)", w.Kind, w.Title, w.SeasonName, entry.Bytes);
                }
                catch (Exception ex) when (ex is ApiException or ArgumentException)
                {
                    entry.Action = "Failed";
                    entry.Detail = ex.Message;
                    _log.LogError("Failed {Kind} {Title} {Season}: {Message}", w.Kind, w.Title, w.SeasonName, ex.Message);
                }

                result.Actions.Add(entry);
            }

            if (deletedAny && s.RefreshJellyfinAfterCleanup)
            {
                try
                {
                    await new JellyfinClient(Http(), s.Jellyfin).RefreshLibraryAsync(ct);
                }
                catch (ApiException ex)
                {
                    _log.LogWarning("Jellyfin refresh failed: {Message}", ex.Message);
                }
            }

            if (record && result.Actions.Count > 0)
            {
                _history.Add(result.Actions);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Skip(HistoryEntry e, string why)
    {
        e.Action = "Skipped";
        e.Detail = why;
    }

    /// <summary>Returns a reason if Sonarr has more files in a season than Jellyfin knows episodes.</summary>
    internal static string? UnseenFiles(ShowInfo show, List<EpisodeFile> files)
    {
        foreach (var g in files.GroupBy(f => f.SeasonNumber))
        {
            var known = show.Seasons.Where(x => x.Number == g.Key).Sum(x => x.Episodes.Count);
            if (g.Count() > known)
            {
                return $"Sonarr has {g.Count()} files in season {g.Key} but Jellyfin only lists {known} episodes; waiting for Jellyfin to catch up";
            }
        }

        return null;
    }
}
