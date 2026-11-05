using System.Globalization;
using UpsTools.Core;

namespace UpsTools.Tests;

/// <summary>What scripts are told about the UPS.</summary>
public class ScriptEnvironmentTests
{
    private static readonly UpsStatus OnBattery = new()
    {
        Timestamp = new DateTimeOffset(2026, 11, 5, 21, 30, 5, TimeSpan.FromHours(2)),
        Model = "EL100V2",
        Serial = "2527008220279",
        GridPresent = false,
        BatteryPercent = 64,
        CapacityWh = 1024,
        AcOutputW = 119.6,
        DcOutputW = 0.2,
    };

    [Fact]
    public void An_event_gets_the_reading_it_came_with()
    {
        CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // numbers mustn't depend on it
        var variables = ScriptEnvironment.For(UpsEventKind.BatteryLow, OnBattery, reachable: true, TimeSpan.FromSeconds(754.9), manual: false);

        Assert.Equal(new Dictionary<string, string?>
        {
            ["UPS_EVENT"] = "BatteryLow",
            ["UPS_MANUAL"] = null,
            ["UPS_GRID"] = "0",
            ["UPS_REACHABLE"] = "1",
            ["UPS_BATTERY_PERCENT"] = "64",
            ["UPS_RUNTIME_MINUTES"] = "328", // 1024 Wh × 64 % / 119.8 W = 5.47 h
            ["UPS_LOAD_W"] = "120",
            ["UPS_OUTAGE_SECONDS"] = "754",
            ["UPS_MODEL"] = "EL100V2",
            ["UPS_SERIAL"] = "2527008220279",
            ["UPS_READING_TIME"] = "2026-11-05T21:30:05+02:00",
        }, variables);
    }

    [Fact]
    public void What_is_not_known_is_removed()
    {
        var variables = ScriptEnvironment.For(null, null, reachable: null, outage: null, manual: true);

        Assert.Equal(ScriptEnvironment.Names, variables.Keys);
        Assert.Equal("1", variables["UPS_MANUAL"]);
        Assert.All(variables.Where(v => v.Key != "UPS_MANUAL"), v => Assert.Null(v.Value));
    }

    [Fact]
    public void Unreachable_and_on_grid_are_zero_and_one()
    {
        var variables = ScriptEnvironment.For(UpsEventKind.UpsUnreachable, OnBattery with { GridPresent = true }, reachable: false, null, false);
        Assert.Equal(("UpsUnreachable", "1", "0"), (variables["UPS_EVENT"], variables["UPS_GRID"], variables["UPS_REACHABLE"]));
        Assert.Null(variables["UPS_OUTAGE_SECONDS"]);
    }

    [Fact]
    public void A_negative_outage_is_not_one()
    {
        Assert.Null(ScriptEnvironment.For(UpsEventKind.GridLost, OnBattery, true, TimeSpan.FromSeconds(-3), false)["UPS_OUTAGE_SECONDS"]);
        Assert.Equal("0", ScriptEnvironment.For(UpsEventKind.GridLost, OnBattery, true, TimeSpan.Zero, false)["UPS_OUTAGE_SECONDS"]);
    }

    [Fact]
    public void None_removes_every_variable()
    {
        Assert.Equal(ScriptEnvironment.Names, ScriptEnvironment.None.Keys);
        Assert.All(ScriptEnvironment.None.Values, Assert.Null);
        Assert.All(ScriptEnvironment.Names, name => Assert.StartsWith("UPS_", name));
    }
}
