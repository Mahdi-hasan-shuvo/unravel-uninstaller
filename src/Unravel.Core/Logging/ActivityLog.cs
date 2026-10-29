using System.Text.Json;
using System.Text.Json.Serialization;

namespace Unravel.Core.Logging;

public enum LogLevel { Info, Action, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Area, string Message, string? Detail = null);

/// <summary>
/// Append-only activity log (one JSON object per line, one file per day) for every change Unravel makes
/// and every failure it meets. Kept in memory too, so the UI can show recent entries without re-reading files.
/// </summary>
public static class ActivityLog
{
    private static readonly object Gate = new();
    private static readonly LinkedList<LogEntry> RecentEntries = new();
    private const int RecentCapacity = 2000;
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public static string Folder { get; private set; } = Path.Combine(Settings.SettingsStore.DefaultFolder, "Logs");

    public static event Action<LogEntry>? EntryAdded;

    /// <summary>Redirects the log (tests).</summary>
    public static void UseFolder(string folder)
    {
        lock (Gate) { Folder = folder; RecentEntries.Clear(); }
    }

    public static string FileFor(DateTime day) => Path.Combine(Folder, $"unravel-{day:yyyyMMdd}.log");

    public static void Info(string area, string message, string? detail = null) => Write(LogLevel.Info, area, message, detail);
    public static void Action(string area, string message, string? detail = null) => Write(LogLevel.Action, area, message, detail);
    public static void Warning(string area, string message, string? detail = null) => Write(LogLevel.Warning, area, message, detail);
    public static void Error(string area, string message, string? detail = null) => Write(LogLevel.Error, area, message, detail);

    public static void Write(LogLevel level, string area, string message, string? detail = null)
    {
        var entry = new LogEntry(DateTime.Now, level, area, message, detail);
        lock (Gate)
        {
            RecentEntries.AddLast(entry);
            while (RecentEntries.Count > RecentCapacity) RecentEntries.RemoveFirst();
            try
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(FileFor(entry.Time), JsonSerializer.Serialize(entry, Json) + Environment.NewLine);
            }
            catch { /* logging must never break an operation */ }
        }
        try { EntryAdded?.Invoke(entry); } catch { }
    }

    public static IReadOnlyList<LogEntry> Recent
    {
        get { lock (Gate) return RecentEntries.ToList(); }
    }

    public static List<LogEntry> ReadDay(DateTime day)
    {
        var file = FileFor(day);
        var list = new List<LogEntry>();
        if (!File.Exists(file)) return list;
        lock (Gate)
        {
            foreach (var line in File.ReadLines(file))
            {
                try { if (JsonSerializer.Deserialize<LogEntry>(line, Json) is { } e) list.Add(e); } catch { }
            }
        }
        return list;
    }

    public static List<DateTime> AvailableDays()
    {
        if (!Directory.Exists(Folder)) return [];
        return Directory.GetFiles(Folder, "unravel-*.log")
            .Select(f => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f)[8..], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d) ? d : (DateTime?)null)
            .Where(d => d is not null).Select(d => d!.Value).OrderByDescending(d => d).ToList();
    }
}
