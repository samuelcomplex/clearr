// Performance check on a large library, so slowdowns show up in CI.
using System.Diagnostics;
using Clearr.Core;
using Clearr.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clearr.Tests;

internal static class PerfTests
{
    public static async Task<int> RunAsync()
    {
        var b = new SafetyTests.WorldBuilder(7);
        var rnd = new Random(7);
        var users = Enumerable.Range(1, 5).Select(i => b.User("u" + i, "User" + i)).ToList();
        var shows = Enumerable.Range(0, 300).Select(i => b.Show("Show " + i, i % 3 == 0 ? "Continuing" : "Ended", Enumerable.Repeat(10, 5).ToArray(), inSonarr: true, gbPerFile: 1)).ToList();
        var movies = Enumerable.Range(0, 2000).Select(i => b.Movie("Movie " + i, inRadarr: true, gb: 4)).ToList();
        foreach (var u in users)
        {
            foreach (var s in shows.Where(_ => rnd.Next(3) == 0))
            {
                b.WatchAll(u, s, rnd.Next(10, 90));
            }

            foreach (var m in movies.Where(_ => rnd.Next(4) == 0))
            {
                b.WatchMovie(u, m, played: true, daysAgo: rnd.Next(10, 90));
            }
        }

        var (app, url) = await FakeServers.StartAsync(b.World);
        var tmp = Path.Combine(Path.GetTempPath(), "clearr-perf-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new ConfigPaths(tmp);
        var settings = new SettingsStore(paths);
        settings.Save(new ClearrSettings
        {
            Jellyfin = new() { Url = url + "/jf", ApiKey = "jf-key" },
            Sonarr = new() { Url = url + "/sonarr", ApiKey = "sonarr-key" },
            Radarr = new() { Url = url + "/radarr", ApiKey = "radarr-key" },
            ShowsMode = CleanupMode.DryRun,
            MoviesMode = CleanupMode.DryRun,
            MaxDeleteGbPerRun = 0,
        });
        var f = new SimpleFactory();
        using var engine = new ClearrEngine(f, settings.Get, new JellyfinApiSource(f, settings.Get), new KeepStore(paths), new HistoryStore(paths), NullLogger.Instance);

        await engine.BuildAsync(default); // warm-up (JIT, connections)
        b.World.LatencyMs = 30;
        b.World.Requests = 0;
        var sw = Stopwatch.StartNew();
        var built = await engine.BuildAsync(default);
        var report = sw.Elapsed;
        sw.Restart();
        var preview = await engine.CleanupAsync(true, false, default);
        var cleanup = sw.Elapsed;
        sw.Restart();
        var requests = b.World.Requests;
        await engine.SetKeepAsync(shows[0].Id, "show", true, default);
        var keep = sw.Elapsed;

        await app.StopAsync();
        await app.DisposeAsync();
        try
        {
            Directory.Delete(tmp, true);
        }
        catch (IOException)
        {
        }

        Console.WriteLine($"  perf: {b.World.Episodes.Count} episodes, {movies.Count} movies, {users.Count} users, {b.World.UserData.Count} play records");
        Console.WriteLine($"  perf (30 ms per request): report {report.TotalMilliseconds:0} ms, preview {cleanup.TotalMilliseconds:0} ms ({preview.Actions.Count} items, {requests} requests for both), keep {keep.TotalMilliseconds:0} ms");
        var ok = built.Report.Shows.Count == 300 && report < TimeSpan.FromSeconds(5) && cleanup < TimeSpan.FromSeconds(10) && keep < TimeSpan.FromSeconds(2);
        Console.WriteLine((ok ? "PASS " : "FAIL ") + "[perf] Large library: report, preview and keep stay fast");
        return ok ? 0 : 1;
    }
}
