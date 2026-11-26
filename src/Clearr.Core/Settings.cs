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

public class HttpsSettings
{
    public bool Enabled { get; set; }

    /// <summary>Port inside the container for HTTPS (HTTP stays on 8585).</summary>
    public int Port { get; set; } = 8586;

    /// <summary>.pfx/.p12, or a PEM certificate (.crt/.pem). Empty = self-signed certificate made by Clearr.</summary>
    public string CertificatePath { get; set; } = "";

    /// <summary>PEM private key (.key/.pem). Not needed for .pfx or when the PEM file also holds the key.</summary>
    public string KeyPath { get; set; } = "";

    /// <summary>Password for a .pfx (or an encrypted PEM key).</summary>
    public string CertificatePassword { get; set; } = "";
}

public class ClearrSettings
{
    /// <summary>Bump when the settings format changes, and add a step to <see cref="Storage.SettingsMigrations"/>.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Format version of the saved file (0 = written before versioning existed).</summary>
    public int SchemaVersion { get; set; } = CurrentSchema;

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
    public bool IncludeSpecials { get; set; }
    public int GraceDays { get; set; } = 7;
    public int MaxDeleteGbPerRun { get; set; } = 50;
    public string KeepTag { get; set; } = "keep";
    /// <summary>Local time of day for the daily run, "HH:mm".</summary>
    public string ScheduleTime { get; set; } = "04:00";
    public bool RefreshJellyfinAfterCleanup { get; set; } = true;

    /// <summary>App only: also write Debug-level detail (every decision) to the log files.</summary>
    public bool DebugLogging { get; set; }

    /// <summary>
    /// Plugin only: items Sonarr/Radarr don't manage may be deleted directly through Jellyfin.
    /// Ignored by the Clearr app, which only ever removes through Sonarr/Radarr.
    /// </summary>
    public bool AllowDirectDelete { get; set; }

    // ---- Flags ----
    public int InactiveDays { get; set; } = 30;
    public int SampledMaxEpisodes { get; set; } = 3;
    public bool AbandonedUsersBlockCleanup { get; set; } = true;

    // ---- Users ----
    public List<string> ExcludedUserIds { get; set; } = new();

    // ---- HTTPS ----
    public HttpsSettings Https { get; set; } = new();

    // ---- Security ----
    /// <summary>PBKDF2 hash of the UI password; empty = no password.</summary>
    public string PasswordHash { get; set; } = "";
}
