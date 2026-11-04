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
        ReportedRuntime = TimeSpan.FromMinutes(370),
    };

    [Fact]
    public void Grid_lost_shows_battery_and_runtime_at_the_current_load()
    {
        var notice = UpsNotice.For(UpsEventKind.GridLost, OnBattery);
        Assert.Equal("Grid lost — running on battery", notice.Title);
        Assert.Equal("Battery 87 % · about 6 h 10 m left at 120 W", notice.Body);
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
        Assert.Equal("The UPS is answering again. On battery · Battery 87 % · about 6 h 10 m left at 120 W.", battery.Body);
        Assert.Equal(NoticeTone.Warning, battery.Tone);
    }

    [Theory]
    [InlineData(new[] { "nas_sleep.ahk" }, new string[0], "Started nas_sleep.ahk")]
    [InlineData(new[] { "pause_games.ahk", "nas_sleep.ahk" }, new string[0], "Started pause_games.ahk and nas_sleep.ahk")]
    [InlineData(new[] { "a.ahk", "b.ahk", "c.ahk", "d.ahk" }, new string[0], "Started a.ahk, b.ahk and 2 more")]
    [InlineData(new[] { "a.ahk" }, new[] { "gone.bat" }, "Started a.ahk; couldn't start gone.bat")]
    [InlineData(new string[0], new[] { "gone.bat" }, "Couldn't start gone.bat")]
    public void Footnote_lists_the_scripts(string[] started, string[] failed, string expected) =>
        Assert.Equal(expected, UpsNotice.For(UpsEventKind.GridLost, OnBattery, null, started, failed).Footnote);
}
