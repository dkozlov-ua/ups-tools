using UpsTools.Core;

namespace UpsTools.Tests;

public class LoadAverageTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static UpsStatus At(double seconds, double? watts) =>
        new() { Timestamp = T0.AddSeconds(seconds), AcOutputW = watts };

    [Fact]
    public void The_window_is_five_minutes_by_default()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), new LoadAverage().Window);
    }

    [Fact]
    public void Averages_the_readings_inside_the_window()
    {
        var average = new LoadAverage(TimeSpan.FromSeconds(60));

        Assert.Equal(100, average.Add(At(0, 100)).AverageOutputW);
        Assert.Equal(150, average.Add(At(30, 200)).AverageOutputW);
        Assert.Equal(250, average.Add(At(60, 300)).AverageOutputW); // the one at 0 is a full window old
        Assert.Equal(300, average.Add(At(200, 300)).AverageOutputW); // after a gap, only the new one
    }

    [Fact]
    public void A_short_spike_moves_the_average_only_a_little()
    {
        var average = new LoadAverage();
        for (var s = 0; s < 290; s += 10) average.Add(At(s, 200));

        var spike = average.Add(At(290, 500));

        Assert.Equal(500, spike.TotalOutputW);
        Assert.Equal(210, spike.AverageOutputW);
    }

    [Fact]
    public void A_reading_without_a_load_is_passed_on_as_it_is()
    {
        var average = new LoadAverage();
        average.Add(At(0, 100));

        var unknown = At(10, null);
        Assert.Same(unknown, average.Add(unknown));
        Assert.Equal(150, average.Add(At(20, 200)).AverageOutputW); // the window kept the earlier reading
    }

    [Fact]
    public void A_clock_going_back_starts_over()
    {
        var average = new LoadAverage();
        average.Add(At(100, 100));

        Assert.Equal(300, average.Add(At(50, 300)).AverageOutputW);
    }
}
