using System.IO;

namespace UpsTools.Services;

public enum LogLevel
{
    Info,
    Event,
    Warning,
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

/// <summary>Thread-safe log: keeps recent entries in memory for the dashboard and appends to a file.</summary>
public sealed class AppLog
{
    private const int MaxEntries = 200;
    private const long MaxFileBytes = 1_000_000;

    private readonly object _lock = new();
    private readonly LinkedList<LogEntry> _recent = new();

    public static string FilePath { get; } = Path.Combine(SettingsStore.Folder, "ups-tools.log");

    /// <summary>Raised on the thread that logged.</summary>
    public event Action<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> Recent
    {
        get { lock (_lock) return [.. _recent]; }
    }

    public void Info(string message) => Add(LogLevel.Info, message);
    public void Event(string message) => Add(LogLevel.Event, message);
    public void Warn(string message) => Add(LogLevel.Warning, message);

    private void Add(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (_lock)
        {
            _recent.AddLast(entry);
            if (_recent.Count > MaxEntries) _recent.RemoveFirst();
            WriteToFile(entry);
        }
        EntryAdded?.Invoke(entry);
    }

    private static void WriteToFile(LogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            var file = new FileInfo(FilePath);
            if (file.Exists && file.Length > MaxFileBytes)
                File.Move(FilePath, FilePath + ".1", overwrite: true);
            File.AppendAllText(FilePath, $"{entry.Time:yyyy-MM-dd HH:mm:ss} [{entry.Level}] {entry.Message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
