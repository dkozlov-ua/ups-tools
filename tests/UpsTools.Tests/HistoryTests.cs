using System.Globalization;
using UpsTools.Core;
using UpsTools.Core.History;

namespace UpsTools.Tests;

/// <summary>The history a UPS keeps for its chart, and the chart worked out of it.</summary>
public sealed class HistoryTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 28, 10, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Directory.CreateTempSubdirectory("upstools-history-").FullName;
    private DateTimeOffset _now = Start;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string File => Path.Combine(_folder, "ups.txt");

    private UpsHistory New() => new(File, () => _now);

    private static UpsStatus Reading(DateTimeOffset at, bool? grid = true, int? battery = 80, double? load = 100) => new()
    {
        Timestamp = at,
        GridPresent = grid,
        BatteryPercent = battery,
        AcOutputW = load,
    };

    [Fact]
    public void The_readings_of_a_minute_make_one_point()
    {
        var history = New();
        history.Record(Reading(Start.AddSeconds(5), load: 100));
        history.Record(Reading(Start.AddSeconds(25), grid: false, battery: 79, load: 200));
        history.Record(Reading(Start.AddSeconds(45), grid: true, battery: 78, load: 300));

        var point = Assert.Single(history.Points(Start));
        Assert.Equal((Start, (bool?)false, (int?)78, (double?)200), (point.Time, point.Grid, point.BatteryPercent, point.LoadW));
    }

    [Fact]
    public void Points_and_events_are_kept_in_the_file_and_read_back()
    {
        var history = New();
        for (var i = 0; i < 3; i++) history.Record(Reading(Start.AddMinutes(i), battery: 80 - i));
        history.Record(UpsEventKind.GridLost, Start.AddMinutes(1));
        history.Flush();

        var again = New();
        Assert.Equal([80, 79, 78], again.Points(Start).Select(p => p.BatteryPercent ?? 0));
        Assert.Equal(UpsEventKind.GridLost, Assert.Single(again.Events(Start)).Kind);
    }

    [Fact]
    public void The_minute_being_recorded_shows_but_is_written_once_over()
    {
        var history = New();
        history.Record(Reading(Start));
        Assert.Single(history.Points(Start));
        Assert.Empty(New().Points(Start)); // not in the file yet

        history.Record(Reading(Start.AddMinutes(1)));
        Assert.Single(New().Points(Start)); // the first minute is over
    }

    [Fact]
    public void What_is_older_than_a_week_is_dropped_from_the_file_too()
    {
        var history = New();
        history.Record(Reading(Start));
        history.Record(Reading(Start.AddDays(3)));
        history.Record(UpsEventKind.GridLost, Start);
        history.Flush();

        _now = Start.AddDays(8);
        var later = New();
        Assert.Equal([Start.AddDays(3)], later.Points(Start).Select(p => p.Time));
        Assert.Empty(later.Events(Start));
        Assert.Equal(2, System.IO.File.ReadAllLines(File).Length); // the header and one point
    }

    [Fact]
    public void A_damaged_line_is_skipped()
    {
        System.IO.File.WriteAllText(File, $"# UPS Tools history 1\np nonsense\np {Start.ToUnixTimeSeconds()} 1 50 12.5\ne {Start.ToUnixTimeSeconds()} Nope\n");
        var point = Assert.Single(New().Points(Start));
        Assert.Equal((50, 12.5), (point.BatteryPercent!.Value, point.LoadW!.Value));
    }

    [Fact]
    public void Forgetting_the_ups_deletes_its_file()
    {
        var history = New();
        history.Record(UpsEventKind.GridLost);
        history.Delete();
        Assert.False(System.IO.File.Exists(File));
        Assert.Empty(history.Events(Start));
    }

    // ---- the chart ----

    private static List<HistoryPoint> Minutes(int count, Func<int, HistoryPoint> point) => [.. Enumerable.Range(0, count).Select(point)];

    [Fact]
    public void The_lines_span_the_range_and_the_load_axis_ends_on_a_round_number()
    {
        var points = Minutes(61, i => new HistoryPoint(Start.AddMinutes(i), true, 100 - i / 2, 130));
        var chart = HistoryChart.Build(points, [], Start, Start.AddHours(1), TimeZoneInfo.Utc, CultureInfo.InvariantCulture);

        var battery = Assert.Single(chart.Battery);
        Assert.Equal((0, 1.0), (battery[0].X, battery[0].Y));
        Assert.Equal((1, 0.7), (battery[^1].X, battery[^1].Y));
        Assert.Equal(200, chart.LoadMaxW);
        Assert.Equal(0.65, Assert.Single(chart.Load)[0].Y);
        Assert.Equal(["0 W", "100 W", "200 W"], chart.LoadTicks.Select(t => t.Label));
        Assert.Empty(chart.Outages);
    }

    [Fact]
    public void An_outage_is_a_span_and_missing_readings_break_the_lines()
    {
        var points = Minutes(30, i => new HistoryPoint(Start.AddMinutes(i), i is < 10 or >= 15, 90, 100))
            .Concat(Minutes(10, i => new HistoryPoint(Start.AddMinutes(60 + i), true, 90, 100))).ToList();
        var chart = HistoryChart.Build(points, [new HistoryEvent(Start.AddMinutes(10), UpsEventKind.GridLost)],
            Start, Start.AddMinutes(120), TimeZoneInfo.Utc, CultureInfo.InvariantCulture);

        var outage = Assert.Single(chart.Outages);
        Assert.Equal((10 / 120.0, 15 / 120.0), (outage.From, outage.To), new ToleranceComparer());
        Assert.Equal(2, chart.Battery.Count);
        var missing = Assert.Single(chart.Missing);
        Assert.Equal((29 / 120.0, 60 / 120.0), (missing.From, missing.To), new ToleranceComparer());
        Assert.Equal(new ChartEvent(10 / 120.0, UpsEventKind.GridLost), Assert.Single(chart.Events));
    }

    [Fact]
    public void A_week_is_merged_to_fewer_points_keeping_every_outage()
    {
        var points = Minutes(7 * 24 * 60, i => new HistoryPoint(Start.AddMinutes(i), i != 5000, 50, 100));
        var chart = HistoryChart.Build(points, [], Start, Start.AddDays(7), TimeZoneInfo.Utc, CultureInfo.InvariantCulture);

        Assert.True(Assert.Single(chart.Battery).Count <= HistoryChart.MaxPoints);
        Assert.Single(chart.Outages);
        Assert.Equal(["Sun 29", "Mon 30", "Tue 1", "Wed 2", "Thu 3", "Fri 4", "Sat 5"], // midnights after Saturday 10:00
            chart.TimeTicks.Select(t => t.Label));
    }

    [Fact]
    public void Hours_are_labelled_in_local_time()
    {
        var kyiv = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");
        var chart = HistoryChart.Build([], [], Start.AddMinutes(30), Start.AddHours(6).AddMinutes(30), kyiv, CultureInfo.InvariantCulture);
        Assert.Equal(["13:00", "14:00", "15:00", "16:00", "17:00", "18:00"], chart.TimeTicks.Select(t => t.Label));
        Assert.True(chart.IsEmpty);
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(11, 20)]
    [InlineData(130, 200)]
    [InlineData(450, 500)]
    [InlineData(501, 1000)]
    public void Axis_ends_are_one_two_or_five_times_a_power_of_ten(double max, double nice) =>
        Assert.Equal(nice, HistoryChart.NiceCeiling(max));

    private sealed class ToleranceComparer : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) a, (double, double) b) =>
            Math.Abs(a.Item1 - b.Item1) < 1e-9 && Math.Abs(a.Item2 - b.Item2) < 1e-9;

        public int GetHashCode((double, double) obj) => 0;
    }
}
