using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Clearr.Core;

/// <summary>What a notification says, in the chosen language, plus the raw numbers for webhooks.</summary>
public sealed record Notification(string Title, string Body, JsonObject Data, bool Problem);

/// <summary>
/// Sends a short message after a run to ntfy, a Discord webhook and/or a generic JSON webhook.
/// Never throws: a notification that can't be delivered is logged and the run carries on.
/// </summary>
public static class Notifier
{
    public const int MaxListed = 15;

    /// <summary>Builds the message for a finished run, or null when there's nothing worth telling.</summary>
    public static Notification? ForRun(CleanupResult r, CleanupScope scope, NotificationSettings n, string? language)
    {
        var deleted = r.Actions.Where(a => a.Action == "Deleted").ToList();
        var would = r.Actions.Where(a => a.Action == "WouldDelete").ToList();
        var failed = r.Actions.Where(a => a.Action == "Failed").ToList();
        var listed = deleted.Concat(failed).Concat(n.IncludeDryRuns ? would : Enumerable.Empty<HistoryEntry>()).ToList();
        if (r.Error == null && deleted.Count == 0 && failed.Count == 0 && !(n.IncludeDryRuns && would.Count > 0))
        {
            return null;
        }

        string T(string code, Dictionary<string, object?>? a = null) => Messages.Text(code, language, a);
        var freed = deleted.Sum(a => a.Bytes);
        var title = r.Error != null ? T("notify.titleError")
            : deleted.Count > 0 ? T("notify.titleDeleted", new() { ["count"] = deleted.Count, ["size"] = Size(freed) })
            : failed.Count > 0 ? T("notify.titleFailed", new() { ["count"] = failed.Count })
            : T("notify.titleWould", new() { ["count"] = would.Count, ["size"] = Size(would.Sum(a => a.Bytes)) });

        var body = new StringBuilder();
        if (r.Error != null)
        {
            body.AppendLine(r.ErrorMsg != null ? Messages.Render(r.ErrorMsg, language) : r.Error);
        }

        foreach (var a in listed.Take(MaxListed))
        {
            var what = a.SeasonName is { Length: > 0 } ? a.Title + " – " + a.SeasonName : a.Title;
            var line = T("action." + a.Action) + ": " + what + (a.Bytes > 0 ? " (" + Size(a.Bytes) + ")" : "");
            if (a.Action == "Failed")
            {
                line += " · " + (a.Parts.Count > 0 ? Messages.Render(a.Parts, language) : a.Detail);
            }

            body.AppendLine("• " + line);
        }

        if (listed.Count > MaxListed)
        {
            body.AppendLine(T("notify.more", new() { ["count"] = listed.Count - MaxListed }));
        }

        if (failed.Count > 0 && deleted.Count > 0)
        {
            body.AppendLine(T("notify.titleFailed", new() { ["count"] = failed.Count }));
        }

        var data = new JsonObject
        {
            ["event"] = "cleanup",
            ["scope"] = scope.ToString(),
            ["dryRun"] = r.DryRun,
            ["deleted"] = deleted.Count,
            ["failed"] = failed.Count,
            ["wouldDelete"] = would.Count,
            ["bytesFreed"] = freed,
            ["error"] = r.Error,
            ["items"] = new JsonArray(r.Actions.Where(a => a.Action != "Skipped").Select(a => (JsonNode)new JsonObject
            {
                ["action"] = a.Action,
                ["kind"] = a.Kind,
                ["title"] = a.Title,
                ["season"] = a.SeasonName,
                ["bytes"] = a.Bytes,
                ["detail"] = a.Detail,
            }).ToArray()),
        };
        return new Notification(title, body.ToString().TrimEnd(), data, r.Error != null || failed.Count > 0);
    }

    /// <summary>A whole run that stopped with an exception (Sonarr unreachable and so on).</summary>
    public static Notification ForCrash(Exception ex, CleanupScope scope, string? language) => new(
        Messages.Text("notify.titleError", language),
        ex.Message,
        new JsonObject { ["event"] = "cleanup", ["scope"] = scope.ToString(), ["error"] = ex.Message },
        true);

    public static Notification Test(string? language) => new(
        Messages.Text("notify.testTitle", language),
        Messages.Text("notify.testBody", language),
        new JsonObject { ["event"] = "test" },
        false);

    /// <summary>Sends to every configured target. Returns one line per target: (name, error or null).</summary>
    public static async Task<List<(string Target, string? Error)>> SendAsync(HttpClient http, NotificationSettings n, Notification notice, ILogger log, CancellationToken ct)
    {
        var results = new List<(string, string?)>();
        async Task Try(string name, Func<HttpRequestMessage> make)
        {
            try
            {
                using var req = make();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                using var res = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(((int)res.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + res.ReasonPhrase);
                }

                results.Add((name, null));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException or FormatException)
            {
                log.LogWarning("Notification to {Target} failed: {Message}", name, ex.Message);
                results.Add((name, ex.Message));
            }
        }

        if (!string.IsNullOrWhiteSpace(n.NtfyUrl))
        {
            await Try("ntfy", () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, n.NtfyUrl.Trim()) { Content = new StringContent(notice.Body.Length > 0 ? notice.Body : notice.Title, Encoding.UTF8, "text/plain") };
                // ntfy reads non-ASCII header values only when RFC 2047-encoded.
                req.Headers.TryAddWithoutValidation("Title", "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(notice.Title)) + "?=");
                req.Headers.TryAddWithoutValidation("Tags", notice.Problem ? "warning" : "wastebasket");
                if (notice.Problem)
                {
                    req.Headers.TryAddWithoutValidation("Priority", "high");
                }

                var token = n.NtfyToken.Trim();
                if (token.Length > 0)
                {
                    req.Headers.Authorization = token.Contains(':', StringComparison.Ordinal)
                        ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(token)))
                        : new AuthenticationHeaderValue("Bearer", token);
                }

                return req;
            }).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(n.DiscordWebhookUrl))
        {
            await Try("Discord", () =>
            {
                var text = "**" + notice.Title + "**" + (notice.Body.Length > 0 ? "\n" + notice.Body : "");
                if (text.Length > 2000)
                {
                    text = text[..1997] + "…";
                }

                var json = new JsonObject { ["username"] = "Clearr", ["content"] = text, ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() } };
                return new HttpRequestMessage(HttpMethod.Post, n.DiscordWebhookUrl.Trim()) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };
            }).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(n.WebhookUrl))
        {
            await Try("webhook", () =>
            {
                var json = (JsonObject)notice.Data.DeepClone();
                json["title"] = notice.Title;
                json["text"] = notice.Body;
                return new HttpRequestMessage(HttpMethod.Post, n.WebhookUrl.Trim()) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };
            }).ConfigureAwait(false);
        }

        return results;
    }

    public static string Size(long bytes)
    {
        var gb = bytes / 1073741824.0;
        return gb >= 1
            ? gb.ToString(gb >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " GB"
            : Math.Max(1, (long)Math.Round(bytes / 1048576.0)).ToString(CultureInfo.InvariantCulture) + " MB";
    }
}

/// <summary>A small, flat summary for a Home Assistant REST sensor (or any dashboard).</summary>
public static class ClearrSummary
{
    public static JsonObject Build(IEnumerable<HistoryEntry> history, ClearrSettings s, DateTime nowUtc)
    {
        var deleted = history.Where(h => h.Action == "Deleted").ToList();
        var recent = deleted.Where(h => h.Date >= nowUtc.AddDays(-30)).ToList();
        var last = deleted.Count == 0 ? (DateTime?)null : deleted.Max(h => h.Date);
        static double Gb(long b) => Math.Round(b / 1073741824.0, 1);
        return new JsonObject
        {
            ["showsMode"] = s.ShowsMode.ToString(),
            ["moviesMode"] = s.MoviesMode.ToString(),
            ["removedLast30Days"] = recent.Count,
            ["freedLast30DaysGb"] = Gb(recent.Sum(h => h.Bytes)),
            ["removedTotal"] = deleted.Count,
            ["freedTotalGb"] = Gb(deleted.Sum(h => h.Bytes)),
            ["lastRemovedAt"] = last,
            ["lastRemoved"] = deleted.OrderByDescending(h => h.Date).FirstOrDefault() is HistoryEntry h
                ? (h.SeasonName is { Length: > 0 } ? h.Title + " – " + h.SeasonName : h.Title)
                : null,
            ["failedLast30Days"] = history.Count(x => x.Action == "Failed" && x.Date >= nowUtc.AddDays(-30)),
        };
    }
}
