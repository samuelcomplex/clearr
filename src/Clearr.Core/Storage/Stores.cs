using System.Text.Json;
using System.Text.Json.Serialization;
using Clearr.Core;

namespace Clearr.Storage;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>Where Clearr keeps its files: $CLEARR_CONFIG_DIR, else /config, else ./config.</summary>
public class ConfigPaths
{
    public ConfigPaths()
    {
        var env = Environment.GetEnvironmentVariable("CLEARR_CONFIG_DIR");
        Root = !string.IsNullOrWhiteSpace(env) ? env : Directory.Exists("/config") ? "/config" : Path.Combine(AppContext.BaseDirectory, "config");
        Directory.CreateDirectory(Root);
    }

    public ConfigPaths(string root)
    {
        Root = root;
        Directory.CreateDirectory(Root);
    }

    /// <summary>Files holding API keys, the password hash or session secrets.</summary>
    public static readonly string[] SecretFiles = { "settings.json", "api.key", "session.key", "clearr-selfsigned.pfx" };

    public void RestrictSecrets()
    {
        foreach (var f in SecretFiles)
        {
            SecureFile.Restrict(File(f));
        }
    }

    public string Root { get; }

    public string File(string name) => Path.Combine(Root, name);
}

/// <summary>Writes files only Clearr's own user can read (they hold API keys and secrets).</summary>
public static class SecureFile
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void WriteAllText(string path, string content) => WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(content));

    public static void WriteAllBytes(string path, byte[] content)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnly;
        }

        using (var f = new FileStream(path, options))
        {
            f.Write(content);
        }

        Restrict(path);
    }

    /// <summary>Tightens a file written before this existed (older versions used the default 644).</summary>
    public static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows() && System.IO.File.Exists(path) && System.IO.File.GetUnixFileMode(path) != OwnerOnly)
        {
            System.IO.File.SetUnixFileMode(path, OwnerOnly);
        }
    }
}

internal static class AtomicFile
{
    public static void Write(string path, string content)
    {
        var tmp = path + ".tmp";
        SecureFile.WriteAllText(tmp, content);
        System.IO.File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// Upgrades settings saved by older versions, one step at a time.
/// To change the format: bump <see cref="ClearrSettings.CurrentSchema"/> and add the step that turns version N-1 into N.
/// </summary>
public static class SettingsMigrations
{
    private static readonly Dictionary<int, Action<System.Text.Json.Nodes.JsonObject>> Steps = new()
    {
        // 0 -> 1: versioning introduced; the format itself didn't change.
        [1] = _ => { },
    };

    public static void Upgrade(System.Text.Json.Nodes.JsonObject json, int from)
    {
        for (var v = from + 1; v <= ClearrSettings.CurrentSchema; v++)
        {
            Steps[v](json);
            json["schemaVersion"] = v;
        }
    }
}

public class SettingsStore
{
    private readonly object _lock = new();
    private readonly ConfigPaths _paths;
    private readonly string _path;
    private ClearrSettings? _cached;

    public SettingsStore(ConfigPaths paths)
    {
        _paths = paths;
        _path = paths.File("settings.json");
    }

    /// <summary>Something worth telling the user about the last load (an upgrade, a broken file).</summary>
    public string? Notice { get; private set; }

    public ClearrSettings Get()
    {
        lock (_lock)
        {
            _cached ??= Load();
            return Clone(_cached);
        }
    }

    public void Save(ClearrSettings s)
    {
        lock (_lock)
        {
            s.SchemaVersion = ClearrSettings.CurrentSchema;
            AtomicFile.Write(_path, JsonSerializer.Serialize(s, Json.Options));
            _cached = Clone(s);
        }
    }

    private ClearrSettings Load()
    {
        if (!System.IO.File.Exists(_path))
        {
            return new ClearrSettings();
        }

        var text = System.IO.File.ReadAllText(_path);
        System.Text.Json.Nodes.JsonObject json;
        try
        {
            json = System.Text.Json.Nodes.JsonNode.Parse(text) as System.Text.Json.Nodes.JsonObject ?? throw new JsonException("not a JSON object");
        }
        catch (JsonException ex)
        {
            // Never crash-loop on a damaged file: keep it for inspection and start from the (safe, Dry run) defaults.
            var broken = _paths.File($"settings.broken-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            System.IO.File.Move(_path, broken);
            SecureFile.Restrict(broken);
            Notice = $"settings.json couldn't be read ({ex.Message}); it was moved to {Path.GetFileName(broken)} and Clearr started with default settings (Dry run).";
            return new ClearrSettings();
        }

        var version = json.TryGetPropertyValue("schemaVersion", out var v) && v is not null ? v.GetValue<int>() : 0;
        if (version < ClearrSettings.CurrentSchema)
        {
            var backup = _paths.File($"settings.v{version}.bak.json");
            if (!System.IO.File.Exists(backup))
            {
                SecureFile.WriteAllText(backup, text);
            }

            SettingsMigrations.Upgrade(json, version);
            var upgraded = json.Deserialize<ClearrSettings>(Json.Options) ?? new();
            AtomicFile.Write(_path, JsonSerializer.Serialize(upgraded, Json.Options));
            Notice = $"Settings upgraded from format {version} to {ClearrSettings.CurrentSchema}; the old file is kept as {Path.GetFileName(backup)}.";
            return upgraded;
        }

        if (version > ClearrSettings.CurrentSchema)
        {
            // Written by a newer Clearr (downgrade): keep a copy, since saving here would drop the newer fields.
            var backup = _paths.File($"settings.v{version}.bak.json");
            if (!System.IO.File.Exists(backup))
            {
                SecureFile.WriteAllText(backup, text);
            }

            Notice = $"These settings were saved by a newer Clearr (format {version}); a copy is kept as {Path.GetFileName(backup)}.";
        }

        return json.Deserialize<ClearrSettings>(Json.Options) ?? new();
    }

    private static ClearrSettings Clone(ClearrSettings s) =>
        JsonSerializer.Deserialize<ClearrSettings>(JsonSerializer.Serialize(s, Json.Options), Json.Options)!;
}

/// <summary>Items (show, season or movie ids) marked Keep in Clearr.</summary>
public class KeepStore : IKeepStore
{
    private readonly object _lock = new();
    private readonly string _path;
    private HashSet<string>? _ids;

    public KeepStore(ConfigPaths paths) => _path = paths.File("keep.json");

    public bool Contains(string id)
    {
        lock (_lock)
        {
            return Load().Contains(id);
        }
    }

    public void Set(string id, bool keep)
    {
        lock (_lock)
        {
            var ids = Load();
            if (keep ? ids.Add(id) : ids.Remove(id))
            {
                AtomicFile.Write(_path, JsonSerializer.Serialize(ids.OrderBy(x => x).ToList(), Json.Options));
            }
        }
    }

    private HashSet<string> Load()
    {
        if (_ids == null)
        {
            _ids = System.IO.File.Exists(_path)
                ? new HashSet<string>(JsonSerializer.Deserialize<List<string>>(System.IO.File.ReadAllText(_path), Json.Options) ?? new(), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return _ids;
    }
}

public class HistoryStore : IHistoryStore
{
    private const int MaxEntries = 2000;
    private readonly object _lock = new();
    private readonly string _path;

    public HistoryStore(ConfigPaths paths) => _path = paths.File("history.json");

    public List<HistoryEntry> GetAll()
    {
        lock (_lock)
        {
            return Load();
        }
    }

    public void Add(IEnumerable<HistoryEntry> entries)
    {
        lock (_lock)
        {
            var list = Load();
            list.InsertRange(0, entries);
            if (list.Count > MaxEntries)
            {
                list = list.Take(MaxEntries).ToList();
            }

            AtomicFile.Write(_path, JsonSerializer.Serialize(list, Json.Options));
        }
    }

    private List<HistoryEntry> Load() =>
        System.IO.File.Exists(_path)
            ? JsonSerializer.Deserialize<List<HistoryEntry>>(System.IO.File.ReadAllText(_path), Json.Options) ?? new()
            : new();
}
