using System.Globalization;
using UpsTools.Core;
using UpsTools.Core.Bluetti;

namespace UpsTools.Tests;

[Collection("Environment")]
public class UpsModelTests
{
    private static UpsStatus Status => new() { Timestamp = DateTimeOffset.UnixEpoch };

    [Fact]
    public void Totals_are_null_only_when_both_parts_are_unknown()
    {
        Assert.Null(Status.TotalInputW);
        Assert.Null(Status.TotalOutputW);
        Assert.Equal(5, (Status with { AcInputW = 5 }).TotalInputW);
        Assert.Equal(7, (Status with { DcInputW = 7 }).TotalInputW);
        Assert.Equal(12, (Status with { AcOutputW = 10, DcOutputW = 2 }).TotalOutputW);
    }

    [Fact]
    public void Battery_flow_is_input_minus_output_when_both_are_known()
    {
        Assert.Null((Status with { AcInputW = 100 }).BatteryFlowW);
        Assert.Null((Status with { AcOutputW = 100 }).BatteryFlowW);
        Assert.Equal(50, (Status with { AcInputW = 150, AcOutputW = 100 }).BatteryFlowW);
        Assert.Equal(-120, (Status with { AcInputW = 0, AcOutputW = 100, DcOutputW = 20 }).BatteryFlowW);
    }

    [Fact]
    public void Runtime_prefers_the_devices_own_figure()
    {
        var status = Status with
        {
            ReportedRuntime = TimeSpan.FromMinutes(42), BatteryPercent = 50, CapacityWh = 1000, AcOutputW = 100,
        };
        Assert.Equal(TimeSpan.FromMinutes(42), status.EstimatedRuntime);
    }

    [Fact]
    public void Runtime_is_estimated_from_stored_energy_and_load()
    {
        var status = Status with { BatteryPercent = 50, CapacityWh = 1000, AcOutputW = 100, DcOutputW = 25 };
        Assert.Equal(TimeSpan.FromHours(4), status.EstimatedRuntime);
    }

    [Theory]
    [InlineData(null, 1000.0, 100.0)]
    [InlineData(50, null, 100.0)]
    [InlineData(50, 1000.0, null)]
    [InlineData(50, 1000.0, 0.5)] // no real load: it would last "forever"
    public void Runtime_is_unknown_without_enough_data(int? soc, double? capacity, double? load)
    {
        var status = Status with { BatteryPercent = soc, CapacityWh = capacity, AcOutputW = load };
        Assert.Null(status.EstimatedRuntime);
    }

    [Fact]
    public void Formats_watts_and_durations()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Assert.Equal("—", UpsFormat.Watts(null));
        Assert.Equal("157 W", UpsFormat.Watts(156.6));
        Assert.Equal("0 W", UpsFormat.Watts(0));
        Assert.Equal("0 m", UpsFormat.Duration(TimeSpan.FromSeconds(59)));
        Assert.Equal("0 m", UpsFormat.Duration(TimeSpan.FromMinutes(-5)));
        Assert.Equal("59 m", UpsFormat.Duration(TimeSpan.FromMinutes(59.9)));
        Assert.Equal("1 h 00 m", UpsFormat.Duration(TimeSpan.FromHours(1)));
        Assert.Equal("26 h 05 m", UpsFormat.Duration(TimeSpan.FromMinutes(26 * 60 + 5)));
    }

    [Theory]
    [InlineData(UpsEventKind.GridLost, "Grid lost", UpsEventKind.GridRestored)]
    [InlineData(UpsEventKind.GridRestored, "Grid restored", UpsEventKind.GridLost)]
    [InlineData(UpsEventKind.UpsUnreachable, "UPS unreachable", UpsEventKind.UpsReachable)]
    [InlineData(UpsEventKind.UpsReachable, "UPS reachable again", UpsEventKind.UpsUnreachable)]
    public void Event_kinds_have_a_title_an_opposite_and_a_description(UpsEventKind kind, string title, UpsEventKind opposite)
    {
        Assert.Equal(title, kind.Title());
        Assert.Equal(opposite, kind.Opposite());
        Assert.Equal(kind, kind.Opposite().Opposite());
        Assert.EndsWith(".", kind.Description());
    }

    [Fact]
    public void Unreachable_description_names_the_failure_count()
    {
        Assert.Contains($"{UpsStateTracker.DefaultFailuresBeforeUnreachable} polls in a row", UpsEventKind.UpsUnreachable.Description());
    }

    [Fact]
    public void Unknown_event_kinds_fall_back_to_their_name()
    {
        var unknown = (UpsEventKind)99;
        Assert.Equal("99", unknown.Title());
        Assert.Equal(unknown, unknown.Opposite());
        Assert.Equal("", unknown.Description());
    }

    [Fact]
    public void The_registry_has_the_bluetti_driver_unless_the_simulator_is_on()
    {
        var drivers = new UpsDrivers();
        var bluetti = Assert.Single(drivers.All);
        Assert.Equal(BluettiBleDriver.DriverId, bluetti.Id);
        Assert.Equal("Bluetti (Bluetooth LE)", bluetti.DisplayName);
        Assert.Same(bluetti, drivers.Find("bluetti-ble"));
        Assert.Null(drivers.Find("nope"));
    }

    [Fact]
    public void The_bluetti_driver_refuses_unknown_models()
    {
        var driver = new BluettiBleDriver();
        var error = Assert.Throws<NotSupportedException>(() =>
            driver.CreateConnection(new PairedUps("bluetti-ble", "00:11:22:33:44:55", "X", "AC500")));
        Assert.Equal("Bluetti model 'AC500' is not supported", error.Message);
    }

    [Fact]
    public async Task The_bluetti_driver_connects_lazily()
    {
        var driver = new BluettiBleDriver();
        // Creating it touches no Bluetooth: the address only has to parse.
        await using var connection = driver.CreateConnection(new PairedUps("bluetti-ble", "00:11:22:33:44:55", "X", "el100v2"));
        Assert.IsType<BluettiConnection>(connection);
    }
}
