using System.Net;

namespace UpsTools.Core.Sharing;

/// <summary>Another PC running UPS Tools with the same sharing code, where its readings can be asked for.</summary>
public sealed record PeerPc(string Instance, string Pc, IPAddress Address, int Port);

public enum UpsRouteKind
{
    /// <summary>Over Bluetooth: this PC is the one connected to the UPS.</summary>
    Direct,

    /// <summary>Through the PC connected to the UPS (<see cref="UpsRoute.Peer"/>).</summary>
    Through,

    /// <summary>Not yet decided, or the UPS is let go of for a while: no read.</summary>
    Waiting,
}

/// <summary>How this PC reads a UPS.</summary>
public sealed record UpsRoute(UpsRouteKind Kind, PeerPc? Peer = null)
{
    public static readonly UpsRoute Direct = new(UpsRouteKind.Direct);
    public static readonly UpsRoute Waiting = new(UpsRouteKind.Waiting);

    public static UpsRoute Through(PeerPc peer) => new(UpsRouteKind.Through, peer);
}

/// <summary>Another PC heard on the network, for the configuration page.</summary>
/// <param name="SameCode">It has this PC's sharing code; otherwise neither can read the other's UPSes.</param>
/// <param name="Holding">What it calls the UPSes it is connected to.</param>
public sealed record NeighbourPc(string Pc, bool SameCode, IReadOnlyList<string> Holding);

/// <summary>A UPS another PC is connected to, for the pairing dialog.</summary>
public sealed record NetworkUps(AnnouncedUps Ups, PeerPc Pc);

/// <summary>
/// Makes sure only one PC is connected to a UPS over Bluetooth (it accepts a single connection), and that the other
/// PCs that have it paired read it through that PC. The PCs with the same sharing code find each other on their own:
/// each tells the others (<see cref="Announcement"/>, over <see cref="IDiscoveryTransport"/>) what it does with each
/// UPS it has paired (<see cref="UpsRole"/>).
/// </summary>
/// <remarks>
/// <para>For each UPS: if another PC is connected to it, this PC reads through that one. If none is, the PCs that have it
/// paired say they want it, wait a moment to hear each other, and the one with the lowest instance ID connects.
/// Should two end up connected (e.g. two networks joined), the one with the higher ID lets go.</para>
/// <para>A PC that is connected but has had the UPS unreachable for a while (e.g. out of Bluetooth range) lets go if
/// another PC has it paired, and rests before wanting it again, so that another PC can try.</para>
/// <para>A PC that stops (it exits, sleeps or turns sharing off) says goodbye, and the others take over at once; one
/// that disappears without a word is forgotten after a few missed announcements.</para>
/// </remarks>
public sealed class UpsCoordinator : IDisposable
{
    public sealed record Timings(TimeSpan Announce, TimeSpan Expiry, TimeSpan Claim, TimeSpan Release, TimeSpan Rest)
    {
        public static readonly Timings Default = new(
            Announce: TimeSpan.FromSeconds(5),
            Expiry: TimeSpan.FromSeconds(17),
            Claim: TimeSpan.FromSeconds(3),
            Release: TimeSpan.FromMinutes(2),
            Rest: TimeSpan.FromMinutes(5));
    }

    private sealed class Peer(PeerPc pc)
    {
        public PeerPc Pc { get; set; } = pc;
        public DateTimeOffset LastSeen { get; set; }
        public IReadOnlyList<AnnouncedUps> Upses { get; set; } = [];

        public UpsRole? RoleOf(string key) => Upses.FirstOrDefault(u => u.Key == key)?.Role;
    }

    private sealed class Local(PairedUps device, string title)
    {
        public PairedUps Device { get; } = device;
        public string Key { get; } = SharingHub.KeyOf(device);
        public string Title { get; set; } = title;
        public UpsRole Role { get; set; }
        public DateTimeOffset RoleSince { get; set; }
        public bool Online { get; set; }
        public DateTimeOffset? UnreachableSince { get; set; }
        public UpsRoute Route { get; set; } = UpsRoute.Waiting;
    }

    private readonly string _instance;
    private readonly string _pc;
    private readonly int _port;
    private readonly byte[] _key;
    private readonly byte[] _fingerprint;
    private readonly IDiscoveryTransport _transport;
    private readonly Action<string>? _log;
    private readonly Timings _timings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Peer> _peers = [];
    private readonly Dictionary<string, (string Pc, DateTimeOffset LastSeen)> _foreign = [];
    private readonly Dictionary<string, Local> _locals = [];
    private readonly CancellationTokenSource _stop = new();
    private DateTimeOffset _nextAnnounce;
    private bool _announceSoon;
    private bool _probe = true;
    private bool _started;

    /// <param name="instance">This running copy (<see cref="SharingHub.Instance"/>).</param>
    /// <param name="pc">What the other PCs call this one.</param>
    /// <param name="port">The TCP port of this PC's <see cref="SharingServer"/>.</param>
    public UpsCoordinator(string instance, string pc, string code, int port, IDiscoveryTransport transport, Action<string>? log = null,
        Timings? timings = null, Func<DateTimeOffset>? clock = null)
    {
        _instance = instance;
        _pc = pc;
        _port = port;
        _key = SharingCode.DiscoveryKey(code);
        _fingerprint = SharingCode.Fingerprint(code);
        _transport = transport;
        _log = log;
        _timings = timings ?? Timings.Default;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>A UPS's route changed; raised on a background thread, or on the caller's of <see cref="Join"/> and the like.</summary>
    public event Action<PairedUps, UpsRoute>? RouteChanged;

    /// <summary>The PCs heard, or what they are connected to, changed; raised on a background thread.</summary>
    public event Action? NeighboursChanged;

    public void Start()
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
        }
        _transport.Received += OnReceived;
        _transport.Start();
        _ = LoopAsync();
    }

    // ---- this PC's UPSes

    /// <summary>Coordinates a UPS from now on.</summary>
    /// <param name="connected">This PC is connected to it already (sharing was just turned on): it keeps the link unless another PC has it too.</param>
    public void Join(PairedUps device, string title, bool connected = false)
    {
        lock (_lock)
        {
            var local = new Local(device, title) { Online = connected };
            SetRole(local, connected ? UpsRole.Holding : UpsRole.Wanting, _clock());
            _locals[local.Key] = local;
        }
        Evaluate();
    }

    /// <summary>Stops coordinating a UPS (it is forgotten, or no longer monitored).</summary>
    public void Leave(PairedUps device)
    {
        lock (_lock)
        {
            if (!_locals.Remove(SharingHub.KeyOf(device))) return;
            _announceSoon = true;
        }
    }

    public void Rename(PairedUps device, string title)
    {
        lock (_lock)
        {
            if (!_locals.TryGetValue(SharingHub.KeyOf(device), out var local) || local.Title == title) return;
            local.Title = title;
            _announceSoon = true;
        }
    }

    /// <summary>While this PC is connected to the UPS: whether it answers.</summary>
    public void ReportLink(PairedUps device, bool online)
    {
        lock (_lock)
        {
            if (!_locals.TryGetValue(SharingHub.KeyOf(device), out var local) || local.Role != UpsRole.Holding) return;
            if (online) local.UnreachableSince = null;
            else local.UnreachableSince ??= _clock();
            if (local.Online == online) return;
            local.Online = online;
            _announceSoon = true;
        }
    }

    /// <summary>How this PC reads the UPS; <see cref="UpsRoute.Direct"/> for one it doesn't coordinate.</summary>
    public UpsRoute RouteOf(PairedUps device)
    {
        lock (_lock) return _locals.TryGetValue(SharingHub.KeyOf(device), out var local) ? local.Route : UpsRoute.Direct;
    }

    // ---- the other PCs

    public IReadOnlyList<NeighbourPc> Neighbours
    {
        get
        {
            lock (_lock)
                return [.. _peers.Values.Select(p => new NeighbourPc(p.Pc.Pc, true,
                        [.. p.Upses.Where(u => u.Role == UpsRole.Holding).Select(u => u.Title)]))
                    .Concat(_foreign.Values.Select(f => new NeighbourPc(f.Pc, false, [])))
                    .OrderBy(n => n.Pc, StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>The UPSes other PCs are connected to.</summary>
    public IReadOnlyList<NetworkUps> NetworkUpses
    {
        get
        {
            lock (_lock)
                return [.. _peers.Values.SelectMany(p => p.Upses.Where(u => u.Role == UpsRole.Holding).Select(u => new NetworkUps(u, p.Pc)))
                    .GroupBy(n => n.Ups.Key).Select(g => g.OrderBy(n => n.Pc.Instance, StringComparer.Ordinal).First())];
        }
    }

    private void OnReceived(byte[] datagram, IPAddress from)
    {
        var now = _clock();
        var announcement = Announcement.Open(datagram, _key, _fingerprint, now, out var foreign);
        var neighboursChanged = false;
        string? found = null;
        lock (_lock)
        {
            if (foreign is { } other)
            {
                neighboursChanged = !_foreign.ContainsKey(other.Instance);
                _foreign[other.Instance] = (other.Pc, now);
            }
            else if (announcement is not null && announcement.Instance != _instance)
            {
                if (announcement.Bye)
                {
                    neighboursChanged = _peers.Remove(announcement.Instance);
                }
                else
                {
                    var pc = new PeerPc(announcement.Instance, announcement.Pc, from.IsIPv4MappedToIPv6 ? from.MapToIPv4() : from, announcement.Port);
                    if (!_peers.TryGetValue(announcement.Instance, out var peer))
                    {
                        _peers[announcement.Instance] = peer = new Peer(pc);
                        neighboursChanged = true;
                        found = $"Found {pc.Pc} sharing on the network";
                    }
                    neighboursChanged |= !peer.Upses.SequenceEqual(announcement.Upses) || peer.Pc != pc;
                    (peer.Pc, peer.LastSeen, peer.Upses) = (pc, now, announcement.Upses);
                    if (announcement.Probe) _announceSoon = true; // it just started: tell it what this PC does
                }
            }
        }
        if (found is not null) _log?.Invoke(found);
        if (neighboursChanged) NeighboursChanged?.Invoke();
        if (announcement is not null) Evaluate();
    }

    // ---- deciding

    private async Task LoopAsync()
    {
        var tick = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(1).Ticks, Math.Min(_timings.Announce.Ticks, _timings.Claim.Ticks) / 3));
        while (!_stop.IsCancellationRequested)
        {
            Evaluate();
            try
            {
                await Task.Delay(tick, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Decides every UPS's route anew, tells the other PCs if anything changed (or it's time to), and raises the events.</summary>
    internal void Evaluate()
    {
        var now = _clock();
        var changes = new List<(PairedUps, UpsRoute)>();
        var logs = new List<string>();
        var neighboursChanged = false;
        byte[]? announcement = null;
        lock (_lock)
        {
            if (_stop.IsCancellationRequested) return;

            foreach (var gone in _peers.Values.Where(p => now - p.LastSeen > _timings.Expiry).ToList())
            {
                _peers.Remove(gone.Pc.Instance);
                logs.Add($"{gone.Pc.Pc} went quiet: no longer counting on it");
                neighboursChanged = true;
            }
            foreach (var gone in _foreign.Where(f => now - f.Value.LastSeen > _timings.Expiry).Select(f => f.Key).ToList())
            {
                _foreign.Remove(gone);
                neighboursChanged = true;
            }

            foreach (var local in _locals.Values)
            {
                var route = Decide(local, now, logs);
                if (route == local.Route) continue;
                local.Route = route;
                changes.Add((local.Device, route));
            }

            if (_started && (_announceSoon || now >= _nextAnnounce))
            {
                announcement = Announce(now, bye: false);
                _announceSoon = false;
                _nextAnnounce = now + _timings.Announce;
            }
        }

        if (announcement is not null) _transport.Send(announcement);
        foreach (var message in logs) _log?.Invoke(message);
        if (neighboursChanged) NeighboursChanged?.Invoke();
        foreach (var (device, route) in changes) RouteChanged?.Invoke(device, route);
    }

    private UpsRoute Decide(Local local, DateTimeOffset now, List<string> logs)
    {
        var fresh = _peers.Values.ToList();
        var holders = fresh.Where(p => p.RoleOf(local.Key) == UpsRole.Holding)
            .OrderBy(p => p.Pc.Instance, StringComparer.Ordinal).ToList();

        switch (local.Role)
        {
            case UpsRole.Holding:
                if (holders.FirstOrDefault(h => Before(h.Pc.Instance, _instance)) is { } other)
                {
                    logs.Add($"{other.Pc.Pc} is connected to {local.Title} too: leaving it to that PC");
                    SetRole(local, UpsRole.Watching, now);
                }
                else if (local.UnreachableSince is { } since && now - since >= _timings.Release
                    && fresh.Any(p => p.RoleOf(local.Key) is UpsRole.Wanting or UpsRole.Watching))
                {
                    logs.Add($"{local.Title} has been unreachable from this PC for a while: letting another PC try");
                    SetRole(local, UpsRole.Resting, now);
                }
                break;

            case UpsRole.Resting when now - local.RoleSince >= _timings.Rest:
                SetRole(local, UpsRole.Wanting, now);
                break;
        }

        if (local.Role is UpsRole.Wanting or UpsRole.Watching)
        {
            if (holders.Count > 0)
            {
                if (local.Role != UpsRole.Watching) logs.Add($"{holders[0].Pc.Pc} is connected to {local.Title}: reading it through that PC");
                SetRole(local, UpsRole.Watching, now);
            }
            else if (local.Role == UpsRole.Watching)
            {
                SetRole(local, UpsRole.Wanting, now); // its PC let go of it, or went away
            }

            // Nobody is connected: after hearing the others for a moment, the first of those that want it connects.
            if (local.Role == UpsRole.Wanting && now - local.RoleSince >= _timings.Claim
                && fresh.Where(p => p.RoleOf(local.Key) == UpsRole.Wanting).All(p => Before(_instance, p.Pc.Instance)))
            {
                logs.Add(fresh.Count == 0
                    ? $"No other PC sharing on the network is connected to {local.Title}: connecting over Bluetooth"
                    : $"No other PC is connected to {local.Title}: connecting over Bluetooth");
                SetRole(local, UpsRole.Holding, now);
            }
        }

        return local.Role switch
        {
            UpsRole.Holding => UpsRoute.Direct,
            _ when holders.Count > 0 => UpsRoute.Through(holders[0].Pc),
            _ => UpsRoute.Waiting,
        };
    }

    private void SetRole(Local local, UpsRole role, DateTimeOffset now)
    {
        if (local.Role == role && local.RoleSince != default) return;
        local.Role = role;
        local.RoleSince = now;
        if (role != UpsRole.Holding) (local.Online, local.UnreachableSince) = (false, null);
        _announceSoon = true;
    }

    private static bool Before(string a, string b) => string.CompareOrdinal(a, b) < 0;

    private byte[] Announce(DateTimeOffset now, bool bye)
    {
        var upses = bye ? [] : _locals.Values.Select(l => new AnnouncedUps(l.Key, l.Title, l.Device with { UserId = null }, l.Role,
            l.Role == UpsRole.Holding && l.Online)).ToList();
        var probe = _probe;
        _probe = false;
        return new Announcement(_instance, _pc, _port, upses, Probe: probe && !bye, Bye: bye).Seal(_key, _fingerprint, now);
    }

    /// <summary>Says goodbye (the other PCs take over the UPSes at once) and stops.</summary>
    public void Dispose()
    {
        byte[]? bye = null;
        lock (_lock)
        {
            if (_stop.IsCancellationRequested) return;
            if (_started) bye = Announce(_clock(), bye: true);
            _stop.Cancel();
        }
        if (bye is not null) _transport.Send(bye);
        _transport.Received -= OnReceived;
        _transport.Dispose();
    }
}
