using System.Globalization;
using Clearr.Core;
using Clearr.Storage;

namespace Clearr.Api;

/// <summary>
/// Runs the daily cleanups at their configured local times (container TZ):
/// one for shows and one for movies, each in its own mode.
/// </summary>
public class Scheduler : BackgroundService
{
    private static readonly string[] TimeFormats = { @"hh\:mm", @"h\:mm" };
    private static readonly CleanupScope[] Kinds = { CleanupScope.Shows, CleanupScope.Movies };
    private readonly ClearrEngine _clearr;
    private readonly SettingsStore _settings;
    private readonly ILogger<Scheduler> _log;
    private readonly Dictionary<CleanupScope, DateOnly> _lastRun = new();

    public Scheduler(ClearrEngine clearr, SettingsStore settings, ILogger<Scheduler> log)
    {
        _clearr = clearr;
        _settings = settings;
        _log = log;

        // Started after today's run time: that kind's first run is tomorrow (no surprise cleanup on startup).
        var s = settings.Get();
        var today = DateOnly.FromDateTime(DateTime.Now);
        foreach (var kind in Kinds)
        {
            _lastRun[kind] = DateTime.Now.TimeOfDay >= ParseTime(TimeFor(s, kind)) ? today : today.AddDays(-1);
        }
    }

    public DateTime? LastRunAt { get; private set; }

    public Msg? LastRunSummary { get; private set; }

    public static string TimeFor(ClearrSettings s, CleanupScope kind) => kind == CleanupScope.Movies ? s.MoviesScheduleTime : s.ShowsScheduleTime;

    /// <summary>When this kind runs next, or null when its mode is Off.</summary>
    public DateTime? NextRun(ClearrSettings s, CleanupScope kind)
    {
        if (s.ModeFor(kind) == CleanupMode.Off)
        {
            return null;
        }

        var today = DateTime.Today.Add(ParseTime(TimeFor(s, kind)));
        return DateTime.Now < today && _lastRun[kind] < DateOnly.FromDateTime(DateTime.Now) ? today : today.AddDays(1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                foreach (var kind in Kinds)
                {
                    await RunIfDueAsync(kind, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunIfDueAsync(CleanupScope kind, CancellationToken stop)
    {
        var s = _settings.Get();
        var now = DateTime.Now;
        if (DateOnly.FromDateTime(now) <= _lastRun[kind] || now.TimeOfDay < ParseTime(TimeFor(s, kind)))
        {
            return;
        }

        _lastRun[kind] = DateOnly.FromDateTime(now);
        var mode = s.ModeFor(kind);
        if (ClearrEngine.ScheduledDryRun(mode) is not bool dry || !s.Jellyfin.IsConfigured)
        {
            return;
        }

        var name = kind == CleanupScope.Movies ? "Movies" : "Shows";
        try
        {
            _log.LogInformation("Scheduled {Kind} cleanup ({Mode})", name, mode);
            var r = await _clearr.CleanupAsync(dryRun: dry, record: true, stop, kind);
            LastRunAt = DateTime.UtcNow;
            LastRunSummary = new Msg($"run.{name.ToLowerInvariant()}{(r.DryRun ? "Dry" : "Cleanup")}", new()
            {
                ["items"] = r.Actions.Count(a => a.Action is "Deleted" or "WouldDelete"),
                ["gb"] = Math.Round(r.TotalBytes / 1073741824.0, 1),
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastRunAt = DateTime.UtcNow;
            LastRunSummary = new Msg($"run.{name.ToLowerInvariant()}Failed", new() { ["error"] = ex.Message });
            _log.LogError(ex, "Scheduled {Kind} cleanup failed", name);
        }
    }

    private static TimeSpan ParseTime(string? hhmm) =>
        TimeSpan.TryParseExact(hhmm ?? "", TimeFormats, CultureInfo.InvariantCulture, out var t) ? t : TimeSpan.FromHours(4);
}
