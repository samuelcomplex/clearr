// Clearr end-to-end tests against in-process fake Jellyfin/Sonarr/Radarr servers.
// Run: dotnet run --project tests/Clearr.Tests
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Clearr.Core;
using Clearr.Storage;
using Clearr.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Clearr.Clients;

var fails = 0;
void Expect(bool ok, string what)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
    if (!ok)
    {
        fails++;
    }
}

const long GB = 1024L * 1024 * 1024;
var now = DateTime.UtcNow;
var w = new FakeWorld();
string Id() => Guid.NewGuid().ToString("N");

w.Users.AddRange(new[] { ("u-anna", "Anna"), ("u-bjorn", "Bjorn"), ("u-cleo", "Cleo") });
var fileId = 1;

FakeWorld.Show AddShow(string name, string status, int tvdb, int[] eps, int? sonarrId, long sizePerFile, int extraFilesInLastSeason = 0, string[]? sonarrTags = null)
{
    var show = new FakeWorld.Show(Id(), name, status, tvdb, new(), new());
    for (var s = 1; s <= eps.Length; s++)
    {
        var seasonId = Id();
        show.Seasons.Add((seasonId, s));
        for (var e = 1; e <= eps[s - 1]; e++)
        {
            w.Episodes.Add(new FakeWorld.Ep(Id(), show.Id, seasonId, s, e));
        }
    }

    w.Shows.Add(show);
    if (sonarrId is int sid)
    {
        var tagIds = (sonarrTags ?? Array.Empty<string>()).Select(t =>
        {
            var existing = w.SonarrTags.FirstOrDefault(x => x.Value == t).Key;
            if (existing == 0)
            {
                existing = w.SonarrTags.Count + 1;
                w.SonarrTags[existing] = t;
            }

            return (JsonNode)existing;
        }).ToArray();
        w.SonarrSeries[sid] = new JsonObject
        {
            ["id"] = sid, ["title"] = name, ["tvdbId"] = tvdb, ["monitored"] = true, ["tags"] = new JsonArray(tagIds),
            ["seasons"] = new JsonArray(Enumerable.Range(0, eps.Length + 1).Select(n => (JsonNode)new JsonObject { ["seasonNumber"] = n, ["monitored"] = n > 0 }).ToArray()),
        };
        for (var s = 1; s <= eps.Length; s++)
        {
            var count = eps[s - 1] + (s == eps.Length ? extraFilesInLastSeason : 0);
            for (var e = 0; e < count; e++)
            {
                w.EpisodeFiles.Add((fileId++, sid, s, sizePerFile));
            }
        }
    }

    return show;
}

void Watch(string user, IEnumerable<FakeWorld.Ep> eps, DateTime when) { foreach (var e in eps) { w.UserData[(user, e.Id)] = (true, 0, when); } }
List<FakeWorld.Ep> Eps(FakeWorld.Show s, int? season = null) => w.Episodes.Where(e => e.SeriesId == s.Id && (season == null || e.Season == season)).ToList();

var done = AddShow("Done Show", "Ended", 100, new[] { 2, 2 }, 1, 5 * GB);
Watch("u-anna", Eps(done), now.AddDays(-40));
Watch("u-bjorn", Eps(done), now.AddDays(-35));

var running = AddShow("Running Show", "Continuing", 200, new[] { 3, 2 }, 2, 2 * GB);
Watch("u-anna", Eps(running), now.AddDays(-30));

var ended = AddShow("Ended Show", "Ended", 300, new[] { 2, 2 }, 3, 1 * GB);
Watch("u-anna", Eps(ended), now.AddDays(-20));
Watch("u-bjorn", Eps(ended, 1), now.AddDays(-10));
w.UserData[("u-bjorn", Eps(ended, 2)[0].Id)] = (false, 9_000_000_000, now.AddDays(-1));
w.UserData[("u-cleo", Eps(ended, 1)[0].Id)] = (true, 0, now.AddDays(-60));

var unmanaged = AddShow("Unmanaged Show", "Ended", 400, new[] { 2 }, null, 0);
Watch("u-anna", Eps(unmanaged), now.AddDays(-40));

var newEp = AddShow("New Episode Show", "Ended", 500, new[] { 2 }, 5, 1 * GB, extraFilesInLastSeason: 1);
Watch("u-anna", Eps(newEp), now.AddDays(-40));

var sonarrKept = AddShow("Sonarr Kept Show", "Ended", 600, new[] { 1 }, 6, 1 * GB, sonarrTags: new[] { "keep" });
Watch("u-anna", Eps(sonarrKept), now.AddDays(-40));

void AddMovie(string name, int tmdb, int? radarrId, long size, string[]? tags = null)
{
    var m = new FakeWorld.Movie(Id(), name, 2020, tmdb, (tags ?? Array.Empty<string>()).ToList());
    w.Movies.Add(m);
    if (radarrId is int rid)
    {
        w.RadarrMovies[rid] = new JsonObject
        {
            ["id"] = rid, ["title"] = name, ["tmdbId"] = tmdb, ["monitored"] = true, ["hasFile"] = true, ["tags"] = new JsonArray(),
            ["movieFile"] = new JsonObject { ["id"] = 1000 + rid, ["size"] = size },
        };
    }
}

AddMovie("Finished Movie", 10, 1, 8 * GB);
AddMovie("Half Movie", 20, 2, 4 * GB);
AddMovie("Not In Radarr", 30, null, 0);
AddMovie("Jellyfin Kept Movie", 40, 4, 3 * GB, new[] { "Keep" });
w.UserData[("u-anna", w.Movies[0].Id)] = (true, 0, now.AddDays(-20));
w.UserData[("u-bjorn", w.Movies[1].Id)] = (false, 30_000_000_000, now.AddDays(-50));
w.UserData[("u-anna", w.Movies[3].Id)] = (true, 0, now.AddDays(-20));

var (fake, url) = await FakeServers.StartAsync(w);
var tmp = Path.Combine(Path.GetTempPath(), "clearr-test-" + Guid.NewGuid().ToString("N")[..8]);
var paths = new ConfigPaths(tmp);
var settings = new SettingsStore(paths);
var keep = new KeepStore(paths);
var history = new HistoryStore(paths);
settings.Save(new ClearrSettings
{
    Jellyfin = new() { Url = url + "/jf", ApiKey = "jf-key" },
    Sonarr = new() { Url = url + "/sonarr", ApiKey = "sonarr-key" },
    Radarr = new() { Url = url + "/radarr", ApiKey = "radarr-key" },
    Mode = CleanupMode.DryRun,
});

var factory = new SimpleFactory();
var source = new JellyfinApiSource(factory, settings.Get);
var svc = new ClearrEngine(factory, settings.Get, source, keep, history, NullLogger.Instance);

// ---------------------------------------------------------------- report
var (_, report, _, _) = await svc.BuildAsync(default);
Console.WriteLine("  shows: " + string.Join(", ", report.Shows.Select(x => x.Name)) + " | movies: " + string.Join(", ", report.Movies.Select(x => x.Name)));
ShowReport S(string n) => report.Shows.Single(x => x.Name == n);
MovieReport M(string n) => report.Movies.Single(x => x.Name == n);

Expect(report.SonarrConnected && report.RadarrConnected && report.Warnings.Count == 0, "Connected to fake Sonarr and Radarr");
Expect(S("Done Show").Eligible && S("Done Show").Managed, "Done Show: whole show eligible");
Expect(S("Running Show").Seasons[0].Eligible && !S("Running Show").Seasons[1].Eligible && S("Running Show").Seasons[1].Reason.Contains("continuing"), "Running Show: S1 eligible, newest season protected");
Expect(!S("Running Show").Eligible && S("Running Show").Reason.Contains("Continuing"), "Running Show: whole-show blocked (Continuing)");
var cleo = S("Ended Show").Users.Single(u => u.UserName == "Cleo");
Expect(cleo.Flags.Contains("Sampled") && !S("Ended Show").Seasons[0].Eligible, "Ended Show: Cleo flagged Sampled, blocks S1");
var bjorn = S("Ended Show").Users.Single(u => u.UserName == "Bjorn");
Expect(bjorn.Status == "InProgress" && bjorn.Flags.Count == 0 && bjorn.LastEpisode == "S02E01", "Ended Show: Bjorn actively watching, last S02E01");
Expect(!S("Unmanaged Show").Eligible && !S("Unmanaged Show").Managed && S("Unmanaged Show").Reason.Contains("Not in Sonarr"), "Unmanaged Show: not eligible (not in Sonarr)");
Expect(S("Sonarr Kept Show").Kept && S("Sonarr Kept Show").KeptBy == "Sonarr tag" && !S("Sonarr Kept Show").Eligible, "Sonarr keep tag respected");
Expect(M("Finished Movie").Eligible, "Finished Movie eligible");
var half = M("Half Movie").Users.Single(u => u.UserName == "Bjorn");
Expect(!M("Half Movie").Eligible && half.Flags.Contains("Sampled") && half.Flags.Contains("MidEpisode"), "Half Movie: Bjorn stopped mid-movie, flagged, blocks");
Expect(!M("Not In Radarr").Managed && !M("Not In Radarr").Eligible == true || !M("Not In Radarr").Managed, "Not In Radarr: unmanaged");
Expect(M("Jellyfin Kept Movie").Kept && M("Jellyfin Kept Movie").KeptBy == "Jellyfin tag", "Jellyfin keep tag respected (case-insensitive)");

// ---------------------------------------------------------------- dry run + guard
var filesBefore = w.EpisodeFiles.Count;
var dry = await svc.CleanupAsync(true, false, default);
string Line(HistoryEntry a) => $"{a.Action} {a.Kind} {a.Title} {a.SeasonName} {a.Bytes / GB}GB :: {a.Detail}";
Console.WriteLine("  dry run:\n    " + string.Join("\n    ", dry.Actions.Select(Line)));
Expect(dry.Actions.Any(a => a.Action == "WouldDelete" && a.Title == "Done Show" && a.Kind == "Show" && a.Bytes == 20 * GB), "Dry run: Done Show would go (20 GB from Sonarr sizes)");
Expect(dry.Actions.Any(a => a.Action == "WouldDelete" && a.Title == "Running Show" && a.SeasonName == "Season 1" && a.Bytes == 6 * GB), "Dry run: Running Show S1 would go (6 GB)");
Expect(dry.Actions.Any(a => a.Action == "WouldDelete" && a.Kind == "Movie" && a.Title.StartsWith("Finished Movie") && a.Bytes == 8 * GB), "Dry run: Finished Movie would go (8 GB)");
Expect(dry.Actions.Any(a => a.Action == "Skipped" && a.Title == "New Episode Show" && a.Detail.Contains("waiting for Jellyfin")), "Safety: show with a file Jellyfin hasn't seen is skipped");
Expect(w.EpisodeFiles.Count == filesBefore && w.Log.Count == 0 && history.GetAll().Count == 0, "Dry run changed nothing and wasn't recorded");

var refused = await svc.CleanupAsync(false, true, default);
Expect(refused.Error != null && refused.Error.Contains("DryRun") && w.Log.Count == 0, "Real cleanup refused while mode is DryRun");

// ---------------------------------------------------------------- keep
var msg = await svc.SetKeepAsync(running.Id, "show", true, default);
Expect(msg.Contains("Sonarr") && w.SonarrSeries[2]["tags"]!.AsArray().Any(t => w.SonarrTags[t!.GetValue<int>()] == "keep"), "Keep show: stored and tagged 'keep' in Sonarr");
(_, report, _, _) = await svc.BuildAsync(default);
Expect(S("Running Show").Kept && !S("Running Show").Seasons[0].Eligible, "Kept show: its seasons are no longer eligible");
await svc.SetKeepAsync(running.Id, "show", false, default);
Expect(w.SonarrSeries[2]["tags"]!.AsArray().Count == 0, "Un-keep show: Sonarr tag removed");
await svc.SetKeepAsync(done.Seasons[1].Id, "season", true, default);
(_, report, _, _) = await svc.BuildAsync(default);
Expect(!S("Done Show").Eligible && S("Done Show").Seasons[0].Eligible && !S("Done Show").Seasons[1].Eligible, "Kept season: blocks whole-show removal, other season still eligible");
await svc.SetKeepAsync(done.Seasons[1].Id, "season", false, default);

// ---------------------------------------------------------------- delete with per-run limit
var st = settings.Get();
st.Mode = CleanupMode.Delete;
st.MaxDeleteGbPerRun = 12;
settings.Save(st);
var run1 = await svc.CleanupAsync(false, true, default);
Console.WriteLine("  run 1 (12 GB limit):\n    " + string.Join("\n    ", run1.Actions.Select(Line)));
Expect(run1.Actions.Single(a => a.Title == "Done Show").Action == "Deleted", "Run 1: Done Show deleted (first item always allowed)");
Expect(run1.Actions.Where(a => a.Title is "Running Show" || a.Title.StartsWith("Finished Movie")).All(a => a.Action == "Skipped" && a.Detail.Contains("limit")), "Run 1: the rest held back by the 12 GB limit");
Expect(!w.EpisodeFiles.Any(f => f.SeriesId == 1), "Run 1: all Done Show files deleted in Sonarr");
var s1 = w.SonarrSeries[1];
Expect(s1["monitored"]!.GetValue<bool>() && s1["seasons"]!.AsArray().All(x => !x!["monitored"]!.GetValue<bool>()), "Run 1: Done Show's seasons unmonitored, show itself still monitored");
Expect(w.JellyfinRefreshes == 1, "Run 1: Jellyfin library refresh requested");
Expect(w.EpisodeFiles.Any(f => f.SeriesId == 5) && w.EpisodeFiles.Any(f => f.SeriesId == 3), "Run 1: New Episode Show and Ended Show untouched");

st = settings.Get();
st.MaxDeleteGbPerRun = 0;
settings.Save(st);
var run2 = await svc.CleanupAsync(false, true, default);
Console.WriteLine("  run 2 (no limit):\n    " + string.Join("\n    ", run2.Actions.Select(Line)));
Expect(!w.EpisodeFiles.Any(f => f.SeriesId == 2 && f.Season == 1) && w.EpisodeFiles.Count(f => f.SeriesId == 2 && f.Season == 2) == 2, "Run 2: Running Show S1 files deleted, S2 kept");
var s2 = w.SonarrSeries[2]["seasons"]!.AsArray();
Expect(!s2.Single(x => x!["seasonNumber"]!.GetValue<int>() == 1)!["monitored"]!.GetValue<bool>() && s2.Single(x => x!["seasonNumber"]!.GetValue<int>() == 2)!["monitored"]!.GetValue<bool>(), "Run 2: Running Show S1 unmonitored, S2 still monitored");
Expect(!w.RadarrMovies[1]["monitored"]!.GetValue<bool>() && w.RadarrMovies[1]["movieFile"] == null, "Run 2: Finished Movie unmonitored and file deleted in Radarr");
Expect(w.RadarrMovies[2]["movieFile"] != null && w.RadarrMovies[4]["movieFile"] != null, "Run 2: Half Movie and kept movie untouched");
var log = string.Join(",", w.Log);
Expect(w.Log.IndexOf("PUT movie 1") < w.Log.IndexOf("DELETE moviefile 1001"), "Run 2: unmonitor happens before delete");
Expect(history.GetAll().Count(h => h.Action == "Deleted") == 3, "History: 3 deletions recorded");

// ---------------------------------------------------------------- plugin fallback: delete through Jellyfin
var fakeDeleter = new FakeDeleter();
var direct = new ClearrEngine(factory, settings.Get, source, keep, history, NullLogger.Instance, fakeDeleter);
(_, report, _, _) = await direct.BuildAsync(default);
Expect(!S("Unmanaged Show").Eligible && S("Unmanaged Show").Via == null, "Fallback available but off (default): unmanaged show still not removable");
st = settings.Get();
st.AllowDirectDelete = true;
settings.Save(st);
(_, report, _, _) = await direct.BuildAsync(default);
Expect(S("Unmanaged Show").Eligible && S("Unmanaged Show").Via == "Jellyfin" && report.DirectDelete, "Fallback on: unmanaged show eligible via Jellyfin");
Expect(S("Ended Show").Via == "Sonarr", "Managed shows still go through Sonarr");
(_, report, _, _) = await svc.BuildAsync(default);
Expect(!S("Unmanaged Show").Eligible, "App (no deleter) ignores the fallback setting");
var drd = await direct.CleanupAsync(true, false, default);
var dItem = drd.Actions.SingleOrDefault(a => a.Title == "Unmanaged Show");
Expect(dItem?.Action == "WouldDelete" && dItem.Detail.Contains("through Jellyfin") && dItem.Bytes == 123 && fakeDeleter.Deleted.Count == 0, "Fallback dry run: listed with the deleter's size, nothing deleted");
var drr = await direct.CleanupAsync(false, true, default);
Expect(drr.Actions.Single(a => a.Title == "Unmanaged Show").Action == "Deleted" && fakeDeleter.Deleted.SequenceEqual(new[] { "Show:" + unmanaged.Id }), "Fallback delete: whole unmanaged show removed through the deleter");
st = settings.Get();
st.AllowDirectDelete = false;
settings.Save(st);

// ---------------------------------------------------------------- web app: auth + guard over HTTP
var appDir = Path.Combine(tmp, "app");
Environment.SetEnvironmentVariable("CLEARR_CONFIG_DIR", appDir);
Environment.SetEnvironmentVariable("CLEARR_PORT", "16565");
Environment.SetEnvironmentVariable("ASPNETCORE_CONTENTROOT", AppContext.BaseDirectory);
var webroot = AppContext.BaseDirectory;
while (webroot != null && !Directory.Exists(Path.Combine(webroot, "src", "Clearr", "wwwroot"))) { webroot = Path.GetDirectoryName(webroot.TrimEnd('/')); }
Environment.SetEnvironmentVariable("ASPNETCORE_WEBROOT", Path.Combine(webroot!, "src", "Clearr", "wwwroot"));
Environment.SetEnvironmentVariable("Logging__LogLevel__Default", "Warning");
Directory.CreateDirectory(appDir);
new SettingsStore(new ConfigPaths(appDir)).Save(new ClearrSettings { Jellyfin = new() { Url = url + "/jf", ApiKey = "jf-key" }, Sonarr = new() { Url = url + "/sonarr", ApiKey = "sonarr-key" }, Https = new() { Enabled = true, Port = 16566 } });

// ---------------------------------------------------------------- certificates
var certDir = Path.Combine(tmp, "certs");
Directory.CreateDirectory(certDir);
using (var rsa = System.Security.Cryptography.RSA.Create(2048))
{
    var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=clearr.test", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
    using var c = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(90));
    File.WriteAllText(Path.Combine(certDir, "fullchain.pem"), c.ExportCertificatePem());
    File.WriteAllText(Path.Combine(certDir, "privkey.pem"), rsa.ExportPkcs8PrivateKeyPem());
    File.WriteAllBytes(Path.Combine(certDir, "cert.pfx"), c.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "pfx-pass"));
}

var cp = new ConfigPaths(Path.Combine(tmp, "certcfg"));
using (var pem = Clearr.Api.Certificates.Load(new HttpsSettings { CertificatePath = Path.Combine(certDir, "fullchain.pem"), KeyPath = Path.Combine(certDir, "privkey.pem") }, cp))
{
    Expect(pem.HasPrivateKey && Clearr.Api.Certificates.Describe(pem).Contains("clearr.test"), "PEM certificate + key (Let's Encrypt / NPM style) loads");
}

using (var pfx = Clearr.Api.Certificates.Load(new HttpsSettings { CertificatePath = Path.Combine(certDir, "cert.pfx"), CertificatePassword = "pfx-pass" }, cp))
{
    Expect(pfx.HasPrivateKey, "Password-protected .pfx loads");
}

using (var self = Clearr.Api.Certificates.Load(new HttpsSettings(), cp))
{
    Expect(self.HasPrivateKey && File.Exists(cp.File("clearr-selfsigned.pfx")) && Clearr.Api.Certificates.Describe(self).Contains("self-signed"), "Empty path: self-signed certificate created and stored in /config");
}

try { Clearr.Api.Certificates.Load(new HttpsSettings { CertificatePath = "/nope/cert.pem" }, cp); Expect(false, "Missing certificate reported"); }
catch (ArgumentException ex) { Expect(ex.Message.Contains("not found"), "Missing certificate reported clearly"); }

try { Clearr.Api.Certificates.Load(new HttpsSettings { CertificatePath = Path.Combine(certDir, "fullchain.pem") }, cp); Expect(false, "PEM without key rejected"); }
catch (Exception ex) { Expect(ex is ArgumentException or System.Security.Cryptography.CryptographicException, "PEM without its key rejected: " + ex.Message.Split('.')[0]); }
var appThread = new Thread(() => typeof(Clearr.Api.AuthService).Assembly.EntryPoint!.Invoke(null, new object[] { Array.Empty<string>() })) { IsBackground = true };
appThread.Start();
var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:16565") };
for (var i = 0; i < 50; i++)
{
    try { if ((await http.GetStringAsync("/ping")) == "ok") break; } catch { }
    await Task.Delay(200);
}

var insecure = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { BaseAddress = new Uri("https://127.0.0.1:16566") };
Expect((await insecure.GetStringAsync("/ping")) == "ok", "HTTPS works on its own port (self-signed)");
var stj = (await http.GetFromJsonAsync<JsonObject>("/api/status"))!;
Expect(stj["https"]!["active"]!.GetValue<bool>() && !stj["https"]!["restartNeeded"]!.GetValue<bool>(), "Status reports HTTPS active, no restart needed");
var html = await http.GetStringAsync("/");
Expect(html.Contains("<title>Clearr</title>"), "Web UI served");
var rep = await http.GetAsync("/api/report");
Expect(rep.IsSuccessStatusCode, "No password: API open");
var rjson = JsonNode.Parse(await rep.Content.ReadAsStringAsync());
Expect(rjson!["shows"]!.AsArray().Count == 6 && rjson["shows"]![0]!["users"] != null, "Report endpoint returns camelCase shows");
var guard = await http.PostAsync("/api/cleanup?dryRun=false", null);
Console.WriteLine($"  guard: {(int)guard.StatusCode} deletes={w.Log.Count(l => l.StartsWith("DELETE"))}");
Expect(guard.StatusCode == HttpStatusCode.Conflict && w.Log.Count(l => l.StartsWith("DELETE")) == 8, "HTTP: real cleanup refused in DryRun mode (409), nothing deleted");
var test = await (await http.PostAsJsonAsync("/api/test/sonarr", new { url = url.Replace("http://", "") + "/sonarr", apiKey = "sonarr-key" })).Content.ReadFromJsonAsync<JsonObject>();
Expect(test!["ok"]!.GetValue<bool>() && test["message"]!.GetValue<string>().Contains("4.0.20"), "Test connection works without http://");
var bad = await (await http.PostAsJsonAsync("/api/test/jellyfin", new { url = url + "/jf", apiKey = "wrong" })).Content.ReadFromJsonAsync<JsonObject>();
Expect(!bad!["ok"]!.GetValue<bool>() && bad["message"]!.GetValue<string>().Contains("401"), "Wrong Jellyfin key reported");

var cur = (await http.GetFromJsonAsync<JsonObject>("/api/settings"))!;
var apiKey = cur["apiKey"]!.GetValue<string>();
var badCert = cur["settings"]!.DeepClone();
badCert["https"]!["certificatePath"] = "/missing/fullchain.pem";
var badPut = await http.PutAsJsonAsync("/api/settings", new JsonObject { ["settings"] = badCert });
Expect(badPut.StatusCode == HttpStatusCode.BadRequest && (await badPut.Content.ReadAsStringAsync()).Contains("not found"), "Saving HTTPS with a missing certificate is refused");
var put = await http.PutAsJsonAsync("/api/settings", new JsonObject { ["settings"] = cur["settings"]!.DeepClone(), ["newPassword"] = "hunter22" });
Expect(put.IsSuccessStatusCode, "Password set");
var anon = new HttpClient { BaseAddress = http.BaseAddress };
Expect((await anon.GetAsync("/api/report")).StatusCode == HttpStatusCode.Unauthorized, "With password: API needs sign-in");
Expect((await anon.GetAsync("/api/status")).IsSuccessStatusCode && (await anon.GetAsync("/")).IsSuccessStatusCode, "Status and UI still load for the sign-in page");
Expect((await anon.PostAsJsonAsync("/api/login", new { password = "nope" })).StatusCode == HttpStatusCode.Unauthorized, "Wrong password rejected");
var cookies = new CookieContainer();
var user = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = http.BaseAddress };
Expect((await user.PostAsJsonAsync("/api/login", new { password = "hunter22" })).IsSuccessStatusCode && (await user.GetAsync("/api/history")).IsSuccessStatusCode, "Right password: signed in");
var keyed = new HttpRequestMessage(HttpMethod.Get, "/api/history");
keyed.Headers.Add("X-Api-Key", apiKey);
Expect((await anon.SendAsync(keyed)).IsSuccessStatusCode, "API key header works");

await fake.StopAsync();
Console.WriteLine(fails == 0 ? "\nALL PASSED" : $"\n{fails} FAILED");
try { Directory.Delete(tmp, true); } catch { }
return fails == 0 ? 0 : 1;

internal class FakeDeleter : IDirectDeleter
{
    public List<string> Deleted { get; } = new();

    public long Measure(DirectTarget t) => 123;

    public string Delete(DirectTarget t)
    {
        Deleted.Add($"{t.Kind}:{t.ItemId}");
        return "deleted through Jellyfin";
    }
}

internal class SimpleFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new HttpClientHandler { UseProxy = false });
}
