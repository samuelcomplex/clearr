using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace Clearr.Tests;

/// <summary>In-memory Jellyfin + Sonarr + Radarr speaking just enough of their real APIs.</summary>
public class FakeWorld
{
    public record Ep(string Id, string SeriesId, string SeasonId, int Season, int Number);
    public record Show(string Id, string Name, string Status, int Tvdb, List<(string Id, int Number)> Seasons, List<string> Tags);
    public record Movie(string Id, string Name, int Year, int Tmdb, List<string> Tags);

    public List<(string Id, string Name)> Users = new();
    public List<Show> Shows = new();
    public List<Ep> Episodes = new();
    public List<Movie> Movies = new();
    public Dictionary<(string User, string Item), (bool Played, long Ticks, DateTime Last)> UserData = new();
    public HashSet<(string User, string Item)> Favorites = new();

    // Sonarr state
    public Dictionary<int, JsonObject> SonarrSeries = new();
    public List<(int Id, int SeriesId, int Season, long Size)> EpisodeFiles = new();
    public Dictionary<int, string> SonarrTags = new();

    // Radarr state
    public Dictionary<int, JsonObject> RadarrMovies = new();
    public Dictionary<int, string> RadarrTags = new();

    public int JellyfinRefreshes;
    public List<string> Log = new();

    // Failure injection and hooks for the safety tests.
    public HashSet<int> FailSeriesPut = new();
    public HashSet<int> FailMoviePut = new();
    public HashSet<int> FailEpisodeFileDelete = new();
    public Action<string>? OnChange;

    /// <summary>Simulated network + server time per request (perf tests).</summary>
    public int LatencyMs;

    public int Requests;

    /// <summary>Notifications received: (path, headers, body).</summary>
    public List<(string Path, Dictionary<string, string> Headers, string Body)> Notifications { get; } = new();

    public void Record(string line)
    {
        Log.Add(line);
        OnChange?.Invoke(line);
    }
}

public static class FakeServers
{
    public static async Task<(WebApplication App, string Url)> StartAsync(FakeWorld w)
    {
        var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = new[] { "--hostBuilder:reloadConfigOnChange=false" } });
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        var app = b.Build();
        app.Use(async (ctx, next) =>
        {
            Interlocked.Increment(ref w.Requests);
            if (w.LatencyMs > 0)
            {
                await Task.Delay(w.LatencyMs);
            }

            await next();
        });

        // ---------------- notification targets ----------------
        foreach (var path in new[] { "/ntfy/clearr", "/discord", "/hook", "/hook-fail" })
        {
            app.MapPost(path, async (HttpContext c) =>
            {
                var body = await new StreamReader(c.Request.Body).ReadToEndAsync();
                lock (w.Notifications)
                {
                    w.Notifications.Add((path, c.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), body));
                }

                return path == "/hook-fail" ? Results.StatusCode(500) : Results.Ok();
            });
        }

        // ---------------- Jellyfin (PascalCase JSON, like the real thing) ----------------
        var pascal = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = null };
        IResult P(object o) => Results.Json(o, pascal);
        bool JfAuth(HttpContext c) => c.Request.Headers.Authorization.ToString().Contains("Token=\"jf-key\"");

        app.MapGet("/jf/System/Info", (HttpContext c) => JfAuth(c) ? P(new { Version = "12.4.0", ServerName = "fake" }) : Results.StatusCode(401));
        app.MapGet("/jf/Users", (HttpContext c) => JfAuth(c)
            ? P(w.Users.Select(u => new { Id = u.Id, Name = u.Name, Policy = new { IsDisabled = false } }))
            : Results.StatusCode(401));
        app.MapPost("/jf/Library/Refresh", () => { w.JellyfinRefreshes++; return Results.NoContent(); });
        app.MapGet("/jf/Items", (HttpContext c) =>
        {
            if (!JfAuth(c))
            {
                return Results.StatusCode(401);
            }

            var q = c.Request.Query;
            var types = q["IncludeItemTypes"].ToString().Split(',');
            var user = q["userId"].ToString();
            var filter = q["Filters"].ToString();
            var start = int.Parse(q["StartIndex"].ToString() is { Length: > 0 } s ? s : "0");
            var limit = int.Parse(q["Limit"].ToString() is { Length: > 0 } l ? l : "1000");
            var items = new List<object>();

            if (types.Contains("Series"))
            {
                items.AddRange(w.Shows.Where(s => Keep(s.Id)).Select(s => (object)new { Id = s.Id, Name = s.Name, Status = s.Status, Tags = s.Tags, ProviderIds = new Dictionary<string, string> { ["Tvdb"] = s.Tvdb.ToString() } }));
            }

            if (types.Contains("Season"))
            {
                items.AddRange(w.Shows.SelectMany(s => s.Seasons.Where(se => Keep(se.Id)).Select(se => (object)new { Id = se.Id, Name = $"Season {se.Number}", IndexNumber = se.Number, SeriesId = s.Id, Tags = Array.Empty<string>() })));
            }

            object EpDto(FakeWorld.Ep e) => new
            {
                Id = e.Id,
                SeriesId = e.SeriesId,
                SeasonId = e.SeasonId,
                ParentIndexNumber = e.Season,
                IndexNumber = e.Number,
                UserData = user.Length > 0 && w.UserData.TryGetValue((user, e.Id), out var d) ? new { Played = d.Played, PlaybackPositionTicks = d.Ticks, LastPlayedDate = d.Last.ToString("o") } : null,
            };
            object MovieDto(FakeWorld.Movie m) => new
            {
                Id = m.Id,
                Name = m.Name,
                ProductionYear = m.Year,
                Tags = m.Tags,
                ProviderIds = new Dictionary<string, string> { ["Tmdb"] = m.Tmdb.ToString() },
                UserData = user.Length > 0 && w.UserData.TryGetValue((user, m.Id), out var d) ? new { Played = d.Played, PlaybackPositionTicks = d.Ticks, LastPlayedDate = d.Last.ToString("o") } : null,
            };

            bool Keep(string id) => filter switch
            {
                "IsPlayed" => w.UserData.TryGetValue((user, id), out var d) && d.Played,
                "IsResumable" => w.UserData.TryGetValue((user, id), out var d) && !d.Played && d.Ticks > 0,
                "IsFavorite" => w.Favorites.Contains((user, id)),
                _ => true,
            };

            if (types.Contains("Episode"))
            {
                items.AddRange(w.Episodes.Where(e => Keep(e.Id)).Select(EpDto));
            }

            if (types.Contains("Movie"))
            {
                items.AddRange(w.Movies.Where(m => Keep(m.Id)).Select(MovieDto));
            }

            return P(new { Items = items.Skip(start).Take(limit), TotalRecordCount = items.Count });
        });

        // ---------------- Sonarr ----------------
        bool ArrAuth(HttpContext c, string key) => c.Request.Headers["X-Api-Key"] == key;

        void Arr(string prefix, string key, Dictionary<int, string> tags)
        {
            app.MapGet($"/{prefix}/api/v3/system/status", (HttpContext c) => ArrAuth(c, key) ? Results.Json(new { version = prefix == "sonarr" ? "4.0.20" : "5.20.0" }) : Results.StatusCode(401));
            app.MapGet($"/{prefix}/api/v3/tag", () => Results.Json(tags.Select(t => new { id = t.Key, label = t.Value })));
            app.MapPost($"/{prefix}/api/v3/tag", async (HttpContext c) =>
            {
                var body = await JsonNode.ParseAsync(c.Request.Body);
                var id = tags.Count + 1;
                tags[id] = body!["label"]!.GetValue<string>();
                return Results.Json(new { id, label = tags[id] });
            });
        }

        Arr("sonarr", "sonarr-key", w.SonarrTags);
        app.MapGet("/sonarr/api/v3/series", (HttpContext c) => ArrAuth(c, "sonarr-key") ? Results.Text(new JsonArray(w.SonarrSeries.Values.Select(v => v.DeepClone()).ToArray()).ToJsonString(), "application/json") : Results.StatusCode(401));
        app.MapGet("/sonarr/api/v3/series/{id:int}", (int id) => Results.Text(w.SonarrSeries[id].ToJsonString(), "application/json"));
        app.MapPut("/sonarr/api/v3/series/{id:int}", async (int id, HttpContext c) =>
        {
            if (w.FailSeriesPut.Contains(id))
            {
                return Results.StatusCode(500);
            }

            var body = (JsonObject)(await JsonNode.ParseAsync(c.Request.Body))!;
            w.SonarrSeries[id] = body;
            w.Record($"PUT series {id}");
            return Results.Text(body.ToJsonString(), "application/json");
        });
        app.MapGet("/sonarr/api/v3/episodefile", (int seriesId) =>
            Results.Json(w.EpisodeFiles.Where(f => f.SeriesId == seriesId).Select(f => new { id = f.Id, seriesId = f.SeriesId, seasonNumber = f.Season, size = f.Size, relativePath = $"S{f.Season:00}/{f.Id}.mkv" })));
        app.MapDelete("/sonarr/api/v3/episodefile/{id:int}", (int id) =>
        {
            if (w.FailEpisodeFileDelete.Contains(id))
            {
                return Results.StatusCode(500);
            }

            w.EpisodeFiles.RemoveAll(f => f.Id == id);
            w.Record($"DELETE episodefile {id}");
            return Results.Ok();
        });

        // ---------------- Radarr ----------------
        Arr("radarr", "radarr-key", w.RadarrTags);
        app.MapGet("/radarr/api/v3/movie", (HttpContext c) => ArrAuth(c, "radarr-key") ? Results.Text(new JsonArray(w.RadarrMovies.Values.Select(v => v.DeepClone()).ToArray()).ToJsonString(), "application/json") : Results.StatusCode(401));
        app.MapGet("/radarr/api/v3/movie/{id:int}", (int id) => Results.Text(w.RadarrMovies[id].ToJsonString(), "application/json"));
        app.MapPut("/radarr/api/v3/movie/{id:int}", async (int id, HttpContext c) =>
        {
            if (w.FailMoviePut.Contains(id))
            {
                return Results.StatusCode(500);
            }

            var body = (JsonObject)(await JsonNode.ParseAsync(c.Request.Body))!;
            w.RadarrMovies[id] = body;
            w.Record($"PUT movie {id}");
            return Results.Text(body.ToJsonString(), "application/json");
        });
        app.MapDelete("/radarr/api/v3/moviefile/{id:int}", (int id) =>
        {
            foreach (var m in w.RadarrMovies.Values.Where(m => m["movieFile"]?["id"]?.GetValue<int>() == id))
            {
                m.Remove("movieFile");
                m["hasFile"] = false;
            }

            w.Record($"DELETE moviefile {id}");
            return Results.Ok();
        });

        await app.StartAsync();
        var url = app.Urls.First();
        return (app, url);
    }
}
