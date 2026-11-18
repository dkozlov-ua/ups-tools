using UpsTools.Services;

namespace UpsTools.AppTests;

/// <summary>Sleep, wake and Modern Standby, as Windows reports them (or doesn't).</summary>
public sealed class PowerWatcherTests
{
    private readonly List<string> _heard = [];

    private PowerWatcher Watcher(long now = 0)
    {
        var watcher = new PowerWatcher(now);
        watcher.Suspending += () => _heard.Add("suspending");
        watcher.Resumed += () => _heard.Add("resumed");
        watcher.Thawed += gap => _heard.Add($"thawed after {gap.TotalSeconds:0} s");
        return watcher;
    }

    private static long Ms(double seconds) => (long)(seconds * 1000);

    [Fact]
    public void Waking_up_is_told_once_however_often_windows_says_it()
    {
        var watcher = Watcher();
        watcher.OnSuspend();
        watcher.OnResume(); // woken automatically
        watcher.OnResume(); // and then by the user
        Assert.Equal(["suspending", "resumed"], _heard);
    }

    [Fact]
    public void Windows_saying_nothing_of_waking_up_is_not_a_wake()
    {
        Watcher().OnResume();
        Assert.Empty(_heard);
    }

    [Fact]
    public void Checks_on_time_find_nothing()
    {
        var watcher = Watcher();
        for (var i = 1; i <= 20; i++) watcher.OnCheck(Ms(5 * i + 0.5));
        Assert.Empty(_heard);
    }

    [Fact]
    public void Being_frozen_without_a_word_is_noticed()
    {
        var watcher = Watcher();
        watcher.OnCheck(Ms(5));
        watcher.OnCheck(Ms(5 + 600)); // Modern Standby: suspended by Windows for 10 minutes
        watcher.OnCheck(Ms(5 + 605));
        Assert.Equal(["thawed after 600 s"], _heard);
    }

    [Fact]
    public void Waking_up_noticed_by_the_clock_first_is_told_once()
    {
        var watcher = Watcher();
        watcher.OnSuspend();
        watcher.OnCheck(Ms(3600)); // the clock check ran before Windows said the PC woke up
        watcher.OnResume();
        Assert.Equal(["suspending", "resumed"], _heard);
    }

    [Fact]
    public void Waking_up_told_by_windows_first_is_not_also_a_thaw()
    {
        var watcher = Watcher();
        watcher.OnCheck(Ms(5));
        watcher.OnSuspend();
        watcher.OnResume(); // Windows said it before the clock check ran
        watcher.OnCheck(Ms(5 + 3600));
        watcher.OnCheck(Ms(5 + 3605));
        watcher.OnCheck(Ms(5 + 3605 + 600)); // a later freeze without a word still counts
        Assert.Equal(["suspending", "resumed", "thawed after 600 s"], _heard);
    }

    [Fact]
    public void Windows_says_when_this_pc_goes_into_modern_standby()
    {
        using var watcher = new PowerWatcher();
        Assert.True(watcher.SeesModernStandby);
    }
}
