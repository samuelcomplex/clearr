using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Clearr.Clients;

/// <summary>Small JSON-over-HTTP helper shared by the Jellyfin, Sonarr and Radarr clients.</summary>
public class HttpApi
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Action<HttpRequestMessage> _auth;
    private readonly string _name;

    public HttpApi(HttpClient http, string name, string url, Action<HttpRequestMessage> auth)
    {
        _http = http;
        _name = name;
        _baseUrl = NormalizeUrl(url, name);
        _auth = auth;
    }

    /// <summary>Accepts "192.168.1.6:8989", "http://host:8989/", "https://sonarr.example.com/sonarr" …</summary>
    public static string NormalizeUrl(string? url, string name = "Server")
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0)
        {
            throw new ArgumentException($"{name} URL is empty");
        }

        if (!u.Contains("://", StringComparison.Ordinal))
        {
            u = "http://" + u;
        }

        if (!Uri.TryCreate(u, UriKind.Absolute, out var p) || (p.Scheme != Uri.UriSchemeHttp && p.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"{name} URL isn't valid: {url}");
        }

        return p.ToString().TrimEnd('/');
    }

    public Task<JsonNode?> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);

    public Task<JsonNode?> PutAsync(string path, JsonNode body, CancellationToken ct) => SendAsync(HttpMethod.Put, path, body, ct);

    public Task<JsonNode?> PostAsync(string path, JsonNode? body, CancellationToken ct) => SendAsync(HttpMethod.Post, path, body, ct);

    public Task<JsonNode?> DeleteAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Delete, path, null, ct);

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, _baseUrl + "/" + path.TrimStart('/'));
        _auth(req);
        if (body != null)
        {
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException($"{_name}: can't connect ({ex.Message})");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException($"{_name}: timed out");
        }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ApiException($"{_name} rejected the API key (401)", 401);
            }

            if (!resp.IsSuccessStatusCode)
            {
                var snippet = text.Length > 300 ? text[..300] : text;
                throw new ApiException($"{_name}: HTTP {(int)resp.StatusCode} on {method} {path}: {snippet}", (int)resp.StatusCode);
            }

            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
    }
}

public class ApiException : Exception
{
    public ApiException(string message, int? status = null)
        : base(message)
    {
        Status = status;
    }

    /// <summary>The HTTP status, when the server answered with an error.</summary>
    public int? Status { get; }
}

internal static class JsonExt
{
    public static string? Str(this JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static int? Int(this JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    public static long Long(this JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

    public static bool Bool(this JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static DateTime? Date(this JsonNode? n, string key) =>
        n?[key] is JsonValue v && v.TryGetValue<string>(out var s) && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public static IEnumerable<JsonNode> Arr(this JsonNode? n, string? key = null)
    {
        var a = key == null ? n as JsonArray : n?[key] as JsonArray;
        return a == null ? Enumerable.Empty<JsonNode>() : a.Where(x => x != null).Select(x => x!);
    }

    public static List<string> Strings(this JsonNode? n, string key) => n.Arr(key).Select(x => x.GetValue<string>()).ToList();

    public static Dictionary<string, string> Dict(this JsonNode? n, string key)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (n?[key] is JsonObject o)
        {
            foreach (var kv in o)
            {
                if (kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                {
                    d[kv.Key] = s;
                }
            }
        }

        return d;
    }
}
