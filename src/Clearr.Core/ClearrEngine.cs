using Clearr.Clients;
using Microsoft.Extensions.Logging;

namespace Clearr.Core;

/// <summary>Where the library and everyone's watch state come from (Jellyfin's API in the app, Jellyfin's internals in the plugin).</summary>
public interface ILibrarySource
{
    Task<LibrarySnapshot> LoadAsync(CancellationToken ct);

    /// <summary>Ask Jellyfin to rescan after files were removed.</summary>
    Task RefreshAsync(CancellationToken ct);
}

/// <summary>What a direct (Jellyfin) removal targets: a whole show, one season, or a movie.</summary>
public record DirectTarget(string Kind, string ItemId, string? SeasonId, int? SeasonNumber);

/// <summary>Plugin only: removes items Sonarr/Radarr don't manage straight through Jellyfin.</summary>
public interface IDirectDeleter
{
    long Measure(DirectTarget target);

    /// <summary>Returns a short description of what was removed.</summary>
    string Delete(DirectTarget target);
}

public interface IKeepStore
{
    bool Contains(string id);

    void Set(string id, bool keep);
}

public interface IHistoryStore
{
    List<HistoryEntry> GetAll();

    void Add(IEnumerable<HistoryEntry> entries);
}

/// <summary>
/// Builds reports from the library + Sonarr/Radarr and carries out (or previews) cleanups.
/// Shared by the Clearr app and the Clearr Jellyfin plugin.
/// </summary>
public sealed class ClearrEngine : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IHttpClientFactory _http;
    private readonly Func<ClearrSettings> _settings;
    private readonly ILibrarySource _source;
    private readonly IKeepStore _keep;
    private readonly IHistoryStore _history;
    private readonly ILogger _log;
    private readonly IDirectDeleter? _direct;
    private (DateTime At, Built Built)? _last;

    public ClearrEngine(IHttpClientFactory http, Func<ClearrSettings> settings, ILibrarySource source, IKeepStore keep, IHistoryStore history, ILogger log, IDirectDeleter? direct = null)
    {
        _http = http;
        _settings = settings;
        _source = source;
        _keep = keep;
        _history = history;
        _log = log;
        _direct = direct;
    }

    public List<HistoryEntry> History() => _history.GetAll();

    public void Dispose() => _gate.Dispose();

    private HttpClient Http()
    {
        var c = _http.CreateClient("clearr");
        c.Timeout = TimeSpan.FromSeconds(100);
        return c;
    }

    // ---------------------------------------------------------------- report

    public record Built(LibrarySnapshot Lib, WatchReport Report, Dictionary<string, ArrItem> Sonarr, Dictionary<string, RadarrMovie> Radarr);

    public async Task<Built> BuildAsync(CancellationToken ct)
    {
        var s = _settings();
        var lib = await _source.LoadAsync(ct);
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
                    show.ArrRootFolder = hit?.RootFolder;
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
                    movie.ArrRootFolder = hit?.Item.RootFolder;
                }
            }
            catch (Exception ex) when (ex is ApiException or ArgumentException)
            {
                lib.Warnings.Add(ex.Message);
            }
        }

        var report = Analyzer.Build(lib, s, _keep.Contains, DateTime.UtcNow, _direct != null);
        foreach (var w in report.Warnings)
        {
            _log.LogWarning("{Warning}", w);
        }

        _log.LogDebug("Report: {Shows} shows, {Movies} movies, {Users} tracked users", report.Shows.Count, report.Movies.Count, report.TrackedUsers.Count);
        var built = new Built(lib, report, sonarr, radarr);
        _last = (DateTime.UtcNow, built);
        return built;
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
        var s = _settings();
        var tag = (s.KeepTag ?? "").Trim();
        if (tag.Length == 0 || kind == "season")
        {
            return keep ? "Kept" : "Removed keep";
        }

        // Mirror show/movie keeps as a tag in Sonarr/Radarr, the *arr way.
        // Only the Sonarr/Radarr ids are needed, so a recent report will do.
        try
        {
            var built = _last is { } last && DateTime.UtcNow - last.At < TimeSpan.FromMinutes(10) ? last.Built : await BuildAsync(ct);
            if (kind == "show" && built.Report.Shows.FirstOrDefault(x => x.ShowId == id) is { ArrId: int sid } && s.Sonarr.IsConfigured)
            {
                await new SonarrClient(Http(), s.Sonarr).SetTagAsync(sid, tag, keep, ct);
                return keep ? "Kept (also tagged in Sonarr)" : "Removed keep (and the Sonarr tag)";
            }

            if (kind == "movie" && built.Report.Movies.FirstOrDefault(x => x.MovieId == id) is { ArrId: int mid } && s.Radarr.IsConfigured)
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

    private sealed record Work(string Kind, string Title, string? SeasonName, int? SeasonNumber, Msg Reason, int? ArrId, ShowInfo? Show, RadarrMovie? Movie, DirectTarget? Direct);

    private static CleanupScope KindOf(Work w) => w.Kind == "Movie" ? CleanupScope.Movies : CleanupScope.Shows;

    /// <summary>
    /// Previews (dryRun) or carries out a cleanup of shows, movies or both.
    /// In a real run each kind follows its own mode: Delete is removed, Dry run is only listed, Off is left out.
    /// </summary>
    public async Task<CleanupResult> CleanupAsync(bool dryRun, bool record, CancellationToken ct, CleanupScope scope = CleanupScope.All)
    {
        CleanupResult result;
        try
        {
            result = await RunCleanupAsync(dryRun, record, scope, ct);
        }
        catch (Exception ex) when (record && ex is not OperationCanceledException)
        {
            await NotifyAsync(Notifier.ForCrash(ex, scope, NotifyLanguage()));
            throw;
        }

        if (record && Notifier.ForRun(result, scope, _settings().Notifications, NotifyLanguage()) is Notification notice)
        {
            await NotifyAsync(notice);
        }

        return result;
    }

    /// <summary>Language for notifications: the Language setting, else the host's (the plugin passes Jellyfin's).</summary>
    public Func<string?> HostLanguage { get; set; } = () => null;

    private string? NotifyLanguage() => _settings().Language is { Length: > 0 } l ? l : HostLanguage();

    private async Task NotifyAsync(Notification notice)
    {
        var n = _settings().Notifications;
        if (!n.AnyTarget)
        {
            return;
        }

        await Notifier.SendAsync(_http.CreateClient("clearr"), n, notice, _log, CancellationToken.None);
    }

    /// <summary>Root folders from Sonarr and Radarr (empty when not connected), for the Settings page.</summary>
    public async Task<(List<string> Sonarr, List<string> Radarr)> RootFoldersAsync(CancellationToken ct)
    {
        var s = _settings();
        async Task<List<string>> Get(Func<Task<List<string>>> load)
        {
            try
            {
                return await load();
            }
            catch (Exception ex) when (ex is ApiException or ArgumentException)
            {
                _log.LogDebug("Root folders not available: {Message}", ex.Message);
                return new();
            }
        }

        var sonarr = s.Sonarr.IsConfigured ? Get(() => new SonarrClient(_http.CreateClient("clearr"), s.Sonarr).RootFoldersAsync(ct)) : Task.FromResult(new List<string>());
        var radarr = s.Radarr.IsConfigured ? Get(() => new RadarrClient(_http.CreateClient("clearr"), s.Radarr).RootFoldersAsync(ct)) : Task.FromResult(new List<string>());
        return (await sonarr, await radarr);
    }

    /// <summary>Sends a test message to the configured targets.</summary>
    public Task<List<(string Target, string? Error)>> TestNotificationsAsync(NotificationSettings n, CancellationToken ct) =>
        Notifier.SendAsync(_http.CreateClient("clearr"), n, Notifier.Test(NotifyLanguage()), _log, ct);

    private async Task<CleanupResult> RunCleanupAsync(bool dryRun, bool record, CleanupScope scope, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var s = _settings();
            bool InScope(CleanupScope k) => (scope & k) != 0 && (dryRun || s.ModeFor(k) != CleanupMode.Off);
            bool Acts(CleanupScope k) => !dryRun && (scope & k) != 0 && s.ModeFor(k) == CleanupMode.Delete;
            if (!dryRun && !Acts(CleanupScope.Shows) && !Acts(CleanupScope.Movies))
            {
                var refused = scope switch
                {
                    CleanupScope.Shows => new Msg("error.refusedShows", new() { ["mode"] = ModeName(s.ShowsMode) }),
                    CleanupScope.Movies => new Msg("error.refusedMovies", new() { ["mode"] = ModeName(s.MoviesMode) }),
                    _ => new Msg("error.refusedBoth", new() { ["shows"] = ModeName(s.ShowsMode), ["movies"] = ModeName(s.MoviesMode) }),
                };
                return new CleanupResult { DryRun = true, Error = refused.ToString(), ErrorMsg = refused };
            }

            var (lib, report, _, radarrIndex) = await BuildAsync(ct);
            var result = new CleanupResult { DryRun = dryRun };
            var work = new List<Work>();
            var showsById = lib.Shows.ToDictionary(x => x.Id);
            var radarrById = radarrIndex.Values.GroupBy(v => v.Item.Id).ToDictionary(g => g.Key, g => g.First());

            foreach (var sr in InScope(CleanupScope.Shows) ? report.Shows : new List<ShowReport>())
            {
                var show = showsById[sr.ShowId];
                var viaSonarr = sr.Via == "Sonarr";
                if (sr.Via == null)
                {
                    continue;
                }

                if (s.DeleteShows && sr.Eligible)
                {
                    work.Add(new Work("Show", sr.Name, null, null, sr.ReasonMsg!, viaSonarr ? sr.ArrId : null, show, null, viaSonarr ? null : new DirectTarget("Show", sr.ShowId, null, null)));
                }
                else if (s.DeleteSeasons)
                {
                    work.AddRange(sr.Seasons.Where(x => x.Eligible && x.Number.HasValue)
                        .Select(x => new Work("Season", sr.Name, x.Name, x.Number, x.ReasonMsg!, viaSonarr ? sr.ArrId : null, show, null, viaSonarr ? null : new DirectTarget("Season", sr.ShowId, x.SeasonId, x.Number))));
                }
            }

            if (InScope(CleanupScope.Movies))
            {
                foreach (var mr in report.Movies.Where(m => m.Eligible && m.Via != null))
                {
                    var title = mr.Name + (mr.Year.HasValue ? $" ({mr.Year})" : "");
                    if (mr.Via == "Radarr")
                    {
                        var rm = mr.ArrId is int rid ? radarrById.GetValueOrDefault(rid) : null;
                        work.Add(new Work("Movie", title, null, null, mr.ReasonMsg!, mr.ArrId, null, rm, null));
                    }
                    else
                    {
                        work.Add(new Work("Movie", title, null, null, mr.ReasonMsg!, null, null, null, new DirectTarget("Movie", mr.MovieId, null, null)));
                    }
                }
            }

            var sonarr = s.Sonarr.IsConfigured ? new SonarrClient(Http(), s.Sonarr) : null;
            var radarr = s.Radarr.IsConfigured ? new RadarrClient(Http(), s.Radarr) : null;
            var cap = s.MaxDeleteGbPerRun > 0 ? s.MaxDeleteGbPerRun * 1024L * 1024L * 1024L : long.MaxValue;

            // Each show's file list from Sonarr, fetched side by side (a season and its show share one list).
            var episodeFiles = new Dictionary<int, Task<List<EpisodeFile>>>();
            using var gate = new SemaphoreSlim(4, 4);
            foreach (var id in work.Where(x => x.Direct == null && x.Kind != "Movie" && x.ArrId.HasValue).Select(x => x.ArrId!.Value).Distinct())
            {
                episodeFiles[id] = FetchAsync(id);
            }

            async Task<List<EpisodeFile>> FetchAsync(int seriesId)
            {
                await gate.WaitAsync(ct);
                try
                {
                    return await sonarr!.EpisodeFilesAsync(seriesId, ct);
                }
                finally
                {
                    gate.Release();
                }
            }

            long planned = 0;
            var deletedAny = false;
            var stopped = new Dictionary<CleanupScope, Msg>();

            foreach (var w in work)
            {
                ct.ThrowIfCancellationRequested();
                var entry = new HistoryEntry { Date = DateTime.UtcNow, Kind = w.Kind, Title = w.Title, SeasonName = w.SeasonName, Parts = { w.Reason } };
                var kind = KindOf(w);
                var act = Acts(kind);

                // Safety: re-read this kind's mode before every real deletion, so switching away from Delete stops a run that's in progress.
                if (act && !stopped.ContainsKey(kind) && _settings().ModeFor(kind) is var current && current != CleanupMode.Delete)
                {
                    stopped[kind] = new Msg(kind == CleanupScope.Movies ? "outcome.stoppedMovies" : "outcome.stoppedShows", new() { ["mode"] = ModeName(current) });
                    _log.LogWarning("Cleanup of {Kind} stopped: mode changed to {Mode}", kind, current);
                }

                if (act && stopped.TryGetValue(kind, out var why))
                {
                    Skip(entry, why);
                    result.Actions.Add(entry);
                    continue;
                }

                try
                {
                    // ---- plan: what exactly would be removed, and how big is it?
                    List<EpisodeFile> files = new();
                    if (w.Direct != null)
                    {
                        entry.Bytes = _direct!.Measure(w.Direct);
                    }
                    else if (w.Kind == "Movie")
                    {
                        if (w.Movie?.FileId is null)
                        {
                            Skip(entry, new Msg("outcome.noFileRadarr"));
                            result.Actions.Add(entry);
                            continue;
                        }

                        entry.Bytes = w.Movie.Size;
                    }
                    else
                    {
                        var all = await episodeFiles[w.ArrId!.Value];
                        files = w.SeasonNumber is int n ? all.Where(f => f.SeasonNumber == n).ToList() : all;
                        if (files.Count == 0)
                        {
                            Skip(entry, new Msg("outcome.noFilesSonarr"));
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

                    // The size limit covers what this run removes (or, in a preview, would remove).
                    if (dryRun || act)
                    {
                        if (planned > 0 && planned + entry.Bytes > cap)
                        {
                            Skip(entry, new Msg("outcome.limit", new() { ["gb"] = s.MaxDeleteGbPerRun }));
                            result.Actions.Add(entry);
                            continue;
                        }

                        planned += entry.Bytes;
                    }

                    if (!act)
                    {
                        entry.Action = "WouldDelete";
                        if (w.Direct != null)
                        {
                            entry.Parts.Add(new Msg("outcome.viaJellyfin"));
                        }

                        if (dryRun)
                        {
                            result.TotalBytes += entry.Bytes; // in a real run, the total is what was actually removed
                        }
                        result.Actions.Add(entry);
                        continue;
                    }

                    // ---- act
                    if (w.Direct != null)
                    {
                        entry.Parts.Add(Msg.Text(_direct!.Delete(w.Direct)));
                    }
                    else if (w.Kind == "Movie")
                    {
                        // Unmonitor first; if that fails nothing is deleted.
                        await radarr!.UnmonitorAsync(w.ArrId!.Value, ct);
                        await radarr.DeleteMovieFileAsync(w.Movie!.FileId!.Value, ct);
                        entry.Parts.Add(new Msg("outcome.movieDeleted"));
                    }
                    else
                    {
                        await sonarr!.UnmonitorAsync(w.ArrId!.Value, w.SeasonNumber, ct);
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

                        entry.Parts.Add(new Msg(w.SeasonNumber is null ? "outcome.showDeleted" : "outcome.seasonDeleted", new() { ["files"] = files.Count }));
                    }

                    entry.Action = "Deleted";
                    result.TotalBytes += entry.Bytes;
                    deletedAny = true;
                    _log.LogInformation("Clearr deleted {Kind} {Title} {Season} ({Bytes} bytes)", w.Kind, w.Title, w.SeasonName, entry.Bytes);
                }
                catch (Exception ex) when (ex is ApiException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    entry.Action = "Failed";
                    entry.Parts = new() { Msg.Text(ex.Message) };
                    _log.LogError("Clearr failed {Kind} {Title} {Season}: {Message}", w.Kind, w.Title, w.SeasonName, ex.Message);
                }

                result.Actions.Add(entry);
            }

            // Let any file-list requests still in flight finish (their errors were reported per item above).
            await Task.WhenAll(episodeFiles.Values).ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

            if (deletedAny && s.RefreshJellyfinAfterCleanup)
            {
                try
                {
                    await _source.RefreshAsync(ct);
                }
                catch (Exception ex) when (ex is ApiException or InvalidOperationException)
                {
                    _log.LogWarning("Jellyfin refresh failed: {Message}", ex.Message);
                }
            }

            foreach (var a in result.Actions)
            {
                a.Detail = Messages.English(a.Parts);
            }

            if (record && result.Actions.Count > 0)
            {
                _history.Add(result.Actions);
            }

            foreach (var a in result.Actions)
            {
                _log.LogDebug("{Action} {Kind} {Title} {Season} ({Bytes} bytes): {Detail}", a.Action, a.Kind, a.Title, a.SeasonName, a.Bytes, a.Detail);
            }

            _log.LogInformation(
                "{Run} finished: {Deleted} deleted, {Would} would be removed, {Skipped} held back, {Failed} failed, {Gb:0.0} GB",
                dryRun ? "Preview" : "Cleanup",
                result.Actions.Count(a => a.Action == "Deleted"),
                result.Actions.Count(a => a.Action == "WouldDelete"),
                result.Actions.Count(a => a.Action == "Skipped"),
                result.Actions.Count(a => a.Action == "Failed"),
                result.TotalBytes / 1073741824.0);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>What a scheduled run does in each mode: null = don't run, true = dry run, false = delete.</summary>
    public static bool? ScheduledDryRun(CleanupMode mode) => mode switch
    {
        CleanupMode.Delete => false,
        CleanupMode.DryRun => true,
        _ => null,
    };

    public static Msg ModeName(CleanupMode m) => new("modeName." + m);

    private static void Skip(HistoryEntry e, Msg why)
    {
        e.Action = "Skipped";
        e.Parts = new() { why };
    }

    /// <summary>Returns a reason if Sonarr has more files in a season than Jellyfin knows episodes.</summary>
    public static Msg? UnseenFiles(ShowInfo show, List<EpisodeFile> files)
    {
        foreach (var g in files.GroupBy(f => f.SeasonNumber))
        {
            var known = show.Seasons.Where(x => x.Number == g.Key).Sum(x => x.Episodes.Count);
            if (g.Count() > known)
            {
                return new Msg("outcome.unseen", new() { ["files"] = g.Count(), ["season"] = g.Key, ["known"] = known });
            }
        }

        return null;
    }
}

/// <summary>Reads the library through Jellyfin's web API (used by the Clearr app).</summary>
public class JellyfinApiSource : ILibrarySource
{
    private readonly IHttpClientFactory _http;
    private readonly Func<ClearrSettings> _settings;

    public JellyfinApiSource(IHttpClientFactory http, Func<ClearrSettings> settings)
    {
        _http = http;
        _settings = settings;
    }

    private JellyfinClient Client()
    {
        var s = _settings();
        if (!s.Jellyfin.IsConfigured)
        {
            throw new ApiException("Jellyfin isn't set up yet. Add its URL and API key in Settings.");
        }

        var c = _http.CreateClient("clearr");
        c.Timeout = TimeSpan.FromSeconds(100);
        return new JellyfinClient(c, s.Jellyfin);
    }

    public Task<LibrarySnapshot> LoadAsync(CancellationToken ct) => Client().LoadAsync(ct);

    public Task RefreshAsync(CancellationToken ct) => Client().RefreshLibraryAsync(ct);
}

/// <summary>The shared web UI (ui/ folder), embedded so both the app and the plugin serve identical files.</summary>
public static class ClearrUi
{
    public static Stream? Open(string name) =>
        typeof(ClearrUi).Assembly.GetManifestResourceStream("ClearrUi." + Path.GetFileName(name));

    public static string ContentType(string name) => Path.GetExtension(name) switch
    {
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".json" => "application/json; charset=utf-8",
        _ => "application/octet-stream",
    };
}

/// <summary>Validation both hosts apply when settings are saved.</summary>
public static class SettingsRules
{
    private static readonly string[] TimeFormats = { "H:mm", "HH:mm" };

    public static string? Normalize(ClearrSettings s)
    {
        s.GraceDays = Math.Max(0, s.GraceDays);
        s.InactiveDays = Math.Max(1, s.InactiveDays);
        s.SampledMaxEpisodes = Math.Max(0, s.SampledMaxEpisodes);
        s.MaxDeleteGbPerRun = Math.Max(0, s.MaxDeleteGbPerRun);
        s.KeepTag = (s.KeepTag ?? "").Trim();
        s.ExcludedUserIds ??= new();
        s.ExcludedRootFolders = (s.ExcludedRootFolders ?? new()).Select(RootFolders.Normalize).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var time in new[] { s.ShowsScheduleTime, s.MoviesScheduleTime })
        {
            if (!TimeOnly.TryParseExact((time ?? "").Trim(), TimeFormats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            {
                return "Schedule times must look like 04:00";
            }
        }

        s.ShowsScheduleTime = s.ShowsScheduleTime.Trim();
        s.MoviesScheduleTime = s.MoviesScheduleTime.Trim();
        return s.Https.Port is < 1 or > 65535 ? "HTTPS port must be between 1 and 65535" : null;
    }
}

public static class RootFolders
{
    /// <summary>Paths compare with or without a trailing slash, ignoring case.</summary>
    public static string Normalize(string? path)
    {
        var p = (path ?? "").Trim();
        return p.Length > 1 ? p.TrimEnd('/', '\\') : p;
    }

    public static bool Excluded(string? root, ClearrSettings s) =>
        root != null && s.ExcludedRootFolders.Count > 0
        && s.ExcludedRootFolders.Any(x => string.Equals(Normalize(x), Normalize(root), StringComparison.OrdinalIgnoreCase));
}
