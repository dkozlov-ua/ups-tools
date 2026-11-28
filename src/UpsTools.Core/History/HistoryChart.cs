using System.Globalization;

namespace UpsTools.Core.History;

/// <summary>A point of the chart: X across the range (0 its start, 1 its end), Y up the plot (0 the bottom, 1 the top).</summary>
public readonly record struct ChartPoint(double X, double Y);

/// <summary>A label on an axis, at X (time) or Y (value), 0 to 1.</summary>
public readonly record struct ChartTick(double At, string Label);

/// <summary>An event on the chart, at X.</summary>
public readonly record struct ChartEvent(double X, UpsEventKind Kind);

/// <summary>A stretch of time, from X to X.</summary>
public readonly record struct ChartSpan(double From, double To);

/// <summary>
/// What the History chart shows of a UPS over a range of time, worked out here and drawn by each app with its own
/// graphics: the charge (0–100 %) and the load (0 to <see cref="LoadMaxW"/>) as lines, broken where readings are
/// missing; the outages as spans; the events as marks; and the axes' labels.
/// </summary>
public sealed record HistoryChart(
    IReadOnlyList<IReadOnlyList<ChartPoint>> Battery,
    IReadOnlyList<IReadOnlyList<ChartPoint>> Load,
    double LoadMaxW,
    IReadOnlyList<ChartSpan> Outages,
    IReadOnlyList<ChartSpan> Missing,
    IReadOnlyList<ChartEvent> Events,
    IReadOnlyList<ChartTick> TimeTicks,
    IReadOnlyList<ChartTick> BatteryTicks,
    IReadOnlyList<ChartTick> LoadTicks)
{
    /// <summary>The ranges the apps offer.</summary>
    public static IReadOnlyList<(string Label, TimeSpan Span)> Ranges { get; } =
        [("6 h", TimeSpan.FromHours(6)), ("24 h", TimeSpan.FromHours(24)), ("7 d", TimeSpan.FromDays(7))];

    /// <summary>The most points a line gets: more would be finer than any screen shows.</summary>
    internal const int MaxPoints = 720;

    /// <summary>Nothing recorded in the range.</summary>
    public bool IsEmpty => Battery.Count == 0 && Load.Count == 0;

    /// <param name="points">The points, oldest first (<see cref="UpsHistory.Points"/>).</param>
    /// <param name="zone">Where the time labels are read; the local zone unless a test says otherwise.</param>
    public static HistoryChart Build(IReadOnlyList<HistoryPoint> points, IReadOnlyList<HistoryEvent> events,
        DateTimeOffset from, DateTimeOffset to, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        zone ??= TimeZoneInfo.Local;
        culture ??= CultureInfo.CurrentCulture;
        var span = (to - from).TotalSeconds;
        double X(DateTimeOffset t) => Math.Clamp((t - from).TotalSeconds / span, 0, 1);

        var inRange = points.Where(p => p.Time >= from && p.Time <= to).ToList();
        var merged = Merge(inRange, from, to - from);
        var gap = GapAfter(merged);

        var loadMax = NiceCeiling(Math.Max(10, merged.Max(p => p.LoadW) ?? 0));
        var battery = Lines(merged, gap, p => p.BatteryPercent / 100.0, X);
        var load = Lines(merged, gap, p => p.LoadW / loadMax, X);

        // Outages: from a point without the grid to the next one with it (or the end of the readings).
        var outages = new List<ChartSpan>();
        DateTimeOffset? outageFrom = null;
        for (var i = 0; i < merged.Count; i++)
        {
            var p = merged[i];
            if (p.Grid == false) outageFrom ??= p.Time;
            var ends = p.Grid == true || i == merged.Count - 1 || merged[i + 1].Time - p.Time > gap;
            if (outageFrom is { } start && ends)
            {
                var end = p.Grid == true ? p.Time : p.Time + Step(merged, i);
                outages.Add(new ChartSpan(X(start), X(end)));
                outageFrom = null;
            }
        }

        // Missing: stretches without readings, in the range's middle (not before the first or after the last).
        var missing = new List<ChartSpan>();
        for (var i = 1; i < merged.Count; i++)
            if (merged[i].Time - merged[i - 1].Time > gap)
                missing.Add(new ChartSpan(X(merged[i - 1].Time), X(merged[i].Time)));

        return new HistoryChart(battery, load, loadMax, outages, missing,
            [.. events.Where(e => e.Time >= from && e.Time <= to).Select(e => new ChartEvent(X(e.Time), e.Kind))],
            TimeLabels(from, to, zone, culture, X),
            [new(0, "0 %"), new(0.5, "50 %"), new(1, "100 %")],
            [new(0, "0 W"), new(0.5, UpsFormat.Watts(loadMax / 2)), new(1, UpsFormat.Watts(loadMax))]);
    }

    /// <summary>
    /// Fewer points for a long range: those of each slice of it in one (the grid gone if it was at any, the last
    /// charge, the average load).
    /// </summary>
    private static List<HistoryPoint> Merge(List<HistoryPoint> points, DateTimeOffset from, TimeSpan range)
    {
        if (points.Count <= MaxPoints) return points;
        var slice = range / MaxPoints;
        return [.. points.GroupBy(p => Math.Min(MaxPoints - 1, (p.Time - from).Ticks / slice.Ticks)).Select(g => new HistoryPoint(
            g.First().Time,
            g.Any(p => p.Grid == false) ? false : g.LastOrDefault(p => p.Grid is not null)?.Grid,
            g.LastOrDefault(p => p.BatteryPercent is not null)?.BatteryPercent,
            g.Any(p => p.LoadW is not null) ? g.Where(p => p.LoadW is not null).Average(p => p.LoadW) : null))];
    }

    /// <summary>
    /// How long without a point is a stretch without readings: three times the usual time between points (the poll
    /// period, which can be up to 10 minutes), and 5 minutes at least.
    /// </summary>
    private static TimeSpan GapAfter(List<HistoryPoint> points)
    {
        if (points.Count < 3) return TimeSpan.FromMinutes(15);
        var steps = points.Zip(points.Skip(1), (a, b) => b.Time - a.Time).Order().ToList();
        return TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(5).Ticks, steps[steps.Count / 2].Ticks * 3));
    }

    /// <summary>How long the point at <paramref name="i"/> stands for: up to the next one, or as long as the one before.</summary>
    private static TimeSpan Step(List<HistoryPoint> points, int i) =>
        i + 1 < points.Count ? points[i + 1].Time - points[i].Time
        : i > 0 ? points[i].Time - points[i - 1].Time
        : TimeSpan.FromMinutes(1);

    private static List<IReadOnlyList<ChartPoint>> Lines(List<HistoryPoint> points, TimeSpan gap, Func<HistoryPoint, double?> y,
        Func<DateTimeOffset, double> x)
    {
        var lines = new List<IReadOnlyList<ChartPoint>>();
        List<ChartPoint>? line = null;
        DateTimeOffset? last = null;
        foreach (var p in points)
        {
            if (y(p) is null || (last is { } l && p.Time - l > gap))
            {
                if (line is { Count: > 0 }) lines.Add(line);
                line = null;
                if (y(p) is null) continue;
            }
            (line ??= []).Add(new ChartPoint(x(p.Time), Math.Clamp(y(p)!.Value, 0, 1)));
            last = p.Time;
        }
        if (line is { Count: > 0 }) lines.Add(line);
        return lines;
    }

    /// <summary>1, 2 or 5 times a power of ten, at or above <paramref name="value"/>.</summary>
    internal static double NiceCeiling(double value)
    {
        var power = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var step in new[] { 1.0, 2, 5, 10 })
            if (step * power >= value - 1e-9) return step * power;
        return 10 * power;
    }

    /// <summary>Labels on whole local hours (every 1, 3 or 6 of them) or, over days, at local midnight.</summary>
    private static List<ChartTick> TimeLabels(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone, CultureInfo culture,
        Func<DateTimeOffset, double> x)
    {
        var range = to - from;
        var step = range <= TimeSpan.FromHours(8) ? TimeSpan.FromHours(1)
            : range <= TimeSpan.FromHours(30) ? TimeSpan.FromHours(4)
            : TimeSpan.FromDays(1);
        var days = step >= TimeSpan.FromDays(1);

        var local = TimeZoneInfo.ConvertTime(from, zone);
        var tick = days ? local.Date : local.Date.AddHours(Math.Ceiling(local.TimeOfDay.TotalHours / step.TotalHours) * step.TotalHours);
        var ticks = new List<ChartTick>();
        for (; ticks.Count < 50; tick += step)
        {
            var at = new DateTimeOffset(tick, zone.GetUtcOffset(tick));
            if (at < from) continue;
            if (at > to) break;
            ticks.Add(new ChartTick(x(at), days ? tick.ToString("ddd d", culture) : tick.ToString("HH:mm", culture)));
        }
        return ticks;
    }
}
