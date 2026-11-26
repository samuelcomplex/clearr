using System.Text.Json;
using System.Text.Json.Nodes;
using Clearr.Api;
using Clearr.Clients;
using Clearr.Core;
using Clearr.Storage;

var builder = WebApplication.CreateBuilder(args);
var startupPaths = new ConfigPaths();
startupPaths.RestrictSecrets();
var https = new HttpsState();

// HTTP on $CLEARR_PORT (default 8585); HTTPS on the configured port when enabled in Settings.
// ASPNETCORE_URLS, if set, takes over completely (used by the dev tools).
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    var httpPort = int.TryParse(Environment.GetEnvironmentVariable("CLEARR_PORT"), out var p) ? p : 8585;
    var startupSettings = new SettingsStore(startupPaths).Get();
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.Limits.MaxRequestBodySize = 1024 * 1024; // settings and keeps are tiny
        k.ListenAnyIP(httpPort);
        if (startupSettings.Https.Enabled)
        {
            try
            {
                var cert = Certificates.Load(startupSettings.Https, startupPaths);
                k.ListenAnyIP(startupSettings.Https.Port, o => o.UseHttps(cert));
                https.ActivePort = startupSettings.Https.Port;
                https.Certificate = Certificates.Describe(cert);
            }
            catch (Exception ex)
            {
                https.Error = "HTTPS is off: " + ex.Message;
                Console.Error.WriteLine(https.Error);
            }
        }
    });
}

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});
builder.Services.AddHttpClient("clearr");
builder.Services.AddSingleton(startupPaths);
builder.Services.AddSingleton(https);
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<KeepStore>();
builder.Services.AddSingleton<HistoryStore>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<ILibrarySource>(sp => new JellyfinApiSource(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<SettingsStore>().Get));
builder.Services.AddSingleton(sp => new ClearrEngine(
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<SettingsStore>().Get,
    sp.GetRequiredService<ILibrarySource>(),
    sp.GetRequiredService<KeepStore>(),
    sp.GetRequiredService<HistoryStore>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Clearr")));
builder.Services.AddSingleton<Scheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Scheduler>());

var app = builder.Build();
_ = app.Services.GetRequiredService<AuthService>(); // creates api.key and session.key at startup
var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

app.Use(async (ctx, next) =>
{
    Security.AddHeaders(ctx);
    if ((ctx.Request.Path.Value ?? "").StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
        && Security.Check(ctx, ctx.RequestServices.GetRequiredService<AuthService>()) is var (status, error))
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { error });
        return;
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/ping", () => "ok");

// The shared UI (embedded in Clearr.Core, identical to what the Jellyfin plugin serves).
app.MapGet("/ui/{file}", (string file) =>
{
    var stream = ClearrUi.Open(file);
    return stream == null ? Results.NotFound() : Results.Stream(stream, ClearrUi.ContentType(file));
});

var api = app.MapGroup("/api");

api.MapGet("/status", (HttpContext ctx, AuthService auth, SettingsStore st, Scheduler sch, HttpsState hs) =>
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
        https = new { active = hs.ActivePort.HasValue, port = hs.ActivePort, certificate = hs.Certificate, error = hs.Error, restartNeeded = st.Get().Https.Enabled != hs.ActivePort.HasValue || (hs.ActivePort.HasValue && st.Get().Https.Port != hs.ActivePort) },
    });
});

api.MapPost("/login", async (HttpContext ctx, AuthService auth, LoginThrottle throttle) =>
{
    var ip = ctx.Connection.RemoteIpAddress;
    if (throttle.LockedFor(ip) is TimeSpan wait)
    {
        return Results.Json(new { ok = false, error = $"Too many wrong passwords. Try again in {Math.Ceiling(wait.TotalMinutes)} minutes." }, statusCode: 429);
    }

    var body = await JsonNode.ParseAsync(ctx.Request.Body);
    if (!auth.PasswordRequired || auth.Verify(body?["password"]?.GetValue<string>() ?? ""))
    {
        throttle.Succeeded(ip);
        ctx.Response.Cookies.Append(AuthService.Cookie, auth.IssueSession(), new CookieOptions { HttpOnly = true, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Strict, Expires = DateTimeOffset.UtcNow.AddDays(30) });
        return Results.Ok(new { ok = true });
    }

    throttle.Failed(ip);
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

    incoming.AllowDirectDelete = false; // plugin-only setting
    if (SettingsRules.Normalize(incoming) is string problem)
    {
        return Results.BadRequest(new { error = problem });
    }

    if (incoming.Https.Enabled)
    {
        try
        {
            using var cert = Certificates.Load(incoming.Https, ctx.RequestServices.GetRequiredService<ConfigPaths>());
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = "HTTPS certificate problem: " + ex.Message });
        }
    }
    st.Save(incoming);

    // Keep the browser signed in when a password is first set.
    if (string.IsNullOrEmpty(current.PasswordHash) && !string.IsNullOrEmpty(incoming.PasswordHash))
    {
        ctx.Response.Cookies.Append(AuthService.Cookie, ctx.RequestServices.GetRequiredService<AuthService>().IssueSession(), new CookieOptions { HttpOnly = true, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Strict, Expires = DateTimeOffset.UtcNow.AddDays(30) });
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

api.MapGet("/report", async (ClearrEngine svc, CancellationToken ct) =>
{
    try
    {
        var (_, report, _, _) = await svc.BuildAsync(ct);
        return Results.Ok(report);
    }
    catch (Exception ex) when (ex is ApiException or ArgumentException)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 502);
    }
});

api.MapPost("/cleanup", async (bool? dryRun, ClearrEngine svc, CancellationToken ct) =>
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

api.MapPost("/https/check", (HttpsSettings h, ConfigPaths paths) =>
{
    try
    {
        using var cert = Certificates.Load(h, paths);
        return Results.Ok(new { ok = true, message = "Certificate OK: " + Certificates.Describe(cert) });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, message = ex.Message });
    }
});

api.MapPost("/restart", (IHostApplicationLifetime life) =>
{
    // Docker's restart policy (unless-stopped) starts Clearr again with the new settings.
    _ = Task.Run(async () => { await Task.Delay(500); life.StopApplication(); });
    return Results.Ok(new { ok = true });
});

api.MapGet("/history", (ClearrEngine svc) => Results.Ok(svc.History()));

api.MapPost("/keep", async (HttpContext ctx, ClearrEngine svc, CancellationToken ct) =>
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
