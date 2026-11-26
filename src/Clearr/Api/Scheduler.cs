using System.Globalization;
using Clearr.Core;
using Clearr.Storage;

namespace Clearr.Api;

/// <summary>Runs the cleanup once a day at the configured local time (container TZ).</summary>
public class Scheduler : BackgroundService
{
    private readonly ClearrEngine _clearr;
    private readonly SettingsStore _settings;
    private readonly ILogger<Scheduler> _log;
    private DateOnly _lastRun;

    public Scheduler(ClearrEngine clearr, SettingsStore settings, ILogger<Scheduler> log)
    {
        _clearr = clearr;
        _settings = settings;
        _log = log;

        // Started after today's run time: first run is tomorrow (no surprise cleanup on startup).
        var today = DateOnly.FromDateTime(DateTime.Now);
        _lastRun = DateTime.Now.TimeOfDay >= ParseTime(settings.Get().ScheduleTime) ? today : today.AddDays(-1);
    }

    public DateTime? LastRunAt { get; private set; }

    public string? LastRunSummary { get; private set; }

    public DateTime NextRun(ClearrSettings s)
    {
        var t = ParseTime(s.ScheduleTime);
        var today = DateTime.Today.Add(t);
        return DateTime.Now < today && _lastRun < DateOnly.FromDateTime(DateTime.Now) ? today : today.AddDays(1);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stop);
                var s = _settings.Get();
                var now = DateTime.Now;
                if (DateOnly.FromDateTime(now) > _lastRun && now.TimeOfDay >= ParseTime(s.ScheduleTime))
                {
                    _lastRun = DateOnly.FromDateTime(now);
                    if (s.Mode == CleanupMode.Off || !s.Jellyfin.IsConfigured)
                    {
                        continue;
                    }

                    _log.LogInformation("Scheduled cleanup ({Mode})", s.Mode);
                    var r = await _clearr.CleanupAsync(dryRun: s.Mode != CleanupMode.Delete, record: true, stop);
                    LastRunAt = DateTime.UtcNow;
                    LastRunSummary = $"{(r.DryRun ? "Dry run" : "Cleanup")}: {r.Actions.Count(a => a.Action is "Deleted" or "WouldDelete")} item(s), {r.TotalBytes / 1073741824.0:0.0} GB";
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LastRunAt = DateTime.UtcNow;
                LastRunSummary = "Failed: " + ex.Message;
                _log.LogError(ex, "Scheduled cleanup failed");
            }
        }
    }

    private static TimeSpan ParseTime(string? hhmm) =>
        TimeSpan.TryParseExact(hhmm ?? "", new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out var t) ? t : TimeSpan.FromHours(4);
}
