// End-to-end check against REAL Jellyfin, Sonarr and Radarr containers sharing a media folder.
// Started by .github/workflows/compatibility.yml. Env: JELLYFIN_URL, SONARR_URL, SONARR_API_KEY, RADARR_URL, RADARR_API_KEY, MEDIA_DIR.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Clearr.Core;
using Clearr.Storage;
using Microsoft.Extensions.Logging.Abstractions;

var jfUrl = Env("JELLYFIN_URL");
var sonarrUrl = Env("SONARR_URL");
var sonarrKey = Env("SONARR_API_KEY");
var radarrUrl = Env("RADARR_URL");
var radarrKey = Env("RADARR_API_KEY");
var media = Env("MEDIA_DIR");
const string ShowFolder = "Breaking Bad (2008) [tmdbid-1396]";
const string MovieFolder = "The Matrix (1999) [tmdbid-603]";

var fails = 0;
void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
static string Env(string n) => Environment.GetEnvironmentVariable(n) ?? throw new Exception(n + " not set");
static async Task<T> Poll<T>(string what, Func<Task<T?>> f, int seconds = 300) where T : class
{
    var until = DateTime.UtcNow.AddSeconds(seconds);
    Exception? last = null;
    while (DateTime.UtcNow < until)
    {
        try { var r = await f(); if (r != null) return r; } catch (Exception ex) { last = ex; }
        await Task.Delay(3000);
    }
    throw new TimeoutException($"Timed out waiting for {what}" + (last != null ? $" ({last.Message})" : ""));
}

// ------------------------------------------------------------------ Jellyfin first-run setup
var jf = new HttpClient { BaseAddress = new Uri(jfUrl.TrimEnd('/') + "/") };
const string Client = "MediaBrowser Client=\"Clearr CI\", Device=\"CI\", DeviceId=\"clearr-ci\", Version=\"1.0\"";
jf.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", Client);

var info = await Poll("Jellyfin to start", async () => { var r = await jf.GetAsync("System/Info/Public"); return r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<JsonObject>() : null; });
Console.WriteLine("Jellyfin " + info["Version"]);
(await jf.PostAsJsonAsync("Startup/Configuration", new { UICulture = "en-US", MetadataCountryCode = "US", PreferredMetadataLanguage = "en" })).EnsureSuccessStatusCode();
await jf.GetAsync("Startup/User");
(await jf.PostAsJsonAsync("Startup/User", new { Name = "anna", Password = "ci-pass-1" })).EnsureSuccessStatusCode();
(await jf.PostAsync("Startup/Complete", null)).EnsureSuccessStatusCode();

var auth = await Poll("Jellyfin login", async () =>
{
    var r = await jf.PostAsJsonAsync("Users/AuthenticateByName", new { Username = "anna", Pw = "ci-pass-1" });
    return r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<JsonObject>() : null;
}, 60);
var token = auth["AccessToken"]!.GetValue<string>();
var annaId = auth["User"]!["Id"]!.GetValue<string>();
jf.DefaultRequestHeaders.Remove("Authorization");
jf.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", Client + $", Token=\"{token}\"");

var bjorn = await (await jf.PostAsJsonAsync("Users/New", new { Name = "bjorn", Password = "ci-pass-2" })).Content.ReadFromJsonAsync<JsonObject>();
var bjornId = bjorn!["Id"]!.GetValue<string>();
(await jf.PostAsync("Auth/Keys?app=clearr", null)).EnsureSuccessStatusCode();
var keys = await jf.GetFromJsonAsync<JsonObject>("Auth/Keys");
var jfKey = keys!["Items"]!.AsArray().Last()!["AccessToken"]!.GetValue<string>();

(await jf.PostAsync("Library/VirtualFolders?name=TV&collectionType=tvshows&paths=/media/tv&refreshLibrary=false", null)).EnsureSuccessStatusCode();
(await jf.PostAsync("Library/VirtualFolders?name=Movies&collectionType=movies&paths=/media/movies&refreshLibrary=false", null)).EnsureSuccessStatusCode();
(await jf.PostAsync("Library/Refresh", null)).EnsureSuccessStatusCode();

var episodes = await Poll("Jellyfin to scan 3 episodes and 1 movie", async () =>
{
    var e = await jf.GetFromJsonAsync<JsonObject>("Items?Recursive=true&IncludeItemTypes=Episode&Fields=ProviderIds");
    var m = await jf.GetFromJsonAsync<JsonObject>("Items?Recursive=true&IncludeItemTypes=Movie");
    return e!["Items"]!.AsArray().Count >= 3 && m!["Items"]!.AsArray().Count >= 1 ? e["Items"]!.AsArray() : null;
});
var movieItem = (await jf.GetFromJsonAsync<JsonObject>("Items?Recursive=true&IncludeItemTypes=Movie"))!["Items"]![0]!;
var s1 = episodes.Where(e => e!["ParentIndexNumber"]?.GetValue<int>() == 1).Select(e => e!["Id"]!.GetValue<string>()).ToList();
Expect(s1.Count == 2, "Jellyfin scanned season 1 (2 episodes) and season 2");

foreach (var user in new[] { annaId, bjornId })
{
    foreach (var id in s1)
    {
        (await jf.PostAsync($"UserPlayedItems/{id}?userId={user}", null)).EnsureSuccessStatusCode();
    }
}

(await jf.PostAsync($"UserPlayedItems/{movieItem["Id"]}?userId={annaId}", null)).EnsureSuccessStatusCode();

// ------------------------------------------------------------------ Sonarr / Radarr: add + import existing files
HttpClient Arr(string url, string key)
{
    var c = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") };
    c.DefaultRequestHeaders.Add("X-Api-Key", key);
    return c;
}

var sonarr = Arr(sonarrUrl, sonarrKey);
var radarr = Arr(radarrUrl, radarrKey);
Console.WriteLine("Sonarr " + (await sonarr.GetFromJsonAsync<JsonObject>("api/v3/system/status"))!["version"] + ", Radarr " + (await radarr.GetFromJsonAsync<JsonObject>("api/v3/system/status"))!["version"]);

await sonarr.PostAsJsonAsync("api/v3/rootfolder", new { path = "/tv" });
var sProfile = (await sonarr.GetFromJsonAsync<JsonArray>("api/v3/qualityprofile"))![0]!["id"]!.GetValue<int>();
var bb = (JsonObject)(await sonarr.GetFromJsonAsync<JsonArray>("api/v3/series/lookup?term=tvdb:81189"))![0]!.DeepClone();
bb["qualityProfileId"] = sProfile;
bb["path"] = "/tv/" + ShowFolder;
bb["monitored"] = true;
bb["seasonFolder"] = true;
bb["addOptions"] = new JsonObject { ["monitor"] = "all", ["searchForMissingEpisodes"] = false };
var added = await sonarr.PostAsync("api/v3/series", new StringContent(bb.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
if (!added.IsSuccessStatusCode) Console.WriteLine(await added.Content.ReadAsStringAsync());
added.EnsureSuccessStatusCode();
var seriesId = (await added.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<int>();
// Import the existing files explicitly (like Activity → Manual Import in the UI): episode + quality per file.
var sQualities = await sonarr.GetFromJsonAsync<JsonArray>("api/v3/qualitydefinition");
var hdtv720 = sQualities!.First(q => q!["quality"]!["name"]!.GetValue<string>() == "HDTV-720p")!["quality"]!.DeepClone();
var allEpisodes = await Poll("Sonarr to load the episode list", async () =>
{
    var eps = await sonarr.GetFromJsonAsync<JsonArray>($"api/v3/episode?seriesId={seriesId}");
    return eps!.Count > 10 ? eps : null;
});
var showDir = Path.Combine(media, "tv", ShowFolder);
var importFiles = new JsonArray();
foreach (var file in Directory.GetFiles(showDir, "*.mkv", SearchOption.AllDirectories))
{
    var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(file), @"S(\d+)E(\d+)");
    var (sn, en) = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    var ep = allEpisodes.First(e => e!["seasonNumber"]!.GetValue<int>() == sn && e["episodeNumber"]!.GetValue<int>() == en)!;
    importFiles.Add(new JsonObject
    {
        ["path"] = "/tv/" + ShowFolder + "/" + Path.GetRelativePath(showDir, file).Replace('\\', '/'),
        ["seriesId"] = seriesId,
        ["episodeIds"] = new JsonArray(ep["id"]!.GetValue<int>()),
        ["quality"] = new JsonObject { ["quality"] = hdtv720.DeepClone(), ["revision"] = new JsonObject { ["version"] = 1, ["real"] = 0, ["isRepack"] = false } },
        ["languages"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "English" }),
        ["releaseGroup"] = "",
        ["indexerFlags"] = 0,
        ["releaseType"] = "singleEpisode",
    });
}

(await sonarr.PostAsync("api/v3/command", new StringContent(new JsonObject { ["name"] = "ManualImport", ["importMode"] = "auto", ["files"] = importFiles }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
await Poll("Sonarr to import the 3 episode files", async () =>
{
    var files = await sonarr.GetFromJsonAsync<JsonArray>($"api/v3/episodefile?seriesId={seriesId}");
    return files!.Count >= 3 ? files : null;
}, 120);
Expect(Directory.GetFiles(showDir, "*.mkv", SearchOption.AllDirectories).Length == 3, "Sonarr imported the files in place");

await radarr.PostAsJsonAsync("api/v3/rootfolder", new { path = "/movies" });
var rProfile = (await radarr.GetFromJsonAsync<JsonArray>("api/v3/qualityprofile"))![0]!["id"]!.GetValue<int>();
var matrix = (JsonObject)(await radarr.GetFromJsonAsync<JsonNode>("api/v3/movie/lookup/tmdb?tmdbId=603"))!.DeepClone();
matrix["qualityProfileId"] = rProfile;
matrix["path"] = "/movies/" + MovieFolder;
matrix["monitored"] = true;
matrix["minimumAvailability"] = "released";
matrix["addOptions"] = new JsonObject { ["searchForMovie"] = false };
var addedMovie = await radarr.PostAsync("api/v3/movie", new StringContent(matrix.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
if (!addedMovie.IsSuccessStatusCode) Console.WriteLine(await addedMovie.Content.ReadAsStringAsync());
addedMovie.EnsureSuccessStatusCode();
var movieId = (await addedMovie.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<int>();
var rQualities = await radarr.GetFromJsonAsync<JsonArray>("api/v3/qualitydefinition");
var bluray1080 = rQualities!.First(q => q!["quality"]!["name"]!.GetValue<string>() == "Bluray-1080p")!["quality"]!.DeepClone();
var movieDir = Path.Combine(media, "movies", MovieFolder);
var movieFile = Directory.GetFiles(movieDir, "*.mkv").Single();
var rFiles = new JsonArray(new JsonObject
{
    ["path"] = "/movies/" + MovieFolder + "/" + Path.GetFileName(movieFile),
    ["movieId"] = movieId,
    ["quality"] = new JsonObject { ["quality"] = bluray1080, ["revision"] = new JsonObject { ["version"] = 1, ["real"] = 0, ["isRepack"] = false } },
    ["languages"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "English" }),
    ["releaseGroup"] = "",
    ["indexerFlags"] = 0,
});
(await radarr.PostAsync("api/v3/command", new StringContent(new JsonObject { ["name"] = "ManualImport", ["importMode"] = "auto", ["files"] = rFiles }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
await Poll("Radarr to import the movie file", async () =>
{
    var m = await radarr.GetFromJsonAsync<JsonObject>($"api/v3/movie/{movieId}");
    return m!["hasFile"]!.GetValue<bool>() ? m : null;
}, 120);

// ------------------------------------------------------------------ Clearr
var dir = Path.Combine(Path.GetTempPath(), "clearr-it");
var paths = new ConfigPaths(dir);
var settings = new SettingsStore(paths);
settings.Save(new ClearrSettings
{
    Jellyfin = new() { Url = jfUrl, ApiKey = jfKey },
    Sonarr = new() { Url = sonarrUrl, ApiKey = sonarrKey },
    Radarr = new() { Url = radarrUrl, ApiKey = radarrKey },
    GraceDays = 0,
    MaxDeleteGbPerRun = 0,
});
var svc = new ClearrEngine(new Factory(), settings.Get, new JellyfinApiSource(new Factory(), settings.Get), new KeepStore(paths), new HistoryStore(paths), NullLogger.Instance);

var (_, report, _, _) = await svc.BuildAsync(default);
foreach (var w in report.Warnings) Console.WriteLine("  warning: " + w);
var show = report.Shows.SingleOrDefault(s => s.Name.Contains("Breaking Bad"));
var movie = report.Movies.SingleOrDefault(m => m.Name.Contains("Matrix"));
Expect(report.TrackedUsers.Count == 2, "Clearr reads both Jellyfin users");
Expect(show != null && show.Managed, "Show found in Jellyfin and matched to Sonarr");
Expect(movie != null && movie.Managed, "Movie found in Jellyfin and matched to Radarr");
Expect(show?.Seasons.FirstOrDefault(x => x.Number == 1)?.Eligible == true, "Season 1 finished by everyone who started → eligible: " + show?.Seasons.FirstOrDefault(x => x.Number == 1)?.Reason);
Expect(show?.Seasons.FirstOrDefault(x => x.Number == 2)?.Eligible == false, "Season 2 nobody started → kept");
Expect(movie?.Eligible == true, "Movie finished → eligible: " + movie?.Reason);

var dry = await svc.CleanupAsync(true, false, default);
foreach (var a in dry.Actions) Console.WriteLine($"  dry: {a.Action} {a.Title} {a.SeasonName} {a.Bytes} :: {a.Detail}");
Expect(dry.Actions.Count(a => a.Action == "WouldDelete") == 2 && dry.TotalBytes > 0, "Dry run lists season 1 and the movie with real sizes");

var s1Files = Directory.GetFiles(Path.Combine(media, "tv", ShowFolder, "Season 01"));
var s2Files = Directory.GetFiles(Path.Combine(media, "tv", ShowFolder, "Season 02"));
Expect(s1Files.Length == 2 && s2Files.Length == 1, "Dry run left files on disk");

var st = settings.Get();
st.SetModes(CleanupMode.Delete);
settings.Save(st);
var real = await svc.CleanupAsync(false, true, default);
foreach (var a in real.Actions) Console.WriteLine($"  real: {a.Action} {a.Title} {a.SeasonName} :: {a.Detail}");

Expect(!Directory.Exists(Path.Combine(media, "tv", ShowFolder, "Season 01")) || Directory.GetFiles(Path.Combine(media, "tv", ShowFolder, "Season 01"), "*.mkv").Length == 0, "Season 1 files deleted from disk by Sonarr");
Expect(Directory.GetFiles(Path.Combine(media, "tv", ShowFolder, "Season 02"), "*.mkv").Length == 1, "Season 2 file still on disk");
Expect(Directory.GetFiles(Path.Combine(media, "movies", MovieFolder), "*.mkv").Length == 0, "Movie file deleted from disk by Radarr");

var series = await sonarr.GetFromJsonAsync<JsonObject>($"api/v3/series/{seriesId}");
bool Mon(int n) => series!["seasons"]!.AsArray().First(x => x!["seasonNumber"]!.GetValue<int>() == n)!["monitored"]!.GetValue<bool>();
Expect(!Mon(1) && Mon(2) && series!["monitored"]!.GetValue<bool>(), "Sonarr: season 1 unmonitored, season 2 and the show still monitored");
var rm = await radarr.GetFromJsonAsync<JsonObject>($"api/v3/movie/{movieId}");
Expect(!rm!["monitored"]!.GetValue<bool>(), "Radarr: movie unmonitored");

Console.WriteLine(fails == 0 ? "\nALL PASSED" : $"\n{fails} FAILED");
return fails == 0 ? 0 : 1;

internal class Factory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
