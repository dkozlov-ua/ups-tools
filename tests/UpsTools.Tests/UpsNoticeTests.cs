using System.Globalization;
using UpsTools.Core;

namespace UpsTools.Tests;

public class UpsNoticeTests
{
    public UpsNoticeTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    private static UpsStatus OnBattery => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch,
        GridPresent = false,
        BatteryPercent = 87,
        AcOutputW = 100,
        DcOutputW = 20,
        CapacityWh = 1000,
    };

    [Fact]
    public void Grid_lost_shows_battery_and_runtime_at_the_current_load()
    {
        var notice = UpsNotice.For(UpsEventKind.GridLost, OnBattery);
        Assert.Equal("Grid lost — running on battery", notice.Title);
        Assert.Equal("Battery 87 % · about 7 h 15 m left at 120 W", notice.Body);
        Assert.Null(notice.Footnote);
        Assert.Equal(NoticeTone.Warning, notice.Tone);
        Assert.True(notice.Important);
    }

    [Fact]
    public void Grid_lost_without_readings_still_says_what_happened()
    {
        var notice = UpsNotice.For(UpsEventKind.GridLost, null);
        Assert.Equal("The UPS switched to battery.", notice.Body);
    }

    [Fact]
    public void Grid_restored_says_how_long_the_outage_lasted()
    {
        var status = OnBattery with { GridPresent = true, BatteryPercent = 64 };
        Assert.Equal("Power is back after 12 m on battery. Battery 64 %.",
            UpsNotice.For(UpsEventKind.GridRestored, status, TimeSpan.FromMinutes(12.5)).Body);
        Assert.Equal("Power is back after less than a minute on battery. Battery 64 %.",
            UpsNotice.For(UpsEventKind.GridRestored, status, TimeSpan.FromSeconds(20)).Body);
        Assert.Equal("Power is back. Battery 64 %.", UpsNotice.For(UpsEventKind.GridRestored, status).Body);
    }

    [Fact]
    public void Restored_replaces_lost_and_reachable_replaces_unreachable()
    {
        Assert.Equal(UpsNotice.For(UpsEventKind.GridLost, null).Tag, UpsNotice.For(UpsEventKind.GridRestored, null).Tag);
        Assert.Equal(UpsNotice.For(UpsEventKind.UpsUnreachable, null).Tag, UpsNotice.For(UpsEventKind.UpsReachable, null).Tag);
        Assert.NotEqual(UpsNotice.For(UpsEventKind.GridLost, null).Tag, UpsNotice.For(UpsEventKind.UpsUnreachable, null).Tag);
    }

    [Fact]
    public void Unreachable_mentions_the_last_reading_when_there_was_one()
    {
        Assert.Contains("(last reading at ", UpsNotice.For(UpsEventKind.UpsUnreachable, OnBattery).Body);
        Assert.Equal("The UPS stopped answering. Power cuts can't be detected until it answers again.",
            UpsNotice.For(UpsEventKind.UpsUnreachable, null).Body);
        Assert.Equal(NoticeTone.Problem, UpsNotice.For(UpsEventKind.UpsUnreachable, null).Tone);
    }

    [Fact]
    public void Reachable_again_reports_the_current_power_state()
    {
        var online = OnBattery with { GridPresent = true, BatteryPercent = 90 };
        var back = UpsNotice.For(UpsEventKind.UpsReachable, online);
        Assert.Equal("The UPS is answering again. Grid online · Battery 90 %.", back.Body);
        Assert.Equal(NoticeTone.Good, back.Tone);

        var battery = UpsNotice.For(UpsEventKind.UpsReachable, OnBattery);
        Assert.Equal("The UPS is answering again. On battery · Battery 87 % · about 7 h 15 m left at 120 W.", battery.Body);
        Assert.Equal(NoticeTone.Warning, battery.Tone);
    }

    [Theory]
    [InlineData(new[] { "nas_sleep.ahk" }, new string[0], "Started nas_sleep.ahk")]
    [InlineData(new[] { "pause_games.ahk", "nas_sleep.ahk" }, new string[0], "Started pause_games.ahk and nas_sleep.ahk")]
    [InlineData(new[] { "a.ahk", "b.ahk", "c.ahk", "d.ahk" }, new string[0], "Started a.ahk, b.ahk and 2 more")]
    [InlineData(new[] { "a.ahk" }, new[] { "gone.bat" }, "Started a.ahk; couldn't start gone.bat")]
    [InlineData(new string[0], new[] { "gone.bat" }, "Couldn't start gone.bat")]
    public void Footnote_lists_the_scripts(string[] started, string[] failed, string expected)
    {
        var chain = new ScriptChain([.. started.Concat(failed).Select(n => new ScriptStep(n, false))]);
        _ = chain.RunAsync((_, step, _) => Task.FromResult(failed.Contains(step.Name)
            ? StepResult.Failure("file not found")
            : StepResult.Success), CancellationToken.None);
        Assert.Equal(expected, UpsNotice.For(UpsEventKind.GridLost, OnBattery, null, chain).Footnote);
    }

    [Fact]
    public void Grid_restored_says_which_grid_lost_scripts_it_stopped()
    {
        var chain = new ScriptChain([new ScriptStep("wake_nas.ahk", false)]);
        _ = chain.RunAsync((_, _, _) => Task.FromResult(StepResult.Success), CancellationToken.None);
        var notice = UpsNotice.For(UpsEventKind.GridRestored, null, null, chain,
            [(UpsEventKind.GridLost, ["hibernate.ps1"])]);
        Assert.Equal("Stopped the Grid lost scripts before hibernate.ps1; started wake_nas.ahk", notice.Footnote);
    }

    [Fact]
    public void Battery_low_from_an_unreachable_ups_says_so_with_its_last_reading()
    {
        var notice = UpsNotice.For(UpsEventKind.BatteryLow, OnBattery, unreachable: true);
        Assert.Equal((NoticeTone.Problem, "Battery low — UPS unreachable", true), (notice.Tone, notice.Title, notice.Important));
        Assert.StartsWith("The UPS stopped answering while on battery, so its charge isn't known. Its last reading, at ", notice.Body);
        Assert.EndsWith(": Battery 87 % · about 7 h 15 m left at 120 W.", notice.Body);

        Assert.Equal("The UPS stopped answering while on battery, so its charge isn't known.",
            UpsNotice.For(UpsEventKind.BatteryLow, null, unreachable: true).Body);
        Assert.EndsWith(": no charge reported.",
            UpsNotice.For(UpsEventKind.BatteryLow, new UpsStatus { Timestamp = DateTimeOffset.UnixEpoch }, unreachable: true).Body);
    }

    [Fact]
    public void Grid_restored_names_each_event_whose_scripts_it_stopped()
    {
        var notice = UpsNotice.For(UpsEventKind.GridRestored, null, null, null,
            [(UpsEventKind.GridLost, ["a.ps1"]), (UpsEventKind.BatteryLow, []), (UpsEventKind.BatteryLow, ["hibernate.ps1", "b"])]);
        Assert.Equal("Stopped the Grid lost scripts before a.ps1; stopped the Battery low scripts before hibernate.ps1 and b", notice.Footnote);
    }

    [Fact]
    public void Battery_low_is_an_important_problem_with_the_charge_and_time_left()
    {
        var notice = UpsNotice.For(UpsEventKind.BatteryLow, OnBattery with { BatteryPercent = 3 });
        Assert.Equal((NoticeTone.Problem, "Battery low", "Battery 3 % · about 15 m left at 120 W", true),
            (notice.Tone, notice.Title, notice.Body, notice.Important));
        Assert.Equal(UpsNotice.For(UpsEventKind.GridLost, null).Tag, notice.Tag); // replaces Grid lost; Grid restored replaces it
        Assert.Equal("The UPS battery is running low.", UpsNotice.For(UpsEventKind.BatteryLow, null).Body);
    }

    [Fact]
    public void Only_lost_and_unreachable_are_important()
    {
        Assert.True(UpsNotice.For(UpsEventKind.GridLost, null).Important);
        Assert.True(UpsNotice.For(UpsEventKind.UpsUnreachable, null).Important);
        Assert.False(UpsNotice.For(UpsEventKind.GridRestored, null).Important);
        Assert.False(UpsNotice.For(UpsEventKind.UpsReachable, null).Important);
    }

    [Fact]
    public void Grid_lost_without_a_known_load_gives_no_runtime()
    {
        var status = OnBattery with { AcOutputW = null, DcOutputW = null };
        Assert.Equal("Battery 87 %", UpsNotice.For(UpsEventKind.GridLost, status).Body);
    }

    [Fact]
    public void The_runtime_is_for_the_average_load_when_there_is_one()
    {
        var status = OnBattery with { AverageOutputW = 145 };
        Assert.Equal("Battery 87 % · about 6 h 00 m left at 145 W", UpsNotice.For(UpsEventKind.GridLost, status).Body);
    }

    [Fact]
    public void Grid_restored_without_a_reading()
    {
        var notice = UpsNotice.For(UpsEventKind.GridRestored, null, TimeSpan.FromHours(2));
        Assert.Equal("Power is back after 2 h 00 m on battery.", notice.Body);
        Assert.Equal(NoticeTone.Good, notice.Tone);
        Assert.Equal("grid", notice.Tag);
    }

    [Fact]
    public void Reachable_again_without_a_grid_reading()
    {
        Assert.Equal("The UPS is answering again. Battery 87 %.",
            UpsNotice.For(UpsEventKind.UpsReachable, OnBattery with { GridPresent = null }).Body);
        var bare = UpsNotice.For(UpsEventKind.UpsReachable, null);
        Assert.Equal("The UPS is answering again.", bare.Body);
        Assert.Equal(NoticeTone.Good, bare.Tone);
        Assert.Equal("link", bare.Tag);
    }

    [Fact]
    public void An_unknown_event_gets_a_neutral_notice()
    {
        var notice = UpsNotice.For((UpsEventKind)99, null);
        Assert.Equal(NoticeTone.Neutral, notice.Tone);
        Assert.Equal("99", notice.Title);
        Assert.Equal("", notice.Body);
    }
}
