// Runs Clearr against a made-up household (fake Jellyfin/Sonarr/Radarr) for screenshots and demos.
// dotnet run --project tools/Demo   →  http://127.0.0.1:6567
using System.Text.Json.Nodes;
using Clearr.Core;
using Clearr.Storage;
using Clearr.Tests;

const long GB = 1024L * 1024 * 1024;
var now = DateTime.UtcNow;
var w = new FakeWorld();
var rnd = new Random(7);
string Id() => Guid.NewGuid().ToString("N");
w.Users.AddRange(new[] { ("alex", "Alex"), ("sam", "Sam"), ("robin", "Robin"), ("kim", "Kim") });
int fileId = 1, sonarrId = 1, radarrId = 1;

FakeWorld.Show Show(string name, string status, int[] eps, bool managed = true, string[]? tags = null)
{
    var s = new FakeWorld.Show(Id(), name, status, 1000 + sonarrId, new(), new());
    for (var n = 1; n <= eps.Length; n++)
    {
        var sid = Id();
        s.Seasons.Add((sid, n));
        for (var e = 1; e <= eps[n - 1]; e++) w.Episodes.Add(new FakeWorld.Ep(Id(), s.Id, sid, n, e));
    }
    w.Shows.Add(s);
    if (managed)
    {
        var id = sonarrId;
        w.SonarrSeries[id] = new JsonObject { ["id"] = id, ["title"] = name, ["tvdbId"] = s.Tvdb, ["monitored"] = true, ["tags"] = new JsonArray(),
            ["seasons"] = new JsonArray(Enumerable.Range(0, eps.Length + 1).Select(n => (JsonNode)new JsonObject { ["seasonNumber"] = n, ["monitored"] = n > 0 }).ToArray()) };
        for (var n = 1; n <= eps.Length; n++) for (var e = 0; e < eps[n - 1]; e++) w.EpisodeFiles.Add((fileId++, id, n, (long)(rnd.Next(700, 1900) * 1024L * 1024)));
    }
    sonarrId++;
    return s;
}
List<FakeWorld.Ep> E(FakeWorld.Show s, int? season = null, int take = 999) => w.Episodes.Where(e => e.SeriesId == s.Id && (season == null || e.Season == season)).Take(take).ToList();
void Watch(string u, IEnumerable<FakeWorld.Ep> eps, int daysAgo) { foreach (var e in eps) w.UserData[(u, e.Id)] = (true, 0, now.AddDays(-daysAgo)); }
void Partial(string u, FakeWorld.Ep e, int daysAgo) => w.UserData[(u, e.Id)] = (false, 12_000_000_000, now.AddDays(-daysAgo));

var harbor = Show("Harbor Lights", "Ended", new[] { 10, 10, 10 }); Watch("alex", E(harbor), 40); Watch("sam", E(harbor), 35);
var iron = Show("Iron Coast", "Ended", new[] { 10, 10, 10 }); Watch("alex", E(iron), 45); Watch("kim", E(iron, 1), 80); Watch("kim", E(iron, 2, 4), 70);
var garden = Show("Midnight Garden", "Ended", new[] { 12 }); Watch("sam", E(garden, 1, 7), 2); Watch("robin", E(garden, 1, 2), 60); Partial("robin", E(garden)[2], 58);
var paper = Show("Paper Moons", "Ended", new[] { 8, 8 }); Watch("alex", E(paper), 50); Watch("robin", E(paper), 48);
var quiet = Show("Quiet Streets", "Ended", new[] { 8 });
var star = Show("Starfall Academy", "Continuing", new[] { 12, 12, 12 }); Watch("alex", E(star, 1), 60); Watch("kim", E(star, 1), 55); Watch("alex", E(star, 2), 30); Watch("kim", E(star, 2), 25); Watch("alex", E(star, 3, 6), 1); Watch("kim", E(star, 3, 2), 3);
var road = Show("The Long Road", "Continuing", new[] { 10, 10 }); Watch("sam", E(road), 20); Watch("robin", E(road), 18);
var local = Show("Home Videos", "Ended", new[] { 6 }, managed: false); Watch("alex", E(local), 90);

void Movie(string name, int year, double gb, bool managed = true)
{
    var m = new FakeWorld.Movie(Id(), name, year, 5000 + radarrId, new());
    w.Movies.Add(m);
    if (managed) w.RadarrMovies[radarrId] = new JsonObject { ["id"] = radarrId, ["title"] = name, ["tmdbId"] = m.Tmdb, ["monitored"] = true, ["hasFile"] = true, ["tags"] = new JsonArray(), ["movieFile"] = new JsonObject { ["id"] = 9000 + radarrId, ["size"] = (long)(gb * GB) } };
    radarrId++;
}
Movie("Northern Signal", 2021, 9.4); Movie("The Glass Orchard", 2019, 6.2); Movie("Low Tide", 2023, 12.8); Movie("Ember & Ash", 2022, 7.1); Movie("Weekend Trip", 2018, 3.3, managed: false);
w.UserData[("alex", w.Movies[0].Id)] = (true, 0, now.AddDays(-20)); w.UserData[("sam", w.Movies[0].Id)] = (true, 0, now.AddDays(-19));
w.UserData[("robin", w.Movies[1].Id)] = (false, 25_000_000_000, now.AddDays(-45));
w.UserData[("kim", w.Movies[2].Id)] = (false, 9_000_000_000, now.AddDays(-1));
w.UserData[("alex", w.Movies[3].Id)] = (true, 0, now.AddDays(-3));

var (fake, url) = await FakeServers.StartAsync(w);
var dir = Path.Combine(Path.GetTempPath(), "clearr-demo");
if (Directory.Exists(dir)) Directory.Delete(dir, true);
var paths = new ConfigPaths(dir);
new SettingsStore(paths).Save(new ClearrSettings
{
    Jellyfin = new() { Url = url + "/jf", ApiKey = "jf-key" },
    Sonarr = new() { Url = url + "/sonarr", ApiKey = "sonarr-key" },
    Radarr = new() { Url = url + "/radarr", ApiKey = "radarr-key" },
});
new KeepStore(paths).Set(paper.Id, true);
new KeepStore(paths).Set(star.Seasons[1].Id, true);
new HistoryStore(paths).Add(new[]
{
    new HistoryEntry { Date = now.AddDays(-1).Date.AddHours(4), Action = "Deleted", Kind = "Season", Title = "Northbound", SeasonName = "Season 2", Bytes = 11 * GB, Detail = "Finished by Alex, Sam · 10 files deleted in Sonarr; season unmonitored" },
    new HistoryEntry { Date = now.AddDays(-1).Date.AddHours(4), Action = "Deleted", Kind = "Movie", Title = "Copper Sky (2020)", Bytes = 8 * GB, Detail = "Finished by Robin · unmonitored and file deleted in Radarr" },
    new HistoryEntry { Date = now.AddDays(-2).Date.AddHours(4), Action = "Deleted", Kind = "Show", Title = "Glass Harbor", Bytes = 25 * GB, Detail = "Finished by Robin · 24 files deleted in Sonarr; existing seasons unmonitored, show still monitored for new seasons" },
    new HistoryEntry { Date = now.AddDays(-3).Date.AddHours(4), Action = "Failed", Kind = "Season", Title = "Cold Front", SeasonName = "Season 1", Bytes = 6 * GB, Detail = "Sonarr: can't connect (Connection refused)" },
});

Environment.SetEnvironmentVariable("CLEARR_CONFIG_DIR", dir);
Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:6567");
Environment.SetEnvironmentVariable("Logging__LogLevel__Default", "Warning");
var root = AppContext.BaseDirectory;
while (root != null && !Directory.Exists(Path.Combine(root, "src", "Clearr", "wwwroot"))) root = Path.GetDirectoryName(root.TrimEnd('/'));
Environment.SetEnvironmentVariable("ASPNETCORE_WEBROOT", Path.Combine(root!, "src", "Clearr", "wwwroot"));
Console.WriteLine("Demo running on http://127.0.0.1:6567");
typeof(Clearr.Api.AuthService).Assembly.EntryPoint!.Invoke(null, new object[] { Array.Empty<string>() });
