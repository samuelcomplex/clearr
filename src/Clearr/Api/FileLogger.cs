using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Clearr.Storage;

namespace Clearr.Api;

/// <summary>
/// *arr-style log files: /config/logs/clearr.txt, rotated at 1 MB, keeping the last 5 files.
/// Written from a background thread so logging never slows a request down.
/// </summary>
public sealed partial class FileLoggerProvider : ILoggerProvider
{
    public const int MaxFiles = 5;
    private const long MaxBytes = 1024 * 1024;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);
    private readonly Thread _writer;
    private readonly Func<bool> _debug;

    public FileLoggerProvider(string directory, Func<bool> debug)
    {
        Directory = directory;
        _debug = debug;
        System.IO.Directory.CreateDirectory(directory);
        _writer = new Thread(Write) { IsBackground = true, Name = "Clearr log writer" };
        _writer.Start();
    }

    public string Directory { get; }

    public string CurrentFile => Path.Combine(Directory, "clearr.txt");

    /// <summary>clearr.txt, clearr.1.txt … clearr.4.txt: the only names the log endpoints will open.</summary>
    public static bool IsLogFileName(string name) => LogName().IsMatch(name);

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    internal bool Enabled(string category, LogLevel level)
    {
        if (level == LogLevel.None)
        {
            return false;
        }

        // Framework chatter stays out unless it's a problem; Clearr's own detail follows the Debug setting.
        var framework = category.StartsWith("Microsoft.", StringComparison.Ordinal) || category.StartsWith("System.", StringComparison.Ordinal);
        return framework ? level >= LogLevel.Warning : level >= (_debug() ? LogLevel.Debug : LogLevel.Information);
    }

    internal void Enqueue(string line) => _queue.TryAdd(line);

    [GeneratedRegex(@"^clearr(\.[1-4])?\.txt$")]
    private static partial Regex LogName();

    private void Write()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    if (File.Exists(CurrentFile) && new FileInfo(CurrentFile).Length > MaxBytes)
                    {
                        Rotate();
                    }

                    File.AppendAllText(CurrentFile, line, Encoding.UTF8);
                }
                catch (IOException)
                {
                    // Disk full or /config read-only: logging must never take Clearr down.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Rotate()
    {
        for (var i = MaxFiles - 1; i >= 1; i--)
        {
            var from = i == 1 ? CurrentFile : Path.Combine(Directory, $"clearr.{i - 1}.txt");
            var to = Path.Combine(Directory, $"clearr.{i}.txt");
            if (File.Exists(from))
            {
                File.Move(from, to, overwrite: true);
            }
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _owner;
        private readonly string _category;

        public FileLogger(FileLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category.StartsWith("Clearr.", StringComparison.Ordinal) ? category["Clearr.".Length..] : category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _owner.Enabled(_category, logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var level = logLevel switch
            {
                LogLevel.Trace => "Trace",
                LogLevel.Debug => "Debug",
                LogLevel.Information => "Info",
                LogLevel.Warning => "Warn",
                LogLevel.Error => "Error",
                _ => "Fatal",
            };
            var sb = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append('|').Append(level)
                .Append('|').Append(_category)
                .Append('|').Append(formatter(state, exception).ReplaceLineEndings(" "))
                .Append('\n');
            if (exception != null)
            {
                sb.Append(exception).Append('\n');
            }

            _owner.Enqueue(sb.ToString());
        }
    }
}

/// <summary>Reads the log files back for the Logs page.</summary>
public static class LogFiles
{
    public static object List(FileLoggerProvider logs, int lines)
    {
        var files = System.IO.Directory.Exists(logs.Directory)
            ? new DirectoryInfo(logs.Directory).GetFiles("clearr*.txt")
                .Where(f => FileLoggerProvider.IsLogFileName(f.Name))
                .OrderBy(f => f.Name == "clearr.txt" ? 0 : 1).ThenBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => new { name = f.Name, size = f.Length, modified = f.LastWriteTimeUtc })
                .ToList<object>()
            : new List<object>();
        return new { files, lines = Tail(logs.CurrentFile, Math.Clamp(lines, 1, 5000)) };
    }

    private static List<string> Tail(string path, int count)
    {
        if (!File.Exists(path))
        {
            return new();
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        var q = new Queue<string>(count);
        while (reader.ReadLine() is { } line)
        {
            if (q.Count == count)
            {
                q.Dequeue();
            }

            q.Enqueue(line);
        }

        return q.Reverse().ToList();
    }
}
