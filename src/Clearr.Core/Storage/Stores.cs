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

    public string Root { get; }

    public string File(string name) => Path.Combine(Root, name);
}

internal static class AtomicFile
{
    public static void Write(string path, string content)
    {
        var tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, content);
        System.IO.File.Move(tmp, path, overwrite: true);
    }
}

public class SettingsStore
{
    private readonly object _lock = new();
    private readonly string _path;
    private ClearrSettings? _cached;

    public SettingsStore(ConfigPaths paths) => _path = paths.File("settings.json");

    public ClearrSettings Get()
    {
        lock (_lock)
        {
            if (_cached != null)
            {
                return Clone(_cached);
            }

            _cached = System.IO.File.Exists(_path)
                ? JsonSerializer.Deserialize<ClearrSettings>(System.IO.File.ReadAllText(_path), Json.Options) ?? new()
                : new ClearrSettings();
            return Clone(_cached);
        }
    }

    public void Save(ClearrSettings s)
    {
        lock (_lock)
        {
            AtomicFile.Write(_path, JsonSerializer.Serialize(s, Json.Options));
            _cached = Clone(s);
        }
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
