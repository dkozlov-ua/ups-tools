namespace UpsTools.Core.Sharing;

/// <summary>
/// A UPS read the way <see cref="UpsCoordinator"/> says: over Bluetooth while this PC is the one connected to it,
/// through the PC that is otherwise, and not at all while that is being decided. A change of route ends a read in
/// progress without failing it, and the Bluetooth link is closed as soon as it isn't the way any more, so that
/// another PC can connect.
/// </summary>
public sealed class CoordinatedUpsConnection : IUpsConnection
{
    /// <summary>A Bluetooth read's limit, also when the monitor set none (the read began on another route).</summary>
    private static readonly TimeSpan DirectReadTimeout = TimeSpan.FromSeconds(30);

    private readonly IUpsConnection _direct;
    private readonly Func<UpsRoute> _route;
    private readonly Func<PeerPc, IUpsConnection> _through;
    private readonly Lock _lock = new();
    private CancellationTokenSource _changed = new();
    private UpsRoute? _applied; // the route of the read in progress, or of the last one
    private IUpsConnection? _network;
    private PeerPc? _networkPeer;
    private bool _directOpen;

    /// <param name="direct">The Bluetooth connection.</param>
    /// <param name="route">The route now (<see cref="UpsCoordinator.RouteOf"/>).</param>
    /// <param name="through">Makes a connection through another PC (<see cref="NetworkUpsConnection"/>).</param>
    public CoordinatedUpsConnection(IUpsConnection direct, Func<UpsRoute> route, Func<PeerPc, IUpsConnection> through)
    {
        _direct = direct;
        _route = route;
        _through = through;
    }

    public bool WaitsForReadings => _route().Kind != UpsRouteKind.Direct;

    /// <summary>The last read (or the one in progress) went to the UPS itself: its readings are this PC's to share.</summary>
    public bool LastReadDirect { get; private set; }

    /// <summary>Call when <see cref="UpsCoordinator.RouteOf"/> may have changed; nothing happens if it didn't.</summary>
    public void RouteChanged()
    {
        CancellationTokenSource old;
        UpsRoute route;
        lock (_lock)
        {
            route = _route();
            if (route == _applied) return;
            _applied = route;
            old = _changed;
            _changed = new CancellationTokenSource();
        }
        old.Cancel();
        if (route.Kind != UpsRouteKind.Direct) _ = CloseDirectAsync();
    }

    public async Task<UpsStatus> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            CancellationToken changed;
            UpsRoute route;
            lock (_lock)
            {
                changed = _changed.Token;
                _applied = route = _route();
            }
            using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, changed);
            try
            {
                switch (route.Kind)
                {
                    case UpsRouteKind.Direct:
                        LastReadDirect = true;
                        await DropNetworkAsync();
                        _directOpen = true;
                        read.CancelAfter(DirectReadTimeout);
                        return await _direct.ReadAsync(read.Token);

                    case UpsRouteKind.Through:
                        LastReadDirect = false;
                        await CloseDirectAsync();
                        if (_networkPeer != route.Peer)
                        {
                            await DropNetworkAsync();
                            (_network, _networkPeer) = (_through(route.Peer!), route.Peer);
                        }
                        return await _network!.ReadAsync(read.Token);

                    default:
                        LastReadDirect = false;
                        await CloseDirectAsync();
                        await DropNetworkAsync();
                        await Task.Delay(Timeout.Infinite, read.Token);
                        break;
                }
            }
            catch (Exception) when (changed.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The route changed mid-read: read the new way.
            }
        }
    }

    private async Task CloseDirectAsync()
    {
        if (!_directOpen) return;
        _directOpen = false;
        await _direct.ResetAsync();
    }

    private async Task DropNetworkAsync()
    {
        var network = _network;
        (_network, _networkPeer) = (null, null);
        if (network is not null) await network.DisposeAsync();
    }

    public async Task ResetAsync()
    {
        _directOpen = false;
        await _direct.ResetAsync();
        if (_network is { } network) await network.ResetAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DropNetworkAsync();
        await _direct.DisposeAsync();
    }
}
