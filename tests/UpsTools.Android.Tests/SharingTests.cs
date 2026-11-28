using System.Globalization;
using UpsTools.Core.Sharing;
using UpsTools.Droid.Tests.Support;
using UpsTools.Services;
using UpsTools.ViewModels;

namespace UpsTools.Droid.Tests;

/// <summary>
/// Two phones with the same UPS paired and sharing on (here over multicast on this PC): only one connects to the UPS,
/// the other reads it through that one, and takes over when it stops. The wire format is Core's, as on Windows.
/// </summary>
public sealed class SharingTests : IDisposable
{
    private const string Code = "7KQ2-M9XD-4FTB-R1WE";

    private sealed record Phone(string Name, TempFolder Folder, FakeUps Ups, FakeNotifications Notifications, MonitorController Controller)
    {
        public UpsUnit Unit => Controller.Upses[0];
    }

    private readonly TestUiThread _ui = new();
    private readonly int _port = Random.Shared.Next(40000, 47000);
    private readonly List<Phone> _phones = [];

    static SharingTests() =>
        Environment.SetEnvironmentVariable(SharingServer.LoopbackEnvironmentVariable, "1"); // no firewall prompt

    public SharingTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        MonitorController.CoordinatorTimings = new UpsCoordinator.Timings(
            Announce: TimeSpan.FromMilliseconds(200), Expiry: TimeSpan.FromSeconds(1), Claim: TimeSpan.FromMilliseconds(300),
            Release: TimeSpan.FromSeconds(1), Rest: TimeSpan.FromSeconds(30));
    }

    public void Dispose()
    {
        foreach (var phone in _phones)
        {
            _ui.Run(phone.Controller.StopAsync).Wait(TimeSpan.FromSeconds(10));
            phone.Folder.Dispose();
        }
        _ui.Dispose();
        MonitorController.CoordinatorTimings = UpsCoordinator.Timings.Default;
    }

    private async Task<Phone> Start(string name)
    {
        var folder = new TempFolder();
        var log = new AppLog(folder.Path);
        new SettingsStore(log, folder.Path).Save(new AppSettings
        {
            Upses = [new UpsSettings { Device = FakeUps.Paired, Label = "Desk" }],
            Sharing = new SharingSettings { Enabled = true, Code = Code, Port = _port },
        });
        var ups = new FakeUps();
        var notifications = new FakeNotifications();
        var controller = await _ui.Run(() =>
        {
            var c = new MonitorController(_ui, log, [ups], name, notifications: notifications);
            c.Settings.UpdatePeriodSeconds = 0; // the fake UPS sets the pace
            c.Start();
            return c;
        });
        var phone = new Phone(name, folder, ups, notifications, controller);
        _phones.Add(phone);
        return phone;
    }

    private async Task Until(Func<bool> condition, string what, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await _ui.Run(condition))
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what}");
            await Task.Delay(20);
        }
    }

    private async Task<(Phone Holder, Phone Watcher)> TwoPhones()
    {
        var a = await Start("Phone A");
        var b = await Start("Phone B");
        await Until(() => (a.Unit.Route, b.Unit.Route) is ("via Bluetooth LE", "through Phone A") or ("through Phone B", "via Bluetooth LE"),
            "one to connect and the other to read through it");
        return a.Unit.Route == "via Bluetooth LE" ? (a, b) : (b, a);
    }

    [Fact]
    public async Task Only_one_connects_and_the_other_reads_through_it_with_its_own_notifications()
    {
        var (holder, watcher) = await TwoPhones();

        holder.Ups.Answer(true, 80);
        await Until(() => watcher.Unit.Dashboard.BatteryValue == "80 %" && watcher.Unit.Dashboard.Link == LinkStatus.Online,
            "the reading on the other phone");
        Assert.Equal(0, watcher.Ups.Reads);
        Assert.Contains($"through {holder.Name}", watcher.Unit.Dashboard.DeviceSubtitle);
        Assert.Contains($"{holder.Name}: connected to Desk", watcher.Controller.Neighbours);
        Assert.Equal("Sharing.", watcher.Controller.SharingStatus);

        holder.Ups.Answer(false, 79);
        await Until(() => !holder.Notifications.Shown.IsEmpty && !watcher.Notifications.Shown.IsEmpty, "Grid lost on both");
        Assert.Equal("Desk", watcher.Notifications.Shown.Single().Ups);
        Assert.Equal(0, watcher.Ups.Reads);
    }

    [Fact]
    public async Task When_the_connected_one_stops_the_other_takes_over()
    {
        var (holder, watcher) = await TwoPhones();
        holder.Ups.Answer(true, 80);
        await Until(() => watcher.Unit.Dashboard.BatteryValue == "80 %", "the reading on the other phone");

        await _ui.Run(holder.Controller.StopAsync);
        await Until(() => watcher.Unit.Route == "via Bluetooth LE", "the other phone to connect");
        watcher.Ups.Answer(true, 77);
        await Until(() => watcher.Unit.Dashboard.BatteryValue == "77 %", "its own reading");
    }

    [Fact]
    public async Task When_the_connected_one_is_unplugged_one_on_mains_power_takes_over()
    {
        var (holder, watcher) = await TwoPhones();
        holder.Ups.Answer(true, 80);
        await Until(() => watcher.Unit.Dashboard.BatteryValue == "80 %", "the reading on the other phone");

        await _ui.Run(() =>
        {
            holder.Controller.SetPowerSource(PowerSource.Battery);
            return true;
        });
        await Until(() => watcher.Unit.Route == "via Bluetooth LE" && holder.Unit.Route == $"through {watcher.Name}",
            "the phone on mains power to take over");
        await Until(() => watcher.Controller.Neighbours.Contains($"{holder.Name} (on battery): not connected to a UPS"),
            "the other phone to say it is on battery");
        watcher.Ups.Answer(true, 77);
        await Until(() => holder.Unit.Dashboard.BatteryValue == "77 %", "the reading through the phone on mains power");
    }

    [Fact]
    public async Task The_ups_another_one_is_connected_to_can_be_paired_and_a_code_is_checked()
    {
        var (holder, _) = await TwoPhones();
        var third = await Start("Phone C");
        await Until(() => third.Controller.NetworkUpses.Count == 1, "the UPS on the network");
        var shared = third.Controller.NetworkUpses.Single();
        Assert.Equal(("Desk", holder.Name, holder.Controller.SharingPort), (shared.Ups.Title, shared.Pc.Pc, (int?)shared.Pc.Port));

        Assert.False(await _ui.Run(() => third.Controller.SetSharingCode("nope")));
        Assert.Equal(Code, third.Controller.Settings.Sharing.Code);
        Assert.True(await _ui.Run(() => third.Controller.SetSharingCode("abcd efgh jkmn pqrs")));
        Assert.Equal("ABCD-EFGH-JKMN-PQRS", third.Controller.Settings.Sharing.Code);
    }
}
