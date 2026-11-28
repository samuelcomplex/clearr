// Deletion safety tests: every way a cleanup can run, checked against what may be deleted.
// The randomized part re-implements the rules independently and compares, so a bug in the
// Analyzer can't hide behind tests written from the same code.
using System.Text.Json.Nodes;
using Clearr.Core;
using Clearr.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clearr.Tests;

internal static class SafetyTests
{
    private const long GB = 1024L * 1024 * 1024;

    public static async Task<int> RunAsync()
    {
        var fails = 0;
        void Expect(bool ok, string what)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + "[safety] " + what);
            if (!ok)
            {
                fails++;
            }
        }

        await Scenarios(Expect);
        await Randomized(Expect, worlds: 200);
        return fails;
    }

    // ------------------------------------------------------------------ fixed scenarios

    private static async Task Scenarios(Action<bool, string> expect)
    {
        Expect(ClearrEngine.ScheduledDryRun(CleanupMode.Off) == null, "Schedule: Off doesn't run at all");
        Expect(ClearrEngine.ScheduledDryRun(CleanupMode.DryRun) == true, "Schedule: Dry run only previews");
        Expect(ClearrEngine.ScheduledDryRun(CleanupMode.Delete) == false, "Schedule: only Delete deletes");

        void Expect(bool ok, string what) => expect(ok, what);

        // A world where everything is finished and eligible.
        var b = new WorldBuilder(seed: 1);
        b.User("u1", "Anna");
        var s1 = b.Show("Alpha", "Ended", new[] { 2, 2 }, inSonarr: true, gbPerFile: 2);
        var s2 = b.Show("Beta", "Ended", new[] { 3 }, inSonarr: true, gbPerFile: 1);
        var s3 = b.Show("Gamma", "Ended", new[] { 1 }, inSonarr: true, gbPerFile: 1);
        var m1 = b.Movie("Delta", inRadarr: true, gb: 5);
        foreach (var s in new[] { s1, s2, s3 })
        {
            b.WatchAll("u1", s, daysAgo: 30);
        }

        b.WatchMovie("u1", m1, played: true, daysAgo: 30);
        var env = await Env.StartAsync(b.World, new ClearrSettings { ShowsMode = CleanupMode.Off, MoviesMode = CleanupMode.Off });

        // Off and DryRun: real cleanup refused through every entry point, nothing touched.
        foreach (var mode in new[] { CleanupMode.Off, CleanupMode.DryRun })
        {
            env.Mode(mode);
            var r = await env.Engine.CleanupAsync(false, true, default);
            Expect(r.ErrorMsg?.Code == "error.refusedBoth" && r.Error!.Contains(Messages.English(ClearrEngine.ModeName(mode))) && env.World.Log.Count == 0 && env.History.GetAll().Count == 0, $"{mode}: real cleanup refused, nothing changed, nothing recorded");
            var direct = await env.DirectEngine.CleanupAsync(false, true, default);
            Expect(direct.Error != null && env.Deleter.Deleted.Count == 0, $"{mode}: Jellyfin fallback refused too");
        }

        // Delete mode, but a preview: still nothing touched.
        env.Mode(CleanupMode.Delete);
        var preview = await env.Engine.CleanupAsync(true, false, default);
        Expect(preview.Actions.Count(a => a.Action == "WouldDelete") == 4 && env.World.Log.Count == 0 && env.History.GetAll().Count == 0, "Delete mode + preview: lists 4 items, changes nothing, records nothing");

        // Mode switched to Dry run while a cleanup is running: it stops before the next item.
        env.World.OnChange = line =>
        {
            if (line.StartsWith("DELETE episodefile", StringComparison.Ordinal))
            {
                env.Mode(CleanupMode.DryRun);
            }
        };
        var interrupted = await env.Engine.CleanupAsync(false, true, default);
        env.World.OnChange = null;
        var deleted = interrupted.Actions.Where(a => a.Action == "Deleted").ToList();
        Expect(deleted.Count == 1 && interrupted.Actions.Count(a => a.Action == "Skipped" && a.Detail.Contains("changed to Dry run")) == 3, "Mode changed mid-run: the item in progress finishes, the other 3 are skipped");
        Expect(env.World.EpisodeFiles.Any(f => f.SeriesId == s2.SonarrId) && env.World.EpisodeFiles.Any(f => f.SeriesId == s3.SonarrId) && env.World.RadarrMovies[m1.RadarrId!.Value]["movieFile"] != null, "Mode changed mid-run: the other shows and the movie are untouched");

        // Shows and movies follow their own modes.
        var bk = new WorldBuilder(seed: 5);
        bk.User("u1", "Anna");
        var ks1 = bk.Show("Kind Show", "Ended", new[] { 2 }, inSonarr: true, gbPerFile: 3);
        var km1 = bk.Movie("Kind Movie", inRadarr: true, gb: 40);
        bk.WatchAll("u1", ks1, 30);
        bk.WatchMovie("u1", km1, true, 30);
        var envk = await Env.StartAsync(bk.World, new ClearrSettings { ShowsMode = CleanupMode.Delete, MoviesMode = CleanupMode.DryRun, MaxDeleteGbPerRun = 10 });
        var onlyMovies = await envk.Engine.CleanupAsync(false, true, default, CleanupScope.Movies);
        Expect(onlyMovies.ErrorMsg?.Code == "error.refusedMovies" && bk.World.Log.Count == 0, "Movies in Dry run: deleting movies only is refused, even though shows are in Delete");
        var mixed = await envk.Engine.CleanupAsync(false, true, default);
        var mixedMovie = mixed.Actions.Single(a => a.Kind == "Movie");
        Expect(mixed.Actions.Single(a => a.Title == "Kind Show").Action == "Deleted" && !bk.World.EpisodeFiles.Any(f => f.SeriesId == ks1.SonarrId), "Mixed run: the show (Delete) is removed");
        Expect(mixedMovie.Action == "WouldDelete" && bk.World.RadarrMovies[km1.RadarrId!.Value]["movieFile"] != null && !bk.World.Log.Any(l => l.Contains("movie", StringComparison.Ordinal)), "Mixed run: the movie (Dry run) is only listed, Radarr untouched");
        Expect(mixed.TotalBytes == 6 * GB, "Mixed run: the total counts only what was really removed (the 40 GB movie preview doesn't use up the 10 GB limit)");
        var schedShows = ClearrEngine.ScheduledDryRun(envk.Settings.Get().ShowsMode);
        var schedMovies = ClearrEngine.ScheduledDryRun(envk.Settings.Get().MoviesMode);
        Expect(schedShows == false && schedMovies == true, "Scheduled runs: shows delete, movies only preview");
        await envk.StopAsync();

        // Unmonitor fails: nothing of that item is deleted, and the failure is recorded.
        var b2 = new WorldBuilder(seed: 2);
        b2.User("u1", "Anna");
        var f1 = b2.Show("Fails To Unmonitor", "Ended", new[] { 2 }, inSonarr: true, gbPerFile: 1);
        var f2 = b2.Show("Fails Halfway", "Ended", new[] { 3 }, inSonarr: true, gbPerFile: 1);
        var fm = b2.Movie("Movie Fails To Unmonitor", inRadarr: true, gb: 2);
        b2.WatchAll("u1", f1, 30);
        b2.WatchAll("u1", f2, 30);
        b2.WatchMovie("u1", fm, true, 30);
        b2.World.FailSeriesPut.Add(f1.SonarrId!.Value);
        b2.World.FailMoviePut.Add(fm.RadarrId!.Value);
        var halfwayFiles = b2.World.EpisodeFiles.Where(f => f.SeriesId == f2.SonarrId).Select(f => f.Id).ToList();
        b2.World.FailEpisodeFileDelete.Add(halfwayFiles[1]);
        var env2 = await Env.StartAsync(b2.World, new ClearrSettings { ShowsMode = CleanupMode.Delete, MoviesMode = CleanupMode.Delete });
        var r2 = await env2.Engine.CleanupAsync(false, true, default);
        var a1 = r2.Actions.Single(a => a.Title == "Fails To Unmonitor");
        Expect(a1.Action == "Failed" && b2.World.EpisodeFiles.Count(f => f.SeriesId == f1.SonarrId) == 2, "Unmonitor fails in Sonarr: no files deleted, recorded as Failed");
        var am = r2.Actions.Single(a => a.Title.StartsWith("Movie Fails", StringComparison.Ordinal));
        Expect(am.Action == "Failed" && b2.World.RadarrMovies[fm.RadarrId.Value]["movieFile"] != null, "Unmonitor fails in Radarr: movie file kept, recorded as Failed");
        var a2 = r2.Actions.Single(a => a.Title == "Fails Halfway");
        Expect(a2.Action == "Failed" && a2.Detail.Contains("after deleting 1 of 3"), "Delete fails halfway: reported with how far it got (" + a2.Detail + ")");
        Expect(env2.History.GetAll().Count(h => h.Action == "Failed") == 3, "Failures are written to History");

        // Every kind of keep holds in Delete mode.
        var b3 = new WorldBuilder(seed: 3);
        b3.User("u1", "Anna");
        var k1 = b3.Show("Kept In Clearr", "Ended", new[] { 1 }, inSonarr: true, gbPerFile: 1);
        var k2 = b3.Show("Kept By Jellyfin Tag", "Ended", new[] { 1 }, inSonarr: true, gbPerFile: 1, jellyfinTags: new[] { "KEEP" });
        var k3 = b3.Show("Kept By Sonarr Tag", "Ended", new[] { 1 }, inSonarr: true, gbPerFile: 1, sonarrTags: new[] { "keep" });
        var k4 = b3.Show("Season Kept", "Ended", new[] { 1, 1 }, inSonarr: true, gbPerFile: 1);
        var km = b3.Movie("Movie Kept In Clearr", inRadarr: true, gb: 1);
        foreach (var s in new[] { k1, k2, k3, k4 })
        {
            b3.WatchAll("u1", s, 30);
        }

        b3.WatchMovie("u1", km, true, 30);
        var env3 = await Env.StartAsync(b3.World, new ClearrSettings { ShowsMode = CleanupMode.Delete, MoviesMode = CleanupMode.Delete });
        env3.Keep.Set(k1.Id, true);
        env3.Keep.Set(k4.Seasons[1].Id, true);
        env3.Keep.Set(km.Id, true);
        var r3 = await env3.Engine.CleanupAsync(false, true, default);
        Expect(b3.World.EpisodeFiles.Count(f => f.SeriesId == k1.SonarrId || f.SeriesId == k2.SonarrId || f.SeriesId == k3.SonarrId) == 3, "Keep in Clearr, Jellyfin tag (any case) and Sonarr tag all protect a show");
        Expect(b3.World.EpisodeFiles.Any(f => f.SeriesId == k4.SonarrId && f.Season == 2) && !b3.World.EpisodeFiles.Any(f => f.SeriesId == k4.SonarrId && f.Season == 1), "Kept season stays; the show's other season is removed");
        Expect(b3.World.RadarrMovies[km.RadarrId!.Value]["movieFile"] != null, "Keep in Clearr protects a movie");

        // Favourites: kept while the option is on, but only favourites of tracked (not ignored) users count.
        var bf = new WorldBuilder(seed: 6);
        bf.User("u1", "Anna");
        bf.User("u2", "Guest");
        var fav1 = bf.Show("Anna's Favourite", "Ended", new[] { 1 }, inSonarr: true, gbPerFile: 1);
        var fav2 = bf.Show("Favourite Season", "Ended", new[] { 1, 1 }, inSonarr: true, gbPerFile: 1);
        var fav3 = bf.Show("Ignored Guest's Favourite", "Ended", new[] { 1 }, inSonarr: true, gbPerFile: 1);
        var favM = bf.Movie("Favourite Movie", inRadarr: true, gb: 1);
        foreach (var s in new[] { fav1, fav2, fav3 })
        {
            bf.WatchAll("u1", s, 30);
        }

        bf.WatchMovie("u1", favM, true, 30);
        bf.World.Favorites.Add(("u1", fav1.Id));
        bf.World.Favorites.Add(("u1", fav2.Seasons[0].Id));
        bf.World.Favorites.Add(("u2", fav3.Id));
        bf.World.Favorites.Add(("u1", favM.Id));
        var envf = await Env.StartAsync(bf.World, new ClearrSettings { ShowsMode = CleanupMode.Delete, MoviesMode = CleanupMode.Delete, ExcludedUserIds = new() { "u2" } });
        var (_, favReport, _, _) = await envf.Engine.BuildAsync(default);
        Expect(favReport.Shows.Single(x => x.Name == "Anna's Favourite").KeptBy == "Favourite" && favReport.Movies.Single().KeptBy == "Favourite", "Favourite show and movie are kept (shown as kept by a favourite)");
        await envf.Engine.CleanupAsync(false, true, default);
        Expect(bf.World.EpisodeFiles.Count(f => f.SeriesId == fav1.SonarrId) == 1 && bf.World.RadarrMovies[favM.RadarrId!.Value]["movieFile"] != null, "Favourites survive a real cleanup");
        Expect(bf.World.EpisodeFiles.Any(f => f.SeriesId == fav2.SonarrId && f.Season == 1) && !bf.World.EpisodeFiles.Any(f => f.SeriesId == fav2.SonarrId && f.Season == 2), "A favourite season stays; the show's other season goes");
        Expect(!bf.World.EpisodeFiles.Any(f => f.SeriesId == fav3.SonarrId), "An ignored user's favourite doesn't hold anything back");
        var favOff = envf.Settings.Get();
        favOff.KeepFavorites = false;
        envf.Settings.Save(favOff);
        await envf.Engine.CleanupAsync(false, true, default);
        Expect(!bf.World.EpisodeFiles.Any(f => f.SeriesId == fav1.SonarrId), "With the option off, favourites are cleaned up like anything else");
        await envf.StopAsync();

        // Root folders left out: everything in them is kept, wherever the trailing slash is.
        var br = new WorldBuilder(seed: 7);
        br.User("u1", "Anna");
        var kids = br.Show("Kids Show", "Ended", new[] { 1 }, inSonarr: true, gbPerFile: 1, root: "/media/kids/");
        var adult = br.Show("Adult Show", "Ended", new[] { 1 }, inSonarr: true, gbPerFile: 1, root: "/media/tv/");
        var kidsMovie = br.Movie("Kids Movie", inRadarr: true, gb: 1, root: "/media/kids-movies");
        br.WatchAll("u1", kids, 30);
        br.WatchAll("u1", adult, 30);
        br.WatchMovie("u1", kidsMovie, true, 30);
        var envr = await Env.StartAsync(br.World, new ClearrSettings { ShowsMode = CleanupMode.Delete, MoviesMode = CleanupMode.Delete, ExcludedRootFolders = new() { "/Media/Kids", "/media/kids-movies/" } });
        var (_, rootReport, _, _) = await envr.Engine.BuildAsync(default);
        Expect(rootReport.Shows.Single(x => x.Name == "Kids Show").KeptBy == "Root folder" && rootReport.Movies.Single().KeptBy == "Root folder" && rootReport.Shows.Single(x => x.Name == "Adult Show").Eligible, "Items in a left-out root folder are kept; others aren't");
        await envr.Engine.CleanupAsync(false, true, default);
        Expect(br.World.EpisodeFiles.Any(f => f.SeriesId == kids.SonarrId) && br.World.RadarrMovies[kidsMovie.RadarrId!.Value]["movieFile"] != null && !br.World.EpisodeFiles.Any(f => f.SeriesId == adult.SonarrId), "A real cleanup leaves left-out root folders alone");
        var (sonarrRoots, radarrRoots) = await envr.Engine.RootFoldersAsync(default);
        Expect(sonarrRoots.Contains("/media/kids/") && sonarrRoots.Contains("/media/tv/") && radarrRoots.SequenceEqual(new[] { "/media/kids-movies" }), "Root folders listed from Sonarr and Radarr");
        await envr.StopAsync();

        // Grace period, someone still watching, someone who never started.
        var b4 = new WorldBuilder(seed: 4);
        b4.User("u1", "Anna");
        b4.User("u2", "Bjorn");
        b4.User("u3", "Cleo");
        var g1 = b4.Show("Finished Yesterday", "Ended", new[] { 2 }, inSonarr: true, gbPerFile: 1);
        var g2 = b4.Show("Bjorn Still Watching", "Ended", new[] { 3 }, inSonarr: true, gbPerFile: 1);
        b4.WatchAll("u1", g1, 1);
        b4.WatchAll("u1", g2, 30);
        b4.Watch("u2", g2.Episodes.Take(1), 1);
        var env4 = await Env.StartAsync(b4.World, new ClearrSettings { ShowsMode = CleanupMode.Delete, MoviesMode = CleanupMode.Delete, GraceDays = 7 });
        var r4 = await env4.Engine.CleanupAsync(false, true, default);
        Expect(b4.World.Log.Count == 0 && r4.Actions.Count == 0, "Grace period and an active viewer both block deletion (Cleo never started and doesn't matter)");
    }

    // ------------------------------------------------------------------ randomized

    private static async Task Randomized(Action<bool, string> expect, int worlds)
    {
        var violations = new List<string>();
        int deletedItems = 0, realRuns = 0, deletedMovies = 0, listedMovies = 0;
        for (var seed = 100; seed < 100 + worlds; seed++)
        {
            var rnd = new Random(seed);
            var rootRnd = new Random(seed * 7919); // separate, so adding root folders didn't change the other random choices
            var b = new WorldBuilder(seed);
            var users = Enumerable.Range(1, rnd.Next(1, 4)).Select(i => b.User("u" + i, "User" + i)).ToList();
            var shows = new List<WorldBuilder.ShowRef>();
            for (var i = 0; i < rnd.Next(1, 6); i++)
            {
                var eps = Enumerable.Range(0, rnd.Next(1, 4)).Select(_ => rnd.Next(1, 5)).ToArray();
                shows.Add(b.Show("Show" + i, rnd.Next(3) == 0 ? "Continuing" : "Ended", eps, inSonarr: rnd.Next(5) > 0, gbPerFile: rnd.Next(1, 5),
                    extraFile: rnd.Next(7) == 0, sonarrTags: rnd.Next(7) == 0 ? new[] { "keep" } : null, jellyfinTags: rnd.Next(10) == 0 ? new[] { "Keep" } : null,
                    root: rootRnd.Next(4) == 0 ? "/kids/" : "/tv/"));
            }

            var movies = new List<WorldBuilder.MovieRef>();
            for (var i = 0; i < rnd.Next(0, 5); i++)
            {
                movies.Add(b.Movie("Movie" + i, inRadarr: rnd.Next(5) > 0, gb: rnd.Next(1, 9), jellyfinTags: rnd.Next(10) == 0 ? new[] { "keep" } : null, root: rootRnd.Next(4) == 0 ? "/kids-movies" : "/movies"));
            }

            foreach (var u in users)
            {
                foreach (var s in shows)
                {
                    switch (rnd.Next(7))
                    {
                        case 0: break;
                        case 1 or 5 or 6: b.WatchAll(u, s, rnd.Next(0, 60)); break;
                        case 2: b.Watch(u, s.Episodes.Take(rnd.Next(1, s.Episodes.Count + 1)), rnd.Next(0, 60)); break;
                        case 3: b.Watch(u, s.Episodes.Where(e => e.Season == 1), rnd.Next(0, 60)); break;
                        default: b.Partial(u, s.Episodes[rnd.Next(s.Episodes.Count)], rnd.Next(0, 60)); break;
                    }
                }

                foreach (var id in shows.Select(s => s.Id).Concat(shows.SelectMany(s => s.Seasons.Select(x => x.Id))).Concat(movies.Select(m => m.Id)))
                {
                    if (rnd.Next(25) == 0)
                    {
                        b.World.Favorites.Add((u, id));
                    }
                }

                foreach (var m in movies)
                {
                    if (rnd.Next(3) > 0)
                    {
                        b.WatchMovie(u, m, played: rnd.Next(5) > 0, daysAgo: rnd.Next(0, 60));
                    }
                }
            }

            // Mode pairs (shows, movies), weighted towards mixed ones: they're where a kind mix-up would show.
            var modes = new[] { (2, 2), (2, 1), (1, 2), (2, 0), (0, 2), (2, 1), (1, 2), (1, 1), (0, 0) }[rnd.Next(9)];
            var settings = new ClearrSettings
            {
                ShowsMode = (CleanupMode)modes.Item1,
                MoviesMode = (CleanupMode)modes.Item2,
                DeleteSeasons = rnd.Next(4) > 0,
                DeleteShows = rnd.Next(4) > 0,
                RequireEndedForShowDelete = rnd.Next(4) > 0,
                ProtectLatestSeasonOfContinuing = rnd.Next(4) > 0,
                GraceDays = rnd.Next(0, 15),
                InactiveDays = rnd.Next(5, 40),
                SampledMaxEpisodes = rnd.Next(0, 4),
                AbandonedUsersBlockCleanup = rnd.Next(2) == 0,
                MaxDeleteGbPerRun = new[] { 0, 2, 5, 12, 50 }[rnd.Next(5)],
                ExcludedUserIds = users.Where(_ => rnd.Next(5) == 0).ToList(),
                KeepFavorites = rnd.Next(3) > 0,
                ExcludedRootFolders = rootRnd.Next(8) switch { 0 or 1 => new() { "/KIDS", "/kids-movies/" }, 2 => new() { "/tv" }, _ => new() },
            };
            var dryRun = rnd.Next(5) == 0;
            var scope = rnd.Next(5) switch { 0 => CleanupScope.Shows, 1 => CleanupScope.Movies, _ => CleanupScope.All };
            var env = await Env.StartAsync(b.World, settings);
            var keptIds = new HashSet<string>();
            foreach (var id in shows.Select(s => s.Id).Concat(shows.SelectMany(s => s.Seasons.Select(x => x.Id))).Concat(movies.Select(m => m.Id)))
            {
                if (rnd.Next(10) == 0)
                {
                    env.Keep.Set(id, true);
                    keptIds.Add(id);
                }
            }

            var filesBefore = b.World.EpisodeFiles.ToList();
            var result = await env.Engine.CleanupAsync(dryRun, true, default, scope);
            var tag = $"world {seed} (shows {settings.ShowsMode}, movies {settings.MoviesMode}, {scope}{(dryRun ? ", preview" : "")})";
            var log = b.World.Log;
            var realShows = !dryRun && scope.HasFlag(CleanupScope.Shows) && settings.ShowsMode == CleanupMode.Delete;
            var realMovies = !dryRun && scope.HasFlag(CleanupScope.Movies) && settings.MoviesMode == CleanupMode.Delete;

            // Each kind may only be touched when its own mode is Delete and the run covers it.
            if (!realShows && (log.Any(l => l.StartsWith("PUT series", StringComparison.Ordinal) || l.StartsWith("DELETE episodefile", StringComparison.Ordinal)) || b.World.EpisodeFiles.Count != filesBefore.Count))
            {
                violations.Add($"{tag}: changed shows without being allowed to");
            }

            if (!realMovies && log.Any(l => l.StartsWith("PUT movie", StringComparison.Ordinal) || l.StartsWith("DELETE moviefile", StringComparison.Ordinal)))
            {
                violations.Add($"{tag}: changed movies without being allowed to");
            }

            if (!realShows && !realMovies)
            {
                await env.StopAsync();
                continue;
            }

            realRuns++;
            var oracle = new Oracle(b, settings, keptIds, DateTime.UtcNow);
            var gone = filesBefore.Where(f => !b.World.EpisodeFiles.Contains(f)).ToList();

            // Every removed file must be covered by something the independent rules allow.
            foreach (var f in gone)
            {
                var show = shows.Single(s => s.SonarrId == f.SeriesId);
                var asShow = settings.DeleteShows && oracle.ShowAllowed(show);
                var asSeason = settings.DeleteSeasons && oracle.SeasonAllowed(show, f.Season);
                if (!asShow && !asSeason)
                {
                    violations.Add($"{tag}: deleted a file of {show.Name} S{f.Season} that the rules don't allow");
                }
            }

            foreach (var m in movies.Where(m => m.RadarrId != null && b.World.RadarrMovies[m.RadarrId.Value]["movieFile"] == null))
            {
                if (!oracle.MovieAllowed(m))
                {
                    violations.Add($"{tag}: deleted movie {m.Name} that the rules don't allow");
                }
            }

            // Unmonitor before delete, for every item.
            foreach (var del in log.Where(l => l.StartsWith("DELETE episodefile", StringComparison.Ordinal)))
            {
                var fid = int.Parse(del.Split(' ')[2]);
                var series = filesBefore.Single(f => f.Id == fid).SeriesId;
                if (log.IndexOf($"PUT series {series}") is var put && (put < 0 || put > log.IndexOf(del)))
                {
                    violations.Add($"{tag}: deleted a file of series {series} before unmonitoring it");
                }
            }

            // Per-run size limit: more than one item only if they fit together.
            var done = result.Actions.Where(a => a.Action == "Deleted").ToList();
            deletedItems += done.Count;
            deletedMovies += done.Count(a => a.Kind == "Movie");
            listedMovies += result.Actions.Count(a => a.Kind == "Movie" && a.Action == "WouldDelete");
            if (settings.MaxDeleteGbPerRun > 0 && done.Count > 1 && done.Sum(a => a.Bytes) > settings.MaxDeleteGbPerRun * GB)
            {
                violations.Add($"{tag}: deleted {done.Sum(a => a.Bytes) / GB} GB with a {settings.MaxDeleteGbPerRun} GB limit");
            }

            // Bytes reported match what actually went.
            var reported = done.Where(a => a.Kind != "Movie").Sum(a => a.Bytes);
            if (reported != gone.Sum(f => f.Size))
            {
                violations.Add($"{tag}: history says {reported} bytes of episodes deleted, but {gone.Sum(f => f.Size)} went");
            }

            await env.StopAsync();
        }

        foreach (var v in violations.Take(10))
        {
            Console.WriteLine("    " + v);
        }

        expect(violations.Count == 0, $"Randomized: {worlds} random libraries, {realRuns} real cleanups, {deletedItems} items deleted, {violations.Count} rule violations");
        expect(realRuns > 40 && deletedItems > 40 && deletedMovies > 5 && listedMovies > 5, $"Randomized: the random libraries actually exercise deletion ({deletedMovies} movies removed, {listedMovies} movies only listed in mixed runs)");
    }

    /// <summary>The cleanup rules written out again from the README, independent of Analyzer.</summary>
    private sealed class Oracle
    {
        private readonly WorldBuilder _b;
        private readonly ClearrSettings _s;
        private readonly HashSet<string> _kept;
        private readonly DateTime _now;

        public Oracle(WorldBuilder b, ClearrSettings s, HashSet<string> kept, DateTime now)
        {
            _b = b;
            _s = s;
            _kept = kept;
            _now = now;
        }

        private bool TagKept(IEnumerable<string> tags) => tags.Any(t => string.Equals(t, _s.KeepTag, StringComparison.OrdinalIgnoreCase));

        private bool Favourite(string id) => _s.KeepFavorites && _b.World.Favorites.Any(f => f.Item == id && !_s.ExcludedUserIds.Contains(f.User));

        private bool RootKept(string? root) => root != null && _s.ExcludedRootFolders.Any(x => string.Equals(x.TrimEnd('/'), root.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

        private bool ShowKept(WorldBuilder.ShowRef show) => _kept.Contains(show.Id) || TagKept(show.JellyfinTags) || TagKept(show.SonarrTags) || Favourite(show.Id) || RootKept(show.Root);

        public bool SeasonAllowed(WorldBuilder.ShowRef show, int season)
        {
            var maxSeason = show.Seasons.Max(x => x.Number);
            var seasonId = show.Seasons.Single(x => x.Number == season).Id;
            if (ShowKept(show) || _kept.Contains(seasonId) || Favourite(seasonId))
            {
                return false;
            }

            if (_s.ProtectLatestSeasonOfContinuing && show.Status == "Continuing" && season == maxSeason)
            {
                return false;
            }

            return NoUnseenFiles(show, season) && Done(show.Episodes.Where(e => e.Season == season).Select(e => e.Id).ToList());
        }

        public bool ShowAllowed(WorldBuilder.ShowRef show)
        {
            if (ShowKept(show) || show.Seasons.Any(x => _kept.Contains(x.Id) || Favourite(x.Id)))
            {
                return false;
            }

            if (_s.RequireEndedForShowDelete && show.Status != "Ended")
            {
                return false;
            }

            return show.Seasons.All(x => NoUnseenFiles(show, x.Number)) && Done(show.Episodes.Select(e => e.Id).ToList());
        }

        public bool MovieAllowed(WorldBuilder.MovieRef m) =>
            !_kept.Contains(m.Id) && !TagKept(m.JellyfinTags) && !Favourite(m.Id) && !RootKept(m.Root) && Done(new List<string> { m.Id });

        private bool NoUnseenFiles(WorldBuilder.ShowRef show, int season) =>
            _b.World.EpisodeFiles.Count(f => f.SeriesId == show.SonarrId && f.Season == season) <= show.Episodes.Count(e => e.Season == season);

        /// <summary>Everyone (not excluded) who started it finished it, or dropped off and that's allowed; somebody finished; grace passed.</summary>
        private bool Done(List<string> items)
        {
            var anyFinished = false;
            var lastFinish = DateTime.MinValue;
            foreach (var (uid, _) in _b.World.Users)
            {
                if (_s.ExcludedUserIds.Contains(uid))
                {
                    continue;
                }

                var data = items.Select(i => _b.World.UserData.TryGetValue((uid, i), out var d) ? d : ((bool, long, DateTime)?)null).ToList();
                var played = data.Count(d => d is { Item1: true });
                var partial = data.Count(d => d is { Item1: false, Item2: > 0 });
                if (played == 0 && partial == 0)
                {
                    continue;
                }

                var last = data.Where(d => d is { Item1: true } || d is { Item1: false, Item2: > 0 }).Max(d => d!.Value.Item3);
                if (played == items.Count)
                {
                    anyFinished = true;
                    lastFinish = last > lastFinish ? last : lastFinish;
                    continue;
                }

                var droppedOff = (_now - last).TotalDays >= Math.Max(1, _s.InactiveDays);
                if (!droppedOff || _s.AbandonedUsersBlockCleanup)
                {
                    return false;
                }
            }

            return anyFinished && lastFinish.AddDays(_s.GraceDays) <= _now;
        }
    }

    // ------------------------------------------------------------------ plumbing

    private sealed class Env
    {
        private Microsoft.AspNetCore.Builder.WebApplication _app = null!;
        private string _tmp = "";

        public FakeWorld World { get; private set; } = null!;

        public SettingsStore Settings { get; private set; } = null!;

        public KeepStore Keep { get; private set; } = null!;

        public HistoryStore History { get; private set; } = null!;

        public ClearrEngine Engine { get; private set; } = null!;

        public ClearrEngine DirectEngine { get; private set; } = null!;

        public FakeDeleter Deleter { get; } = new();

        public static async Task<Env> StartAsync(FakeWorld w, ClearrSettings s)
        {
            var e = new Env { World = w };
            var (app, url) = await FakeServers.StartAsync(w);
            e._app = app;
            e._tmp = Path.Combine(Path.GetTempPath(), "clearr-safety-" + Guid.NewGuid().ToString("N")[..8]);
            var paths = new ConfigPaths(e._tmp);
            e.Settings = new SettingsStore(paths);
            e.Keep = new KeepStore(paths);
            e.History = new HistoryStore(paths);
            s.Jellyfin = new() { Url = url + "/jf", ApiKey = "jf-key" };
            s.Sonarr = new() { Url = url + "/sonarr", ApiKey = "sonarr-key" };
            s.Radarr = new() { Url = url + "/radarr", ApiKey = "radarr-key" };
            s.AllowDirectDelete = true;
            e.Settings.Save(s);
            var f = new SimpleFactory();
            var source = new JellyfinApiSource(f, e.Settings.Get);
            e.Engine = new ClearrEngine(f, e.Settings.Get, source, e.Keep, e.History, NullLogger.Instance);
            e.DirectEngine = new ClearrEngine(f, e.Settings.Get, source, e.Keep, e.History, NullLogger.Instance, e.Deleter);
            return e;
        }

        public void Mode(CleanupMode m)
        {
            var s = Settings.Get();
            s.SetModes(m);
            Settings.Save(s);
        }

        public async Task StopAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            try
            {
                Directory.Delete(_tmp, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Builds fake libraries with matching Jellyfin, Sonarr and Radarr state.</summary>
    internal sealed class WorldBuilder
    {
        private readonly DateTime _now = DateTime.UtcNow;
        private int _sonarrId;
        private int _radarrId;
        private int _fileId;
        private int _tvdb = 1000;

        public WorldBuilder(int seed) => Seed = seed;

        public int Seed { get; }

        public FakeWorld World { get; } = new();

        public record ShowRef(string Id, string Name, string Status, int? SonarrId, List<(string Id, int Number)> Seasons, List<FakeWorld.Ep> Episodes, string[] JellyfinTags, string[] SonarrTags, string? Root);

        public record MovieRef(string Id, string Name, int? RadarrId, string[] JellyfinTags, string? Root);

        private static string NewId() => Guid.NewGuid().ToString("N");

        public string User(string id, string name)
        {
            World.Users.Add((id, name));
            return id;
        }

        public ShowRef Show(string name, string status, int[] eps, bool inSonarr, int gbPerFile, bool extraFile = false, string[]? sonarrTags = null, string[]? jellyfinTags = null, string root = "/tv/")
        {
            var show = new FakeWorld.Show(NewId(), name, status, ++_tvdb, new(), (jellyfinTags ?? Array.Empty<string>()).ToList());
            var episodes = new List<FakeWorld.Ep>();
            for (var s = 1; s <= eps.Length; s++)
            {
                var seasonId = NewId();
                show.Seasons.Add((seasonId, s));
                for (var e = 1; e <= eps[s - 1]; e++)
                {
                    var ep = new FakeWorld.Ep(NewId(), show.Id, seasonId, s, e);
                    World.Episodes.Add(ep);
                    episodes.Add(ep);
                }
            }

            World.Shows.Add(show);
            int? sid = null;
            if (inSonarr)
            {
                sid = ++_sonarrId;
                var tagIds = (sonarrTags ?? Array.Empty<string>()).Select(t =>
                {
                    var existing = World.SonarrTags.FirstOrDefault(x => x.Value == t).Key;
                    if (existing == 0)
                    {
                        existing = World.SonarrTags.Count + 1;
                        World.SonarrTags[existing] = t;
                    }

                    return (JsonNode)existing;
                }).ToArray();
                World.SonarrSeries[sid.Value] = new JsonObject
                {
                    ["id"] = sid,
                    ["title"] = name,
                    ["tvdbId"] = show.Tvdb,
                    ["rootFolderPath"] = root,
                    ["path"] = root.TrimEnd('/') + "/" + name,
                    ["monitored"] = true,
                    ["tags"] = new JsonArray(tagIds),
                    ["seasons"] = new JsonArray(Enumerable.Range(0, eps.Length + 1).Select(n => (JsonNode)new JsonObject { ["seasonNumber"] = n, ["monitored"] = n > 0 }).ToArray()),
                };
                for (var s = 1; s <= eps.Length; s++)
                {
                    var count = eps[s - 1] + (extraFile && s == eps.Length ? 1 : 0);
                    for (var e = 0; e < count; e++)
                    {
                        World.EpisodeFiles.Add((++_fileId, sid.Value, s, gbPerFile * GB));
                    }
                }
            }

            return new ShowRef(show.Id, name, status, sid, show.Seasons, episodes, jellyfinTags ?? Array.Empty<string>(), inSonarr ? sonarrTags ?? Array.Empty<string>() : Array.Empty<string>(), inSonarr ? root : null);
        }

        public MovieRef Movie(string name, bool inRadarr, int gb, string[]? jellyfinTags = null, string root = "/movies")
        {
            var m = new FakeWorld.Movie(NewId(), name, 2020, ++_tvdb, (jellyfinTags ?? Array.Empty<string>()).ToList());
            World.Movies.Add(m);
            int? rid = null;
            if (inRadarr)
            {
                rid = ++_radarrId;
                World.RadarrMovies[rid.Value] = new JsonObject
                {
                    ["id"] = rid,
                    ["title"] = name,
                    ["tmdbId"] = m.Tmdb,
                    ["rootFolderPath"] = root,
                    ["monitored"] = true,
                    ["hasFile"] = true,
                    ["tags"] = new JsonArray(),
                    ["movieFile"] = new JsonObject { ["id"] = 5000 + rid, ["size"] = gb * GB },
                };
            }

            return new MovieRef(m.Id, name, rid, jellyfinTags ?? Array.Empty<string>(), inRadarr ? root : null);
        }

        public void Watch(string user, IEnumerable<FakeWorld.Ep> eps, int daysAgo)
        {
            foreach (var e in eps)
            {
                World.UserData[(user, e.Id)] = (true, 0, _now.AddDays(-daysAgo).AddMinutes(-60 + e.Number));
            }
        }

        public void WatchAll(string user, ShowRef show, int daysAgo) => Watch(user, show.Episodes, daysAgo);

        public void Partial(string user, FakeWorld.Ep ep, int daysAgo) => World.UserData[(user, ep.Id)] = (false, 6_000_000_000, _now.AddDays(-daysAgo));

        public void WatchMovie(string user, MovieRef m, bool played, int daysAgo) =>
            World.UserData[(user, m.Id)] = (played, played ? 0 : 20_000_000_000, _now.AddDays(-daysAgo));
    }
}
