using System.Globalization;

namespace Clearr.Core;

/// <summary>
/// The heart of Clearr: who started what, who finished it, who dropped off,
/// and whether a season/show/movie is clear to remove. Pure: no I/O.
/// </summary>
public static class Analyzer
{
    public static WatchReport Build(LibrarySnapshot lib, ClearrSettings s, Func<string, bool> keptInClearr, DateTime now)
    {
        var excluded = new HashSet<string>(s.ExcludedUserIds ?? new(), StringComparer.OrdinalIgnoreCase);
        var users = lib.Users.Where(u => !excluded.Contains(u.Id)).ToList();

        var report = new WatchReport
        {
            GeneratedAt = now,
            TrackedUsers = users.Select(u => u.Name).ToList(),
            SonarrConnected = lib.SonarrConnected,
            RadarrConnected = lib.RadarrConnected,
            Warnings = lib.Warnings.ToList(),
        };

        foreach (var show in lib.Shows.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var r = AnalyzeShow(show, lib, s, users, keptInClearr, now);
            if (r != null)
            {
                report.Shows.Add(r);
            }
        }

        foreach (var movie in lib.Movies.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            report.Movies.Add(AnalyzeMovie(movie, lib, s, users, keptInClearr, now));
        }

        return report;
    }

    private static ShowReport? AnalyzeShow(ShowInfo show, LibrarySnapshot lib, ClearrSettings s, List<MediaUser> users, Func<string, bool> keptInClearr, DateTime now)
    {
        var seasons = show.Seasons
            .Where(x => x.Episodes.Count > 0 && (s.IncludeSpecials || x.Number != 0))
            .OrderBy(x => x.Number ?? int.MaxValue)
            .ToList();
        if (seasons.Count == 0)
        {
            return null;
        }

        var allEpisodes = seasons.SelectMany(x => x.Episodes).ToList();
        var (showKept, showKeptBy) = Kept(show.Id, show.Tags, show.ArrKeep, "Sonarr", s, keptInClearr);

        var r = new ShowReport
        {
            ShowId = show.Id,
            Name = show.Name,
            Status = show.Status,
            EpisodeCount = allEpisodes.Count,
            Kept = showKept,
            KeptBy = showKeptBy,
            Managed = show.ArrId.HasValue,
            ArrId = show.ArrId,
            TvdbId = show.ProviderIds.TryGetValue("Tvdb", out var tvdb) ? tvdb : null,
        };

        var maxSeason = seasons.Where(x => x.Number > 0).Select(x => x.Number!.Value).DefaultIfEmpty(-1).Max();
        var continuing = string.Equals(show.Status, "Continuing", StringComparison.OrdinalIgnoreCase);
        var notManaged = lib.SonarrConnected && !show.ArrId.HasValue ? "Not in Sonarr — Clearr only removes shows Sonarr manages" : null;
        var noSonarr = !lib.SonarrConnected ? "Sonarr isn't connected" : null;

        foreach (var season in seasons)
        {
            var (seasonKept, seasonKeptBy) = Kept(season.Id, season.Tags, false, null, s, keptInClearr);
            var sr = new SeasonReport
            {
                SeasonId = season.Id,
                Number = season.Number,
                Name = string.IsNullOrWhiteSpace(season.Name) ? $"Season {season.Number}" : season.Name,
                EpisodeCount = season.Episodes.Count,
                Kept = seasonKept,
                KeptBy = seasonKeptBy,
            };
            foreach (var u in users)
            {
                sr.Users.Add(Measure(u, season.Episodes, lib, s, now));
            }

            string? block = null;
            if (s.ProtectLatestSeasonOfContinuing && continuing && season.Number == maxSeason)
            {
                block = "Newest season of a show that's still continuing";
            }

            (sr.Eligible, sr.Reason) = Decide(sr.Users, s, now, showKept || seasonKept, block ?? noSonarr ?? notManaged);
            r.Seasons.Add(sr);
        }

        foreach (var u in users)
        {
            r.Users.Add(Measure(u, allEpisodes, lib, s, now));
        }

        string? showBlock = null;
        if (s.RequireEndedForShowDelete && !string.Equals(show.Status, "Ended", StringComparison.OrdinalIgnoreCase))
        {
            showBlock = show.Status != null ? $"Show status is {show.Status}" : "Show status is unknown (not marked Ended)";
        }
        else if (r.Seasons.Any(x => x.Kept))
        {
            showBlock = "A season is marked keep (other seasons can still be removed)";
        }

        (r.Eligible, r.Reason) = Decide(r.Users, s, now, showKept, showBlock ?? noSonarr ?? notManaged);
        return r;
    }

    private static MovieReport AnalyzeMovie(MovieInfo movie, LibrarySnapshot lib, ClearrSettings s, List<MediaUser> users, Func<string, bool> keptInClearr, DateTime now)
    {
        var (kept, keptBy) = Kept(movie.Id, movie.Tags, movie.ArrKeep, "Radarr", s, keptInClearr);
        var r = new MovieReport
        {
            MovieId = movie.Id,
            Name = movie.Name,
            Year = movie.Year,
            Kept = kept,
            KeptBy = keptBy,
            Managed = movie.ArrId.HasValue,
            ArrId = movie.ArrId,
        };

        var single = new List<EpisodeInfo> { new() { Id = movie.Id } };
        foreach (var u in users)
        {
            var p = Measure(u, single, lib, s, now);
            p.LastEpisode = null;
            r.Users.Add(p);
        }

        string? block = !lib.RadarrConnected ? "Radarr isn't connected"
            : !movie.ArrId.HasValue ? "Not in Radarr — Clearr only removes movies Radarr manages"
            : null;
        (r.Eligible, r.Reason) = Decide(r.Users, s, now, kept, block);
        return r;
    }

    private static (bool, string?) Kept(string id, List<string> tags, bool arrKeep, string? arrName, ClearrSettings s, Func<string, bool> keptInClearr)
    {
        if (keptInClearr(id))
        {
            return (true, "Clearr");
        }

        if (arrKeep)
        {
            return (true, arrName + " tag");
        }

        var tag = (s.KeepTag ?? "").Trim();
        if (tag.Length > 0 && tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
        {
            return (true, "Jellyfin tag");
        }

        return (false, null);
    }

    internal static UserProgress Measure(MediaUser user, List<EpisodeInfo> episodes, LibrarySnapshot lib, ClearrSettings s, DateTime now)
    {
        var p = new UserProgress { UserId = user.Id, UserName = user.Name, Total = episodes.Count };
        EpisodeInfo? last = null;
        var lastPartial = false;

        foreach (var ep in episodes)
        {
            var d = lib.Get(ep.Id, user.Id);
            if (d is null)
            {
                continue;
            }

            var partial = !d.Played && d.PositionTicks > 0;
            if (d.Played)
            {
                p.Watched++;
            }
            else if (partial)
            {
                p.Partial++;
            }

            if ((d.Played || partial) && d.LastPlayed.HasValue && (!p.LastActivity.HasValue || d.LastPlayed.Value >= p.LastActivity.Value))
            {
                p.LastActivity = d.LastPlayed;
                last = ep;
                lastPartial = partial;
            }
        }

        if (p.Watched == 0 && p.Partial == 0)
        {
            return p;
        }

        if (last != null)
        {
            p.LastEpisode = string.Format(CultureInfo.InvariantCulture, "S{0:00}E{1:00}", last.Season ?? 0, last.Number ?? 0);
        }

        if (p.Watched == p.Total)
        {
            p.Status = WatchStatus.Finished;
            p.FinishedAt = p.LastActivity;
            return p;
        }

        p.Status = WatchStatus.InProgress;
        var inactive = !p.LastActivity.HasValue || (now - p.LastActivity.Value).TotalDays >= Math.Max(1, s.InactiveDays);
        if (inactive)
        {
            p.Flags.Add(p.Watched <= Math.Max(0, s.SampledMaxEpisodes) ? WatchFlag.Sampled : WatchFlag.Stalled);
            if (lastPartial)
            {
                p.Flags.Add(WatchFlag.MidEpisode);
            }
        }

        return p;
    }

    internal static (bool, string) Decide(List<UserProgress> users, ClearrSettings s, DateTime now, bool kept, string? block)
    {
        var starters = users.Where(u => u.Status != WatchStatus.NotStarted).ToList();
        if (starters.Count == 0)
        {
            return (false, "Nobody has started it");
        }

        var counted = s.AbandonedUsersBlockCleanup ? starters : starters.Where(u => !u.IsAbandoned).ToList();
        var finished = counted.Where(u => u.Status == WatchStatus.Finished).ToList();
        var waiting = counted.Where(u => u.Status != WatchStatus.Finished).ToList();

        if (waiting.Count > 0)
        {
            return (false, "Waiting on " + string.Join(", ", waiting.Select(Describe)));
        }

        if (finished.Count == 0)
        {
            return (false, "Nobody has finished it (everyone who started dropped off)");
        }

        if (kept)
        {
            return (false, "Everyone finished, but it's marked keep");
        }

        if (block != null)
        {
            return (false, "Everyone finished, but: " + block);
        }

        var lastFinish = finished.Where(u => u.FinishedAt.HasValue).Select(u => u.FinishedAt!.Value).DefaultIfEmpty(DateTime.MinValue).Max();
        if (lastFinish != DateTime.MinValue)
        {
            var graceEnds = lastFinish.AddDays(Math.Max(0, s.GraceDays));
            if (graceEnds > now)
            {
                return (false, "Everyone finished; grace period until " + graceEnds.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
        }

        var skipped = starters.Count - counted.Count;
        return (true, "Finished by " + string.Join(", ", finished.Select(u => u.UserName)) + (skipped > 0 ? $" ({skipped} dropped-off viewer(s) ignored)" : ""));
    }

    private static string Describe(UserProgress u)
    {
        var d = $"{u.UserName} ({u.Watched}/{u.Total}";
        if (u.Flags.Count > 0)
        {
            d += ", " + string.Join(", ", u.Flags.Select(f => f == WatchFlag.MidEpisode ? "mid-episode" : f.ToLowerInvariant()));
        }

        return d + ")";
    }
}
