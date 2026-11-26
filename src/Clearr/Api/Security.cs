using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Clearr.Api;

/// <summary>
/// Request-level protections for the web app:
/// security headers, local-only access while no password is set (like Sonarr's
/// "Disabled for local addresses"), and cross-site request protection for anything that changes state.
/// </summary>
public static class Security
{
    public const string RequestHeader = "X-Clearr-Request";

    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'";

    public static void AddHeaders(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Content-Security-Policy"] = ContentSecurityPolicy;
        h["Cross-Origin-Opener-Policy"] = "same-origin";
    }

    /// <summary>
    /// Returns why a request is refused, or null if it may continue.
    /// Called for /api/* only; status, login and ping stay reachable so the sign-in page works.
    /// </summary>
    public static (int Status, string Error)? Check(HttpContext ctx, AuthService auth)
    {
        var path = ctx.Request.Path.Value ?? "";
        var open = path is "/api/status" or "/api/login";
        var hasKey = auth.HasValidApiKey(ctx);

        if (!open && !auth.PasswordRequired && !hasKey && !IsLocal(ctx.Connection.RemoteIpAddress))
        {
            return (401, "Clearr only accepts connections from your local network until a password is set. Set one in Settings, from a device on your network.");
        }

        if (!open && !auth.IsAuthenticated(ctx))
        {
            return (401, "Sign in required");
        }

        // Anything that changes state must come from Clearr's own page (or carry the API key).
        // A custom header can't be sent cross-site without a CORS preflight, which Clearr never approves.
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !hasKey)
        {
            if (!ctx.Request.Headers.ContainsKey(RequestHeader))
            {
                return (403, "Missing " + RequestHeader + " header (requests from other sites are refused; scripts should use the X-Api-Key header)");
            }

            var origin = ctx.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || !string.Equals(o.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
            {
                return (403, "Cross-site request refused");
            }
        }

        return null;
    }

    /// <summary>Loopback, private, link-local, CGNAT/Tailscale and IPv6 unique-local addresses.</summary>
    public static bool IsLocal(IPAddress? ip)
    {
        if (ip == null)
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return ip.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
        }

        return false;
    }
}

/// <summary>Slows down password guessing: 5 wrong passwords from one address locks it out for 10 minutes.</summary>
public class LoginThrottle
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (int Count, DateTime First)> _failures = new();
    private readonly TimeProvider _time;

    public LoginThrottle(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public TimeSpan? LockedFor(IPAddress? ip)
    {
        var key = ip?.ToString() ?? "?";
        if (_failures.TryGetValue(key, out var f))
        {
            var left = f.First + Window - _time.GetUtcNow().UtcDateTime;
            if (left <= TimeSpan.Zero)
            {
                _failures.TryRemove(key, out _);
                return null;
            }

            if (f.Count >= MaxFailures)
            {
                return left;
            }
        }

        return null;
    }

    public void Failed(IPAddress? ip)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        _failures.AddOrUpdate(ip?.ToString() ?? "?", _ => (1, now), (_, f) => now - f.First > Window ? (1, now) : (f.Count + 1, f.First));
    }

    public void Succeeded(IPAddress? ip) => _failures.TryRemove(ip?.ToString() ?? "?", out _);
}
