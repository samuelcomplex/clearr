namespace Clearr.Core;

public enum CleanupMode
{
    Off = 0,
    DryRun = 1,
    Delete = 2
}

public class ConnectionSettings
{
    public string Url { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);
}

public class ClearrSettings
{
    // ---- Connections ----
    public ConnectionSettings Jellyfin { get; set; } = new();
    public ConnectionSettings Sonarr { get; set; } = new();
    public ConnectionSettings Radarr { get; set; } = new();

    // ---- Cleanup ----
    public CleanupMode Mode { get; set; } = CleanupMode.DryRun;
    public bool DeleteSeasons { get; set; } = true;
    public bool DeleteShows { get; set; } = true;
    public bool DeleteMovies { get; set; } = true;
    public bool RequireEndedForShowDelete { get; set; } = true;
    public bool ProtectLatestSeasonOfContinuing { get; set; } = true;
    public bool IncludeSpecials { get; set; } = false;
    public int GraceDays { get; set; } = 7;
    public int MaxDeleteGbPerRun { get; set; } = 50;
    public string KeepTag { get; set; } = "keep";
    /// <summary>Local time of day for the daily run, "HH:mm".</summary>
    public string ScheduleTime { get; set; } = "04:00";
    public bool RefreshJellyfinAfterCleanup { get; set; } = true;

    // ---- Flags ----
    public int InactiveDays { get; set; } = 30;
    public int SampledMaxEpisodes { get; set; } = 3;
    public bool AbandonedUsersBlockCleanup { get; set; } = true;

    // ---- Users ----
    public List<string> ExcludedUserIds { get; set; } = new();

    // ---- Security ----
    /// <summary>PBKDF2 hash of the UI password; empty = no password.</summary>
    public string PasswordHash { get; set; } = "";
}
