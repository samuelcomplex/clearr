// Settings versioning and log files.
using Clearr.Api;
using Clearr.Core;
using Clearr.Storage;
using Microsoft.Extensions.Logging;

namespace Clearr.Tests;

internal static class StorageTests
{
    public static async Task<int> RunAsync()
    {
        var fails = 0;
        void Expect(bool ok, string what)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + "[storage] " + what);
            if (!ok)
            {
                fails++;
            }
        }

        var root = Path.Combine(Path.GetTempPath(), "clearr-storage-" + Guid.NewGuid().ToString("N")[..8]);

        // A settings file from before versioning existed.
        var v0 = new ConfigPaths(Path.Combine(root, "v0"));
        const string old = """{ "mode": "Delete", "graceDays": 3, "sonarr": { "url": "http://sonarr:8989", "apiKey": "abc" } }""";
        File.WriteAllText(v0.File("settings.json"), old);
        var store = new SettingsStore(v0);
        var s = store.Get();
        Expect(s.ShowsMode == CleanupMode.Delete && s.MoviesMode == CleanupMode.Delete && s.GraceDays == 3 && s.Sonarr.ApiKey == "abc" && s.SchemaVersion == ClearrSettings.CurrentSchema, "Old settings load with every value intact and the current format version");
        Expect(File.ReadAllText(v0.File("settings.v0.bak.json")) == old && File.ReadAllText(v0.File("settings.json")).Contains("\"schemaVersion\": 2"), "Upgrade keeps the original as settings.v0.bak.json and saves the new format");
        var again = new SettingsStore(v0);
        again.Get();
        Expect(store.Notice?.ToString().Contains("upgraded") == true && again.Notice == null, "Upgrade is reported once, not on every start");

        // Version 1: one mode and time for everything, "Remove finished movies" off.
        var v1 = new ConfigPaths(Path.Combine(root, "v1"));
        File.WriteAllText(v1.File("settings.json"), """{ "schemaVersion": 1, "mode": "Delete", "deleteMovies": false, "scheduleTime": "03:15" }""");
        var s1 = new SettingsStore(v1).Get();
        Expect(s1.ShowsMode == CleanupMode.Delete && s1.MoviesMode == CleanupMode.Off && s1.ShowsScheduleTime == "03:15" && s1.MoviesScheduleTime == "03:15", "Version 1 settings: shows keep the mode, movies Off (they weren't being removed), both keep the run time");

        // A damaged file must not stop Clearr from starting, and must fall back to the safe defaults.
        var broken = new ConfigPaths(Path.Combine(root, "broken"));
        File.WriteAllText(broken.File("settings.json"), "{ \"mode\": \"Delete\", ");
        var bs = new SettingsStore(broken);
        Expect(bs.Get().ShowsMode == CleanupMode.DryRun && bs.Get().MoviesMode == CleanupMode.DryRun && bs.Notice?.ToString().Contains("couldn't be read") == true, "Damaged settings.json: starts in Dry run with defaults and says so");
        Expect(Directory.GetFiles(broken.Root, "settings.broken-*.json").Length == 1 && !File.Exists(broken.File("settings.json")), "Damaged settings.json is kept aside for inspection");

        // Written by a newer Clearr (a downgrade): load what's understood, keep a copy.
        var newer = new ConfigPaths(Path.Combine(root, "newer"));
        File.WriteAllText(newer.File("settings.json"), """{ "schemaVersion": 99, "showsMode": "Off", "somethingNew": true }""");
        var ns = new SettingsStore(newer);
        Expect(ns.Get().ShowsMode == CleanupMode.Off && File.Exists(newer.File("settings.v99.bak.json")) && ns.Notice?.ToString().Contains("newer") == true, "Settings from a newer version: loaded, copy kept, reported");

        // History keeps translatable messages (nested ones and people lists too) across a save and reload.
        var hp = new ConfigPaths(Path.Combine(root, "history"));
        var waiting = new Msg("reason.waiting", new() { ["people"] = new List<PersonArg> { new("Anna", 3, 10, new[] { "Sampled", "MidEpisode" }) } });
        var blocked = new Msg("reason.blocked", new() { ["block"] = new Msg("block.showStatus", new() { ["status"] = "Continuing" }) });
        new HistoryStore(hp).Add(new[] { new HistoryEntry { Action = "Skipped", Title = "X", Parts = { waiting, blocked }, Detail = Messages.English(new[] { waiting, blocked }) } });
        var back = new HistoryStore(hp).GetAll().Single();
        Expect(Messages.English(back.Parts) == "Waiting on Anna (3/10, sampled, mid-episode) · Everyone finished, but: Show status is Continuing" && back.Detail == Messages.English(back.Parts), "History messages survive a save and reload, and render the same English text");

        // Log files: format, rotation, and only real log names can be opened.
        var logDir = Path.Combine(root, "logs");
        var debug = false;
        using (var logs = new FileLoggerProvider(logDir, () => debug))
        {
            var log = logs.CreateLogger("Clearr.Engine");
            log.LogInformation("Cleanup finished: {N} deleted", 3);
            log.LogDebug("hidden detail");
            debug = true;
            log.LogDebug("shown detail");
            logs.CreateLogger("Microsoft.AspNetCore.Routing").LogInformation("framework chatter");
            log.LogError(new InvalidOperationException("boom"), "It failed");
            var filler = new string('x', 2000);
            for (var i = 0; i < 1200; i++)
            {
                log.LogInformation("{Filler}", filler);
            }

            await Task.Delay(1500);
        }

        var all = string.Join("\n", Directory.GetFiles(logDir).OrderBy(f => f).Select(File.ReadAllText));
        Expect(all.Contains("|Info|Engine|Cleanup finished: 3 deleted") && all.Contains("System.InvalidOperationException: boom"), "Log lines carry time, level and source; errors include the exception");
        Expect(!all.Contains("hidden detail") && all.Contains("shown detail") && !all.Contains("framework chatter"), "Debug detail only with Debug logging on; framework chatter stays out");
        Expect(File.Exists(Path.Combine(logDir, "clearr.1.txt")) && new FileInfo(Path.Combine(logDir, "clearr.txt")).Length <= 1024 * 1024 + 4096, "Log rotates at 1 MB");
        Expect(Directory.GetFiles(logDir).Length <= FileLoggerProvider.MaxFiles, "At most 5 log files are kept");
        Expect(FileLoggerProvider.IsLogFileName("clearr.txt") && FileLoggerProvider.IsLogFileName("clearr.4.txt") && !FileLoggerProvider.IsLogFileName("../settings.json") && !FileLoggerProvider.IsLogFileName("clearr.9.txt") && !FileLoggerProvider.IsLogFileName("api.key"), "Only Clearr's log files can be downloaded");

        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
        }

        return fails;
    }
}
