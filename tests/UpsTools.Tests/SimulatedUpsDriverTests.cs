using System.Text.Json;
using UpsTools.Core;

namespace UpsTools.Tests;

[Collection("Environment")] // sets UPSTOOLS_SIMULATOR
public sealed class SimulatedUpsDriverTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"ups-sim-{Guid.NewGuid():N}.json");
    private readonly SimulatedUpsDriver _driver;

    public SimulatedUpsDriverTests() => _driver = new SimulatedUpsDriver(_file);

    public void Dispose() => File.Delete(_file);

    private void Write(object reading) => File.WriteAllText(_file, JsonSerializer.Serialize(reading));

    private IUpsConnection Connect() =>
        _driver.CreateConnection(new PairedUps(SimulatedUpsDriver.DriverId, SimulatedUpsDriver.Address, "x", "x"));

    [Fact]
    public async Task Each_read_reports_what_the_file_says()
    {
        Write(new { GridPresent = false, BatteryPercent = 64, CapacityWh = 1000, AcOutputW = 120 });
        await using var ups = Connect();

        var status = await ups.ReadAsync(CancellationToken.None);
        Assert.False(status.GridPresent);
        Assert.Equal(64, status.BatteryPercent);
        Assert.Equal(120, status.TotalOutputW);
        Assert.Equal(1000, status.CapacityWh);
        Assert.Equal("Simulator", status.Model);
        Assert.Equal("SIM0001", status.Serial);

        Write(new { GridPresent = true });
        status = await ups.ReadAsync(CancellationToken.None);
        Assert.True(status.GridPresent);
        Assert.Equal(100, status.BatteryPercent); // the default
        Assert.Equal(1024, status.CapacityWh);
    }

    [Fact]
    public async Task Fail_makes_reads_fail()
    {
        Write(new { Fail = "out of range" });
        await using var ups = Connect();
        var error = await Assert.ThrowsAsync<IOException>(() => ups.ReadAsync(CancellationToken.None));
        Assert.Equal("out of range", error.Message);
    }

    [Fact]
    public async Task A_missing_file_is_a_ups_that_does_not_answer()
    {
        await using var ups = Connect();
        var error = await Assert.ThrowsAsync<IOException>(() => ups.ReadAsync(CancellationToken.None));
        Assert.Equal("Simulated UPS not found", error.Message);
        await ups.ResetAsync();
    }

    [Fact]
    public async Task An_empty_reading_is_an_error()
    {
        File.WriteAllText(_file, "null");
        await using var ups = Connect();
        await Assert.ThrowsAsync<IOException>(() => ups.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_slow_read_can_be_cancelled()
    {
        Write(new { DelayMs = 60_000 });
        await using var ups = Connect();
        using var cancel = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ups.ReadAsync(cancel.Token));
    }

    [Fact]
    public async Task Discovery_finds_the_simulator_when_its_file_exists()
    {
        var found = new List<DiscoveredUps>();
        await _driver.DiscoverAsync(found.Add, TimeSpan.FromSeconds(8), CancellationToken.None);
        Assert.Empty(found);

        Write(new { });
        await _driver.DiscoverAsync(found.Add, TimeSpan.FromSeconds(8), CancellationToken.None);
        var ups = Assert.Single(found);
        Assert.Equal(new DiscoveredUps("simulated", "00:00:00:00:00:01", "SIMULATOR0001", "Simulator", -42), ups);
    }

    [Fact]
    public async Task Cancelled_discovery_finds_nothing()
    {
        Write(new { });
        var found = new List<DiscoveredUps>();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await _driver.DiscoverAsync(found.Add, TimeSpan.FromSeconds(8), cancel.Token);
        Assert.Empty(found);
    }

    [Fact]
    public void It_is_on_when_the_environment_names_its_file()
    {
        Environment.SetEnvironmentVariable(SimulatedUpsDriver.EnvironmentVariable, _file);
        try
        {
            var drivers = new UpsDrivers();
            var driver = Assert.IsType<SimulatedUpsDriver>(Assert.Single(drivers.All));
            Assert.Equal(_file, driver.Path);
            Assert.Equal("Simulated UPS", driver.DisplayName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SimulatedUpsDriver.EnvironmentVariable, null);
        }
        Assert.Null(SimulatedUpsDriver.FromEnvironment());
    }
}
