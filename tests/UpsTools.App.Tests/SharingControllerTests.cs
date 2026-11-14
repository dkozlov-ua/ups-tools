using System.Globalization;
using UpsTools.AppTests.Support;
using UpsTools.Core;
using UpsTools.Core.Sharing;
using UpsTools.Services;
using UpsTools.ViewModels;

namespace UpsTools.AppTests;

/// <summary>
/// Two copies of the app with the same UPS paired and sharing on (as on two PCs, here over multicast on this PC):
/// only one connects to the UPS, the other reads it through that one and still runs its own scripts.
/// </summary>
[Collection("Registry")] // the configuration reads the Start with Windows entry
public sealed class SharingControllerTests : IDisposable
{
    private const string Code = "7KQ2-M9XD-4FTB-R1WE";

    private sealed class Copy(TempFolder folder, AppLog log, FakeUps ups, FakeScripts scripts)
    {
        public TempFolder Folder { get; } = folder;
        public AppLog Log { get; } = log;
        public FakeUps Ups { get; } = ups;
        public FakeScripts Scripts { get; } = scripts;
        public AppController Controller { get; set; } = null!;
        public UpsUnit Unit => Controller.Upses[0];
        public string Route => Unit.Configuration.Route;
    }

    private readonly int _port = Random.Shared.Next(40000, 47000);
    private readonly List<Copy> _copies = [];

    static SharingControllerTests() =>
        Environment.SetEnvironmentVariable(SharingServer.LoopbackEnvironmentVariable, "1"); // no firewall prompt

    public SharingControllerTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        AppController.CoordinatorTimings = new UpsCoordinator.Timings(
            Announce: TimeSpan.FromMilliseconds(200), Expiry: TimeSpan.FromSeconds(1), Claim: TimeSpan.FromMilliseconds(300),
            Release: TimeSpan.FromSeconds(1), Rest: TimeSpan.FromSeconds(30));
    }

    public void Dispose()
    {
        foreach (var copy in _copies)
        {
            UiThread.Run(() =>
            {
                copy.Controller.Sharing.Enabled = false;
                return copy.Controller.StopMonitoringAsync();
            }).Wait(TimeSpan.FromSeconds(10));
            copy.Folder.Dispose();
        }
        AppController.CoordinatorTimings = UpsCoordinator.Timings.Default;
    }

    /// <summary>Starts a copy as the app does: sharing first, then monitoring.</summary>
    private async Task<Copy> Start(string script)
    {
        var folder = new TempFolder();
        var log = new AppLog(folder.Path);
        var ups = new UpsSettings { Id = "u" + _copies.Count, Device = FakeUps.Paired, Label = "Desk" };
        ups.ScriptsFor(UpsEventKind.GridLost).Add(new ScriptAction { Path = $@"C:\scripts\{script}" });
        new SettingsStore(log, folder.Path).Save(new AppSettings
        {
            Upses = [ups],
            UpdatePeriodSeconds = 0, // the fake UPS sets the pace
            Sharing = new SharingSettings { Enabled = true, Code = Code, Port = _port },
        });

        var copy = new Copy(folder, log, new FakeUps(), new FakeScripts());
        copy.Controller = await UiThread.Run(() =>
        {
            var controller = new AppController(UiThread.Dispatcher, log, [copy.Ups], copy.Scripts.RunAsync, new FakePower(), new FakeCountdown().ShowAsync);
            controller.AttachTray(new FakeTray(), new FakeNotifications());
            controller.StartSharing();
            controller.StartMonitoring();
            return controller;
        });
        _copies.Add(copy);
        return copy;
    }

    private static async Task Until(Func<bool> condition, string what, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await UiThread.Run(condition))
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what}");
            await Task.Delay(20);
        }
    }

    private async Task<(Copy Holder, Copy Watcher)> TwoCopies()
    {
        var a = await Start("a.ps1");
        var b = await Start("b.ps1");
        await Until(() => new[] { a.Route, b.Route }.Order().SequenceEqual([$"through {Environment.MachineName}", "via Bluetooth LE"]),
            "one to connect and the other to read through it");
        return a.Route == "via Bluetooth LE" ? (a, b) : (b, a);
    }

    [Fact]
    public async Task Only_one_copy_connects_and_the_other_reads_through_it()
    {
        var (holder, watcher) = await TwoCopies();

        holder.Ups.Answer(true, 80);
        await Until(() => watcher.Unit.Dashboard.BatteryValue == "80 %" && watcher.Unit.Dashboard.Link == LinkStatus.Online, "the reading on the other copy");
        Assert.Equal(0, watcher.Ups.Reads); // never connected to the UPS itself
        Assert.Contains($"through {Environment.MachineName}", watcher.Unit.Dashboard.DeviceSubtitle);
        Assert.Contains(watcher.Log.Recent, e => e.Message == $"{Environment.MachineName} is connected to Desk: reading it through that PC");
        Assert.Contains(watcher.Controller.Sharing.Neighbours, n => n == $"{Environment.MachineName}: connected to Desk");

        // Each runs its own scripts on the outage.
        holder.Ups.Answer(false, 79);
        await Until(() => holder.Scripts.Ran.Any(r => r.Name == "a.ps1" || r.Name == "b.ps1")
            && watcher.Scripts.Ran.Any(r => r.Name == "a.ps1" || r.Name == "b.ps1"), "both copies' Grid lost scripts");
        Assert.Equal(0, watcher.Ups.Reads);
    }

    [Fact]
    public async Task When_the_connected_copy_stops_sharing_the_other_takes_over()
    {
        var (holder, watcher) = await TwoCopies();
        holder.Ups.Answer(true, 80);
        await Until(() => watcher.Unit.Dashboard.BatteryValue == "80 %", "the reading on the other copy");

        await UiThread.Run(() => holder.Controller.Sharing.Enabled = false);
        await Until(() => watcher.Route == "via Bluetooth LE", "the other copy to connect");
        watcher.Ups.Answer(true, 77);
        await Until(() => watcher.Unit.Dashboard.BatteryValue == "77 %", "its own reading");
    }

    [Fact]
    public async Task The_upses_other_copies_are_connected_to_can_be_paired()
    {
        var (holder, _) = await TwoCopies();
        var third = await Start("c.ps1");
        await Until(() => third.Controller.NetworkUpses.Count == 1, "the UPS on the network");
        var shared = third.Controller.NetworkUpses.Single();
        Assert.Equal(("Desk", FakeUps.Paired.Address), (shared.Ups.Title, shared.Ups.Device.Address));
        Assert.Equal(holder.Controller.SharingPort, shared.Pc.Port);
    }

    [Fact]
    public async Task A_new_code_is_used_at_once()
    {
        var copy = await Start("a.ps1");
        await UiThread.Run(copy.Controller.Sharing.NewCode);
        var code = copy.Controller.Settings.Sharing.Code!;
        Assert.NotEqual(Code, code);

        using var client = await SharingClient.ConnectAsync(System.Net.IPAddress.Loopback, copy.Controller.SharingPort!.Value, code, "me", default);
        Assert.Equal(copy.Controller.Hub.Instance, (await client.ListAsync(default)).Instance);
    }

    [Fact]
    public async Task A_code_typed_in_is_checked_and_kept_formatted()
    {
        var copy = await Start("a.ps1");
        var sharing = copy.Controller.Sharing;
        await UiThread.Run(() => sharing.Code = "nope");
        Assert.Equal(Code, sharing.Code);
        Assert.True(sharing.HasProblem);

        await UiThread.Run(() => sharing.Code = "abcd efgh jkmn pqrs");
        Assert.Equal("ABCD-EFGH-JKMN-PQRS", copy.Controller.Settings.Sharing.Code);
        Assert.False(sharing.HasProblem);
    }
}
