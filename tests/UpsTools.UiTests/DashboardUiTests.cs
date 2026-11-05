using UpsTools.UiTests.Support;

namespace UpsTools.UiTests;

/// <summary>The dashboard follows the (simulated) UPS, and events run the scripts.</summary>
public sealed class DashboardUiTests : IDisposable
{
    private readonly AppUnderTest _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public void The_dashboard_shows_the_readings()
    {
        _app.Seed();
        var window = _app.Start()!;

        window.HasText("DeviceTitle", "Bluetti Simulator");
        window.HasText("LinkText", "Connected");
        window.HasText("DeviceSubtitle", "Serial SIM0001 · 00:00:00:00:00:01");
        window.HasText("GridValue", "Online");
        window.HasText("GridDetail", "230.4 V at the UPS input");
        window.HasText("BatteryValue", "80 %");
        window.HasText("BatteryDetail", "≈ 0.82 kWh of 1.02 kWh stored");
        window.HasText("FlowValue", "+30 W");
        window.HasText("FlowDetail", "Charging");
        window.HasText("RuntimeValue", "6 h 49 m");
        window.HasText("InputValue", "150 W");
        window.HasText("OutputValue", "120 W");
        window.HasText("OutputDetail", "AC 120 W · DC — · 229.8 V");
        Assert.StartsWith("Updated ", window.ById("LastUpdated").Text());
        Assert.Equal(["Dashboard", "Scripts", "Configuration"], new[] { "Dashboard", "Scripts", "Configuration" }.Select(t => window.ByName(t).Text()));
    }

    [Fact]
    public void Without_a_ups_it_asks_to_pair_one()
    {
        _app.Seed(paired: false);
        var window = _app.Start()!;

        window.HasText("DeviceTitle", "No UPS paired");
        window.HasText("DeviceSubtitle", "Open Configuration → Find UPS to pair one.");
        window.HasText("LinkText", "Not paired");
        window.HasText("GridValue", "—");
    }

    [Fact]
    public void A_power_cut_runs_the_grid_lost_scripts_and_the_return_runs_grid_restored()
    {
        var lost = _app.Script("lost.ps1");
        var restored = _app.Script("restored.ps1");
        _app.Seed(s =>
        {
            s["Scripts"]!["GridLost"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["Path"] = lost, ["Arguments"] = "now" });
            s["Scripts"]!["GridRestored"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["Path"] = restored });
        });
        var window = _app.Start()!;
        window.HasText("GridValue", "Online");

        _app.Simulate(new { GridPresent = false, BatteryPercent = 79, AcInputW = 0, AcOutputW = 120, ReportedRuntimeMinutes = 300 });

        window.HasText("GridValue", "Lost");
        window.HasText("GridDetail", "Running on battery");
        window.HasText("FlowValue", "−120 W");
        window.HasText("RuntimeValue", "5 h 00 m");
        window.HasText("RuntimeDetail", "Reported by the UPS");
        Ui.Until(() => _app.Runs("lost.ps1") == 1, "the Grid lost script");
        Assert.Equal("ran now", File.ReadAllText(Path.Combine(_app.Work, "lost.ps1.out")).Trim());
        Assert.Contains(window.AllById("ActivityMessage"), m => m.Text() == "Grid lost");

        _app.Simulate(new { GridPresent = true, BatteryPercent = 79, AcInputW = 150, AcOutputW = 120 });

        window.HasText("GridValue", "Online");
        Ui.Until(() => _app.Runs("restored.ps1") == 1, "the Grid restored script");
        Assert.Equal(1, _app.Runs("lost.ps1"));
    }

    [Fact]
    public void A_low_battery_runs_the_battery_low_scripts_once_per_outage()
    {
        var low = _app.Script("low.ps1");
        _app.Seed(s => s["Scripts"]!["BatteryLow"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["Path"] = low }));
        var window = _app.Start()!;
        window.HasText("GridValue", "Online");

        _app.Simulate(new { GridPresent = false, BatteryPercent = 50, AcInputW = 0, AcOutputW = 120 });
        window.HasText("GridValue", "Lost");
        Assert.Equal(0, _app.Runs("low.ps1"));

        _app.Simulate(new { GridPresent = false, BatteryPercent = 20, AcInputW = 0, AcOutputW = 120 });
        Ui.Until(() => _app.Runs("low.ps1") == 1, "the Battery low script");
        Assert.Contains(window.AllById("ActivityMessage"), m => m.Text() == "Battery low");

        _app.Simulate(new { GridPresent = false, BatteryPercent = 19, AcInputW = 0, AcOutputW = 120 });
        window.HasText("BatteryValue", "19 %");
        _app.Simulate(new { GridPresent = false, BatteryPercent = 18, AcInputW = 0, AcOutputW = 120 });
        window.HasText("BatteryValue", "18 %");
        Assert.Equal(1, _app.Runs("low.ps1"));
    }

    [Fact]
    public void A_ups_that_stops_answering_becomes_unreachable_then_reachable_again()
    {
        _app.Seed();
        var window = _app.Start()!;
        window.HasText("LinkText", "Connected");

        _app.Simulate(new { Fail = "Bluetooth device not found" });

        window.HasText("LinkText", "Reconnecting…");
        window.HasText("LinkText", "Unreachable", TimeSpan.FromSeconds(20));
        Assert.Contains(window.AllById("ActivityMessage"), m => m.Text() == "UPS unreachable");
        Assert.Contains(window.AllById("ActivityMessage"), m => m.Text() == "UPS read failed: Bluetooth device not found");

        _app.Simulate(new { GridPresent = true, BatteryPercent = 64 });

        window.HasText("LinkText", "Connected");
        window.HasText("BatteryValue", "64 %");
        Ui.Until(() => window.AllById("ActivityMessage").Any(m => m.Text() == "UPS reachable again"), "UPS reachable again");
    }
}
