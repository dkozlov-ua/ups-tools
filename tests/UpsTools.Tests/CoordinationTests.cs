using System.Collections.Concurrent;
using System.Net;
using UpsTools.Core;
using UpsTools.Core.Sharing;

namespace UpsTools.Tests;

/// <summary>
/// PCs finding each other and agreeing which one connects to each UPS over Bluetooth (<see cref="UpsCoordinator"/>),
/// on an in-memory network with short timings, and once over real multicast on this PC.
/// </summary>
public sealed class CoordinationTests : IDisposable
{
    private const string Code = "7KQ2-M9XD-4FTB-R1WE";
    private static readonly PairedUps Ups = new("bluetti-ble", "0C:4E:A0:E0:81:E6", "EL100V2ABC", "EL100V2", UserId: "secret");

    private static readonly UpsCoordinator.Timings Fast = new(
        Announce: TimeSpan.FromMilliseconds(100),
        Expiry: TimeSpan.FromMilliseconds(500),
        Claim: TimeSpan.FromMilliseconds(150),
        Release: TimeSpan.FromMilliseconds(400),
        Rest: TimeSpan.FromSeconds(30));

    /// <summary>Delivers each datagram to every other node that is connected, a moment later.</summary>
    private sealed class Network
    {
        public readonly List<Node> Nodes = [];

        public sealed class Node(Network network) : IDiscoveryTransport
        {
            public volatile bool Cut;
            public event Action<byte[], IPAddress>? Received;
            public void Start() { }

            public void Send(byte[] datagram)
            {
                if (Cut) return;
                List<Node> others;
                lock (network.Nodes) others = [.. network.Nodes.Where(n => n != this && !n.Cut)];
                foreach (var node in others) Task.Run(() => node.Received?.Invoke(datagram, IPAddress.Loopback));
            }

            public void Dispose()
            {
                lock (network.Nodes) network.Nodes.Remove(this);
            }
        }

        public Node Join()
        {
            var node = new Node(this);
            lock (Nodes) Nodes.Add(node);
            return node;
        }
    }

    private sealed class Pc(UpsCoordinator coordinator, Network.Node node, ConcurrentQueue<string> log)
    {
        public UpsCoordinator Coordinator { get; } = coordinator;
        public Network.Node Node { get; } = node;
        public ConcurrentQueue<string> Log { get; } = log;
        public UpsRoute Route => Coordinator.RouteOf(Ups);
    }

    private readonly Network _network = new();
    private readonly List<UpsCoordinator> _coordinators = [];

    public void Dispose()
    {
        foreach (var coordinator in _coordinators) coordinator.Dispose();
    }

    private Pc Start(string instance, string? code = null, IDiscoveryTransport? transport = null, int port = 47870)
    {
        var node = _network.Join();
        var log = new ConcurrentQueue<string>();
        var coordinator = new UpsCoordinator(instance, "PC-" + instance.ToUpperInvariant(), code ?? Code, port, transport ?? node, log.Enqueue, Fast);
        _coordinators.Add(coordinator);
        coordinator.Start();
        return new Pc(coordinator, node, log);
    }

    private static async Task Until(Func<bool> condition, string what, double seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what}");
            await Task.Delay(10);
        }
    }

    // ---- announcements

    [Fact]
    public void An_announcement_opens_with_the_same_code_only()
    {
        var now = DateTimeOffset.Now;
        var sent = new Announcement("abc", "DESKTOP-1", 47870,
            [new AnnouncedUps("bluetti-ble/0C", "Desk", Ups with { UserId = null }, UpsRole.Holding, Online: true)], Probe: true);
        var datagram = sent.Seal(SharingCode.DiscoveryKey(Code), SharingCode.Fingerprint(Code), now);

        var opened = Announcement.Open(datagram, SharingCode.DiscoveryKey(Code), SharingCode.Fingerprint(Code), now, out var foreign);
        Assert.Null(foreign);
        Assert.Equal(sent.Instance, opened!.Instance);
        Assert.Equal(sent.Upses, opened.Upses);
        Assert.True(opened.Probe);

        // Another code: who sent it, nothing more.
        var other = SharingCode.New();
        Assert.Null(Announcement.Open(datagram, SharingCode.DiscoveryKey(other), SharingCode.Fingerprint(other), now, out foreign));
        Assert.Equal(("abc", "DESKTOP-1"), foreign);
    }

    [Fact]
    public void A_changed_or_old_announcement_is_ignored()
    {
        var now = DateTimeOffset.Now;
        var key = SharingCode.DiscoveryKey(Code);
        var fingerprint = SharingCode.Fingerprint(Code);
        var datagram = new Announcement("abc", "DESKTOP-1", 47870, []).Seal(key, fingerprint, now);

        var changed = (byte[])datagram.Clone();
        changed[^20] ^= 1;
        Assert.Null(Announcement.Open(changed, key, fingerprint, now, out var foreign));
        Assert.Null(foreign);

        Assert.Null(Announcement.Open(datagram, key, fingerprint, now + TimeSpan.FromMinutes(11), out _)); // played back later
        Assert.Null(Announcement.Open([1, 2, 3], key, fingerprint, now, out _));
    }

    // ---- deciding

    [Fact]
    public async Task Alone_it_connects_after_a_moment()
    {
        var a = Start("a");
        a.Coordinator.Join(Ups, "Desk");
        Assert.Equal(UpsRoute.Waiting, a.Route);

        await Until(() => a.Route == UpsRoute.Direct, "the claim");
        Assert.Contains("No other PC sharing on the network is connected to Desk: connecting over Bluetooth", a.Log);
    }

    [Fact]
    public async Task Of_two_starting_together_one_connects_and_the_other_reads_through_it()
    {
        var a = Start("a");
        var b = Start("b");
        b.Coordinator.Join(Ups, "Desk");
        a.Coordinator.Join(Ups, "Desk");

        await Until(() => a.Route == UpsRoute.Direct && IsThrough(b, "PC-A"), "a to connect and b to read through it");
        await Task.Delay(400);
        Assert.Equal(UpsRoute.Direct, a.Route);
        Assert.True(IsThrough(b, "PC-A"));
        Assert.Contains("PC-A is connected to Desk: reading it through that PC", b.Log);
    }

    [Fact]
    public async Task A_pc_that_comes_later_never_connects_while_another_is()
    {
        var b = Start("b");
        b.Coordinator.Join(Ups, "Desk");
        await Until(() => b.Route == UpsRoute.Direct, "b to connect");

        var a = Start("a"); // first in order, but b is connected already
        var routes = new ConcurrentQueue<UpsRoute>();
        a.Coordinator.RouteChanged += (_, route) => routes.Enqueue(route);
        a.Coordinator.Join(Ups, "Desk");

        await Until(() => IsThrough(a, "PC-B"), "a to read through b");
        await Task.Delay(400);
        Assert.DoesNotContain(UpsRoute.Direct, routes);
        Assert.Equal(UpsRoute.Direct, b.Route);
    }

    [Fact]
    public async Task When_the_connected_pc_stops_another_takes_over_at_once()
    {
        var a = Start("a");
        var b = Start("b");
        a.Coordinator.Join(Ups, "Desk");
        b.Coordinator.Join(Ups, "Desk");
        await Until(() => IsThrough(b, "PC-A"), "b to read through a");

        a.Coordinator.Dispose(); // says goodbye
        await Until(() => b.Route == UpsRoute.Direct, "b to connect", seconds: 0.45); // sooner than the expiry
    }

    [Fact]
    public async Task When_the_connected_pc_goes_quiet_another_takes_over_after_a_few_missed_announcements()
    {
        var a = Start("a");
        var b = Start("b");
        a.Coordinator.Join(Ups, "Desk");
        b.Coordinator.Join(Ups, "Desk");
        await Until(() => IsThrough(b, "PC-A"), "b to read through a");

        a.Node.Cut = true;
        await Until(() => b.Route == UpsRoute.Direct, "b to connect");
        Assert.Contains("PC-A went quiet: no longer counting on it", b.Log);
    }

    [Fact]
    public async Task Of_two_connected_already_the_later_one_lets_go()
    {
        var b = Start("b");
        var a = Start("a");
        a.Coordinator.Join(Ups, "Desk", connected: true);
        b.Coordinator.Join(Ups, "Desk", connected: true);

        await Until(() => IsThrough(b, "PC-A"), "b to let go");
        Assert.Equal(UpsRoute.Direct, a.Route);
        Assert.Contains("PC-A is connected to Desk too: leaving it to that PC", b.Log);
    }

    [Fact]
    public async Task A_pc_that_cannot_reach_the_ups_lets_another_try()
    {
        var a = Start("a");
        var b = Start("b");
        a.Coordinator.Join(Ups, "Desk");
        b.Coordinator.Join(Ups, "Desk");
        await Until(() => IsThrough(b, "PC-A"), "b to read through a");

        a.Coordinator.ReportLink(Ups, online: false); // e.g. out of range
        await Until(() => b.Route == UpsRoute.Direct && IsThrough(a, "PC-B"), "b to connect and a to read through it");
        Assert.Contains("Desk has been unreachable from this PC for a while: letting another PC try", a.Log);
    }

    [Fact]
    public async Task Unreachable_and_alone_it_keeps_trying()
    {
        var a = Start("a");
        a.Coordinator.Join(Ups, "Desk");
        await Until(() => a.Route == UpsRoute.Direct, "a to connect");
        a.Coordinator.ReportLink(Ups, online: false);
        await Task.Delay(700);
        Assert.Equal(UpsRoute.Direct, a.Route);
    }

    [Fact]
    public async Task A_ups_only_one_pc_has_is_that_pcs_alone()
    {
        var other = Ups with { Address = "11:22:33:44:55:66" };
        var a = Start("a");
        var b = Start("b");
        a.Coordinator.Join(Ups, "Desk");
        b.Coordinator.Join(other, "Rack");
        await Until(() => a.Route == UpsRoute.Direct && b.Coordinator.RouteOf(other) == UpsRoute.Direct, "both to connect");
    }

    [Fact]
    public async Task The_other_pcs_and_the_upses_they_are_connected_to_are_known()
    {
        var a = Start("a");
        var b = Start("b");
        var stranger = Start("c", code: SharingCode.New());
        a.Coordinator.Join(Ups, "Desk");
        await Until(() => a.Route == UpsRoute.Direct, "a to connect");

        await Until(() => b.Coordinator.Neighbours.Any(n => n.Pc == "PC-A" && n.SameCode && n.Holding.SequenceEqual(["Desk"]))
            && b.Coordinator.Neighbours.Any(n => n.Pc == "PC-C" && !n.SameCode), "the neighbours");
        var shared = Assert.Single(b.Coordinator.NetworkUpses);
        Assert.Equal(("Desk", "PC-A", 47870), (shared.Ups.Title, shared.Pc.Pc, shared.Pc.Port));
        Assert.Null(shared.Ups.Device.UserId); // the account stays on its PC
        Assert.Empty(stranger.Coordinator.NetworkUpses);
    }

    private static bool IsThrough(Pc pc, string holder) => pc.Route is { Kind: UpsRouteKind.Through, Peer: { } peer } && peer.Pc == holder;

    // ---- over real multicast

    [Fact]
    public async Task Pcs_find_each_other_over_multicast()
    {
        var port = Random.Shared.Next(40000, 47000);
        var a = Start("a", transport: new MulticastTransport(port, loopbackOnly: true), port: 1234);
        var b = Start("b", transport: new MulticastTransport(port, loopbackOnly: true));
        a.Coordinator.Join(Ups, "Desk");
        b.Coordinator.Join(Ups, "Desk");

        await Until(() => a.Route == UpsRoute.Direct && IsThrough(b, "PC-A"), "a to connect and b to read through it");
        Assert.Equal(new PeerPc("a", "PC-A", IPAddress.Loopback, 1234), b.Route.Peer);
    }

    // ---- reading the way the route says

    [Fact]
    public async Task Reads_follow_the_route()
    {
        var route = UpsRoute.Direct;
        var direct = new Reader(80);
        var network = new Reader(50);
        var peer = new PeerPc("a", "PC-A", IPAddress.Loopback, 1);
        var connection = new CoordinatedUpsConnection(direct, () => route, p => p == peer ? network : throw new InvalidOperationException());

        Assert.Equal(80, (await connection.ReadAsync(default)).BatteryPercent);
        Assert.True(connection.LastReadDirect);
        Assert.False(connection.WaitsForReadings);

        route = UpsRoute.Through(peer);
        connection.RouteChanged();
        Assert.Equal(1, direct.Resets); // the Bluetooth link is let go of at once
        Assert.Equal(50, (await connection.ReadAsync(default)).BatteryPercent);
        Assert.False(connection.LastReadDirect);
        Assert.True(connection.WaitsForReadings);

        // Waiting: no read until it is decided, and then without failing.
        route = UpsRoute.Waiting;
        connection.RouteChanged();
        var read = connection.ReadAsync(default);
        await Task.Delay(100);
        Assert.False(read.IsCompleted);
        Assert.True(network.Disposed);
        route = UpsRoute.Direct;
        connection.RouteChanged();
        Assert.Equal(80, (await read.WaitAsync(TimeSpan.FromSeconds(5))).BatteryPercent);
    }

    [Fact]
    public async Task A_read_in_progress_ends_without_failing_when_the_route_changes()
    {
        var route = UpsRoute.Direct;
        var direct = new Reader(80) { Hang = true };
        var peer = new PeerPc("a", "PC-A", IPAddress.Loopback, 1);
        var connection = new CoordinatedUpsConnection(direct, () => route, _ => new Reader(50));

        var read = connection.ReadAsync(default);
        await Task.Delay(100);
        route = UpsRoute.Through(peer);
        connection.RouteChanged();
        Assert.Equal(50, (await read.WaitAsync(TimeSpan.FromSeconds(5))).BatteryPercent);
    }

    private sealed class Reader(int battery) : IUpsConnection
    {
        public bool Hang;
        public int Resets;
        public bool Disposed;

        public async Task<UpsStatus> ReadAsync(CancellationToken cancellationToken)
        {
            if (Hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new UpsStatus { Timestamp = DateTimeOffset.Now, BatteryPercent = battery };
        }

        public Task ResetAsync()
        {
            Resets++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
