using System.Text.Json;
using System.Text.Json.Nodes;
using Clearr.Api;
using Clearr.Clients;
using Clearr.Core;
using Clearr.Storage;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")))
{
    builder.WebHost.UseUrls("http://0.0.0.0:6565");
}

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});
builder.Services.AddHttpClient("clearr");
builder.Services.AddSingleton<ConfigPaths>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<KeepStore>();
builder.Services.AddSingleton<HistoryStore>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<ClearrService>();
builder.Services.AddSingleton<Scheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Scheduler>());

var app = builder.Build();
var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

app.UseDefaultFiles();
app.UseStaticFiles();

// Everything under /api needs a session (if a password is set), except login and status.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    var open = path is "/api/status" or "/api/login" or "/ping";
    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && !open && !ctx.RequestServices.GetRequiredService<AuthService>().IsAuthenticated(ctx))
    {
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsJsonAsync(new { error = "Sign in required" });
        return;
    }

    await next();
});

app.MapGet("/ping", () => "ok");

var api = app.MapGroup("/api");

api.MapGet("/status", (HttpContext ctx, AuthService auth, SettingsStore st, Scheduler sch) =>
{
    var s = st.Get();
    return Results.Ok(new
    {
        version,
        configured = s.Jellyfin.IsConfigured,
        passwordRequired = auth.PasswordRequired,
        authenticated = auth.IsAuthenticated(ctx),
        mode = s.Mode,
        nextRun = sch.NextRun(s),
        lastRunAt = sch.LastRunAt,
        lastRunSummary = sch.LastRunSummary,
    });
});

api.MapPost("/login", async (HttpContext ctx, AuthService auth) =>
{
    var body = await JsonNode.ParseAsync(ctx.Request.Body);
    if (!auth.PasswordRequired || auth.Verify(body?["password"]?.GetValue<string>() ?? ""))
    {
        ctx.Response.Cookies.Append(AuthService.Cookie, auth.IssueSession(), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Expires = DateTimeOffset.UtcNow.AddDays(30) });
        return Results.Ok(new { ok = true });
    }

    await Task.Delay(1000); // slow down guessing
    return Results.Json(new { ok = false, error = "Wrong password" }, statusCode: 401);
});

api.MapPost("/logout", (HttpContext ctx) =>
{
    ctx.Response.Cookies.Delete(AuthService.Cookie);
    return Results.Ok(new { ok = true });
});

api.MapGet("/settings", (SettingsStore st, AuthService auth) =>
{
    var s = st.Get();
    var hasPassword = !string.IsNullOrEmpty(s.PasswordHash);
    s.PasswordHash = "";
    return Results.Ok(new { settings = s, hasPassword, apiKey = auth.ApiKey });
});

api.MapPut("/settings", async (HttpContext ctx, SettingsStore st) =>
{
    var body = await JsonNode.ParseAsync(ctx.Request.Body) ?? new JsonObject();
    var incoming = body["settings"].Deserialize<ClearrSettings>(Json.Options) ?? new ClearrSettings();
    var current = st.Get();
    incoming.PasswordHash = current.PasswordHash;

    var newPassword = body["newPassword"]?.GetValue<string>();
    if (body["clearPassword"]?.GetValue<bool>() == true)
    {
        incoming.PasswordHash = "";
    }
    else if (!string.IsNullOrEmpty(newPassword))
    {
        if (newPassword.Length < 6)
        {
            return Results.BadRequest(new { error = "Password must be at least 6 characters" });
        }

        incoming.PasswordHash = AuthService.Hash(newPassword);
    }

    incoming.GraceDays = Math.Max(0, incoming.GraceDays);
    incoming.InactiveDays = Math.Max(1, incoming.InactiveDays);
    incoming.SampledMaxEpisodes = Math.Max(0, incoming.SampledMaxEpisodes);
    incoming.MaxDeleteGbPerRun = Math.Max(0, incoming.MaxDeleteGbPerRun);
    st.Save(incoming);

    // Keep the browser signed in when a password is first set.
    if (string.IsNullOrEmpty(current.PasswordHash) && !string.IsNullOrEmpty(incoming.PasswordHash))
    {
        ctx.Response.Cookies.Append(AuthService.Cookie, ctx.RequestServices.GetRequiredService<AuthService>().IssueSession(), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Expires = DateTimeOffset.UtcNow.AddDays(30) });
    }

    return Results.Ok(new { ok = true });
});

api.MapPost("/test/{service}", async (string service, ConnectionSettings c, IHttpClientFactory f, CancellationToken ct) =>
{
    try
    {
        var http = f.CreateClient("clearr");
        http.Timeout = TimeSpan.FromSeconds(15);
        var msg = service.ToLowerInvariant() switch
        {
            "jellyfin" => await new JellyfinClient(http, c).TestAsync(ct),
            "sonarr" => await new SonarrClient(http, c).TestAsync(ct),
            "radarr" => await new RadarrClient(http, c).TestAsync(ct),
            _ => throw new ArgumentException("Unknown service " + service),
        };
        return Results.Ok(new { ok = true, message = msg });
    }
    catch (Exception ex) when (ex is ApiException or ArgumentException)
    {
        return Results.Ok(new { ok = false, message = ex.Message });
    }
});

api.MapGet("/users", async (SettingsStore st, IHttpClientFactory f, CancellationToken ct) =>
{
    var s = st.Get();
    if (!s.Jellyfin.IsConfigured)
    {
        return Results.Ok(Array.Empty<MediaUser>());
    }

    try
    {
        return Results.Ok(await new JellyfinClient(f.CreateClient("clearr"), s.Jellyfin).GetUsersAsync(ct));
    }
    catch (Exception ex) when (ex is ApiException or ArgumentException)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 502);
    }
});

api.MapGet("/report", async (ClearrService svc, SettingsStore st, CancellationToken ct) =>
{
    try
    {
        var (_, report, _, _) = await svc.BuildAsync(st.Get(), ct);
        return Results.Ok(report);
    }
    catch (Exception ex) when (ex is ApiException or ArgumentException)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 502);
    }
});

api.MapPost("/cleanup", async (bool? dryRun, ClearrService svc, CancellationToken ct) =>
{
    var dry = dryRun ?? true;
    try
    {
        var r = await svc.CleanupAsync(dry, record: !dry, ct);
        return r.Error != null ? Results.Json(r, statusCode: 409) : Results.Ok(r);
    }
    catch (Exception ex) when (ex is ApiException or ArgumentException)
    {
        return Results.Json(new CleanupResult { DryRun = true, Error = ex.Message }, statusCode: 502);
    }
});

api.MapGet("/history", (HistoryStore h) => Results.Ok(h.GetAll()));

api.MapPost("/keep", async (HttpContext ctx, ClearrService svc, CancellationToken ct) =>
{
    var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct);
    var id = body?["id"]?.GetValue<string>();
    var kind = body?["kind"]?.GetValue<string>() ?? "show";
    if (string.IsNullOrWhiteSpace(id) || kind is not ("show" or "season" or "movie"))
    {
        return Results.BadRequest(new { error = "id and kind (show, season, movie) are required" });
    }

    var msg = await svc.SetKeepAsync(id, kind, body?["keep"]?.GetValue<bool>() ?? true, ct);
    return Results.Ok(new { ok = true, message = msg });
});

app.Run();

public partial class Program
{
}
