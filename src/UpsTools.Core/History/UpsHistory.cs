using System.Globalization;
using System.Text;

namespace UpsTools.Core.History;

/// <summary>A minute of readings: whether the grid was there all along, the last charge, and the average load.</summary>
/// <param name="Grid">False if the grid was gone at any reading of the minute; null when the UPS doesn't say.</param>
public sealed record HistoryPoint(DateTimeOffset Time, bool? Grid, int? BatteryPercent, double? LoadW);

public sealed record HistoryEvent(DateTimeOffset Time, UpsEventKind Kind);

/// <summary>
/// What a UPS did lately, for the History chart: a point a minute and its events, kept <see cref="Keep"/> in a text
/// file next to the settings. Each device records what it sees, also of a UPS read through another one.
/// </summary>
/// <remarks>
/// The file is a line per record, appended as it comes: <c>p &lt;unix seconds&gt; &lt;grid 1/0/-&gt; &lt;battery %/-&gt;
/// &lt;load W/-&gt;</c> for a point, <c>e &lt;unix seconds&gt; &lt;event&gt;</c> for an event. A minute's point is written once
/// the minute is over (the readings of a minute are folded into one), so a crash loses a minute at most. Older records
/// are dropped when it is loaded, and once a day after.
/// </remarks>
public sealed class UpsHistory
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(7);

    private const string Header = "# UPS Tools history 1";

    private readonly Lock _lock = new();
    private readonly string? _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly List<HistoryPoint> _points = [];
    private readonly List<HistoryEvent> _events = [];
    private DateTimeOffset _prunedAt;

    /// <summary>The minute being recorded: its point so far, and how many readings make its load.</summary>
    private (HistoryPoint Point, int Loads)? _pending;

    /// <param name="path">The file; null keeps it in memory only.</param>
    /// <param name="clock">Now; the system clock unless a test says otherwise.</param>
    public UpsHistory(string? path, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Load();
    }

    /// <summary>A point, or an event, was recorded. Raised on the thread that recorded it.</summary>
    public event Action? Changed;

    /// <summary>The time a reading belongs to: the start of its minute.</summary>
    private static DateTimeOffset MinuteOf(DateTimeOffset time) =>
        DateTimeOffset.FromUnixTimeSeconds(time.ToUnixTimeSeconds() / 60 * 60);

    public void Record(UpsStatus status)
    {
        var minute = MinuteOf(status.Timestamp);
        var load = status.TotalOutputW;
        lock (_lock)
        {
            if (_pending is { } pending && pending.Point.Time == minute)
            {
                var p = pending.Point;
                var loads = pending.Loads + (load is null ? 0 : 1);
                var average = load is not { } w ? p.LoadW
                    : p.LoadW is { } before ? before + (w - before) / loads
                    : w;
                _pending = (new HistoryPoint(minute, p.Grid == false || status.GridPresent == false ? false : status.GridPresent ?? p.Grid,
                    status.BatteryPercent ?? p.BatteryPercent, average), loads);
            }
            else
            {
                if (_pending is { } done && done.Point.Time < minute) Close(done.Point);
                _pending = (new HistoryPoint(minute, status.GridPresent, status.BatteryPercent, load), load is null ? 0 : 1);
            }
        }
        Changed?.Invoke();
    }

    public void Record(UpsEventKind kind, DateTimeOffset? at = null)
    {
        var e = new HistoryEvent(DateTimeOffset.FromUnixTimeSeconds((at ?? _clock()).ToUnixTimeSeconds()), kind);
        lock (_lock)
        {
            _events.Add(e);
            Append(Line(e));
        }
        Changed?.Invoke();
    }

    /// <summary>Writes the minute being recorded, e.g. before the app exits.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_pending is { } pending) Close(pending.Point);
            _pending = null;
        }
    }

    /// <summary>The points from <paramref name="from"/> on, the minute being recorded included, oldest first.</summary>
    public IReadOnlyList<HistoryPoint> Points(DateTimeOffset from)
    {
        lock (_lock)
        {
            var points = _points.Where(p => p.Time >= from).ToList();
            if (_pending is { } pending && pending.Point.Time >= from) points.Add(pending.Point);
            return points;
        }
    }

    public IReadOnlyList<HistoryEvent> Events(DateTimeOffset from)
    {
        lock (_lock) return [.. _events.Where(e => e.Time >= from)];
    }

    /// <summary>Forgets it all, the file too (the UPS was forgotten).</summary>
    public void Delete()
    {
        lock (_lock)
        {
            _points.Clear();
            _events.Clear();
            _pending = null;
            if (_path is null) return;
            try { File.Delete(_path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }

    // ---- the file ----

    private void Close(HistoryPoint point)
    {
        _points.Add(point);
        Append(Line(point));
        if (_clock() - _prunedAt > TimeSpan.FromDays(1)) Prune();
    }

    private static string Line(HistoryPoint p)
    {
        var grid = p.Grid switch { true => "1", false => "0", null => "-" };
        var battery = p.BatteryPercent?.ToString(CultureInfo.InvariantCulture) ?? "-";
        var load = p.LoadW is { } w ? Math.Round(w, 1).ToString("0.#", CultureInfo.InvariantCulture) : "-";
        return $"p {p.Time.ToUnixTimeSeconds()} {grid} {battery} {load}";
    }

    private static string Line(HistoryEvent e) => $"e {e.Time.ToUnixTimeSeconds()} {e.Kind}";

    private void Append(string line)
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (!File.Exists(_path)) File.WriteAllText(_path, Header + "\n");
            File.AppendAllText(_path, line + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The chart misses it; monitoring goes on.
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            foreach (var line in File.ReadLines(_path))
            {
                var parts = line.Split(' ');
                if (parts.Length < 3 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)) continue;
                var time = DateTimeOffset.FromUnixTimeSeconds(seconds);
                if (parts[0] == "p" && parts.Length >= 5)
                {
                    bool? grid = parts[2] switch { "1" => true, "0" => false, _ => null };
                    int? battery = int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : null;
                    double? load = double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : null;
                    _points.Add(new HistoryPoint(time, grid, battery, load));
                }
                else if (parts[0] == "e" && Enum.TryParse<UpsEventKind>(parts[2], out var kind))
                {
                    _events.Add(new HistoryEvent(time, kind));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: the chart starts afresh.
        }
        _points.Sort((a, b) => a.Time.CompareTo(b.Time));
        _events.Sort((a, b) => a.Time.CompareTo(b.Time));
        Prune();
    }

    /// <summary>Drops what is older than <see cref="Keep"/>, and rewrites the file without it.</summary>
    private void Prune()
    {
        _prunedAt = _clock();
        var from = _prunedAt - Keep;
        var dropped = _points.RemoveAll(p => p.Time < from) + _events.RemoveAll(e => e.Time < from);
        if (_path is null || dropped == 0) return;
        try
        {
            var text = new StringBuilder(Header).Append('\n');
            foreach (var line in _points.Select(p => (p.Time, Line: Line(p))).Concat(_events.Select(e => (e.Time, Line: Line(e))))
                         .OrderBy(r => r.Time))
                text.Append(line.Line).Append('\n');
            var temp = _path + ".tmp";
            File.WriteAllText(temp, text.ToString());
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again tomorrow.
        }
    }
}
