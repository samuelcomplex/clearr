namespace Clearr.Core;

// ---------- Library snapshot (what Clearr reads from Jellyfin, plus *arr info) ----------

public record MediaUser(string Id, string Name);

public record PlayState(bool Played, long PositionTicks, DateTime? LastPlayed);

public class EpisodeInfo
{
    public string Id { get; set; } = "";
    public int? Season { get; set; }
    public int? Number { get; set; }
}

public class SeasonInfo
{
    public string Id { get; set; } = "";
    public int? Number { get; set; }
    public string Name { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public List<EpisodeInfo> Episodes { get; set; } = new();
}

public class ShowInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"Continuing", "Ended" or null.</summary>
    public string? Status { get; set; }
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SeasonInfo> Seasons { get; set; } = new();

    // Filled from Sonarr
    public int? ArrId { get; set; }
    public bool ArrKeep { get; set; }
    public string? ArrRootFolder { get; set; }
}

public class MovieInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int? Year { get; set; }
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Filled from Radarr
    public int? ArrId { get; set; }
    public bool ArrKeep { get; set; }
    public string? ArrRootFolder { get; set; }
    public bool ArrHasFile { get; set; }
}

public class LibrarySnapshot
{
    public List<MediaUser> Users { get; set; } = new();
    public List<ShowInfo> Shows { get; set; } = new();
    public List<MovieInfo> Movies { get; set; } = new();

    /// <summary>itemId → userId → play state. Missing entry = never touched.</summary>
    public Dictionary<string, Dictionary<string, PlayState>> UserData { get; set; } = new();

    /// <summary>itemId (show, season or movie) → ids of the users who marked it as a favourite.</summary>
    public Dictionary<string, HashSet<string>> Favorites { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void AddFavorite(string itemId, string userId)
    {
        if (!Favorites.TryGetValue(itemId, out var users))
        {
            Favorites[itemId] = users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        users.Add(userId);
    }

    /// <summary>Whether Sonarr / Radarr were reachable and indexed for this snapshot.</summary>
    public bool SonarrConnected { get; set; }
    public bool RadarrConnected { get; set; }
    public List<string> Warnings { get; set; } = new();

    public PlayState? Get(string itemId, string userId) =>
        UserData.TryGetValue(itemId, out var byUser) && byUser.TryGetValue(userId, out var s) ? s : null;
}

// ---------- Report (what the UI shows and cleanup acts on) ----------

public static class WatchStatus
{
    public const string NotStarted = "NotStarted";
    public const string InProgress = "InProgress";
    public const string Finished = "Finished";
}

public static class WatchFlag
{
    public const string Sampled = "Sampled";
    public const string Stalled = "Stalled";
    public const string MidEpisode = "MidEpisode";
}

public class UserProgress
{
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Status { get; set; } = WatchStatus.NotStarted;
    public int Watched { get; set; }
    public int Partial { get; set; }
    public int Total { get; set; }
    public DateTime? LastActivity { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? LastEpisode { get; set; }
    public List<string> Flags { get; set; } = new();
    public bool IsAbandoned => Flags.Contains(WatchFlag.Sampled) || Flags.Contains(WatchFlag.Stalled);
}

public class SeasonReport
{
    public string SeasonId { get; set; } = "";
    public string Name { get; set; } = "";
    public int? Number { get; set; }
    public int EpisodeCount { get; set; }
    public bool Kept { get; set; }
    public string? KeptBy { get; set; }
    public List<UserProgress> Users { get; set; } = new();
    public bool Eligible { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>The reason as a translatable message (Reason is its English text).</summary>
    public Msg? ReasonMsg { get; set; }
}

public class ShowReport
{
    public string ShowId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Status { get; set; }
    public int EpisodeCount { get; set; }
    public bool Kept { get; set; }
    public string? KeptBy { get; set; }
    public bool Managed { get; set; }
    public int? ArrId { get; set; }
    /// <summary>How it would be removed: "Sonarr", "Jellyfin" (plugin fallback) or null (can't be).</summary>
    public string? Via { get; set; }
    public string? TvdbId { get; set; }
    public List<UserProgress> Users { get; set; } = new();
    public List<SeasonReport> Seasons { get; set; } = new();
    public bool Eligible { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>The reason as a translatable message (Reason is its English text).</summary>
    public Msg? ReasonMsg { get; set; }
}

public class MovieReport
{
    public string MovieId { get; set; } = "";
    public string Name { get; set; } = "";
    public int? Year { get; set; }
    public bool Kept { get; set; }
    public string? KeptBy { get; set; }
    public bool Managed { get; set; }
    public int? ArrId { get; set; }
    /// <summary>How it would be removed: "Radarr", "Jellyfin" (plugin fallback) or null (can't be).</summary>
    public string? Via { get; set; }
    public List<UserProgress> Users { get; set; } = new();
    public bool Eligible { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>The reason as a translatable message (Reason is its English text).</summary>
    public Msg? ReasonMsg { get; set; }
}

public class WatchReport
{
    public DateTime GeneratedAt { get; set; }
    public List<string> TrackedUsers { get; set; } = new();
    public bool SonarrConnected { get; set; }
    public bool RadarrConnected { get; set; }
    /// <summary>True when unmanaged items may be removed through Jellyfin (plugin with the fallback on).</summary>
    public bool DirectDelete { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<ShowReport> Shows { get; set; } = new();
    public List<MovieReport> Movies { get; set; } = new();
}

// ---------- Cleanup ----------

public class HistoryEntry
{
    public DateTime Date { get; set; }
    /// <summary>Deleted, WouldDelete, Failed, Skipped.</summary>
    public string Action { get; set; } = "";
    /// <summary>Show, Season, Movie.</summary>
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string? SeasonName { get; set; }
    public string Detail { get; set; } = "";

    /// <summary>The detail as translatable parts, joined with " · " (Detail is their English text). Empty in entries from before 0.2.</summary>
    public List<Msg> Parts { get; set; } = new();
    public long Bytes { get; set; }
}

public class CleanupResult
{
    public bool DryRun { get; set; }
    public long TotalBytes { get; set; }
    public string? Error { get; set; }

    /// <summary>The error as a translatable message, when Clearr itself refused (Error is its English text).</summary>
    public Msg? ErrorMsg { get; set; }
    public List<HistoryEntry> Actions { get; set; } = new();
}
