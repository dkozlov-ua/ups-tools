using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace UpsTools.Core.Sharing;

/// <summary>Carries <see cref="Announcement"/>s between the PCs of a network.</summary>
public interface IDiscoveryTransport : IDisposable
{
    /// <summary>A datagram arrived from <c>from</c>; raised on a background thread.</summary>
    event Action<byte[], IPAddress>? Received;

    void Start();

    /// <summary>Sends a datagram to every PC listening; failures are ignored (the next one goes out a few seconds later).</summary>
    void Send(byte[] datagram);
}

/// <summary>
/// UDP multicast on 239.255.47.70 (a group local to the organisation's network, which routers don't forward
/// further), on every network the PC is on, so that UPS Tools on another PC hears it without being told where to look.
/// </summary>
public sealed class MulticastTransport : IDiscoveryTransport
{
    public static readonly IPAddress Group = IPAddress.Parse("239.255.47.70");

    private readonly int _port;
    private readonly bool _loopbackOnly;
    private readonly Socket _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<IPAddress> _joined = [];
    private DateTime _interfacesCheckedAt = DateTime.MinValue;

    /// <param name="port">The UDP port, the same on every PC.</param>
    /// <param name="loopbackOnly">This PC only (the tests: no firewall prompt).</param>
    public MulticastTransport(int port, bool loopbackOnly)
    {
        _port = port;
        _loopbackOnly = loopbackOnly;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); // other copies on this PC listen too
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
    }

    public event Action<byte[], IPAddress>? Received;

    public void Start()
    {
        _socket.Bind(new IPEndPoint(_loopbackOnly ? IPAddress.Loopback : IPAddress.Any, _port));
        JoinNetworks();
        _ = ReceiveLoopAsync();
    }

    /// <summary>The IPv4 addresses of this PC's networks that can carry multicast (rechecked as they come and go).</summary>
    private IReadOnlyList<IPAddress> Interfaces()
    {
        if (_loopbackOnly) return [IPAddress.Loopback];
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private void JoinNetworks()
    {
        _interfacesCheckedAt = DateTime.UtcNow;
        foreach (var address in Interfaces())
        {
            lock (_joined)
            {
                if (_joined.Contains(address)) continue;
                try
                {
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, address));
                    _joined.Add(address);
                }
                catch (SocketException)
                {
                    // Not ready yet: tried again later.
                }
            }
        }
    }

    public void Send(byte[] datagram)
    {
        // A network that came up since (e.g. Wi-Fi reconnected) joins the group too.
        if (DateTime.UtcNow - _interfacesCheckedAt > TimeSpan.FromSeconds(30)) JoinNetworks();

        var target = new IPEndPoint(Group, _port);
        foreach (var address in Interfaces())
        {
            try
            {
                lock (_socket)
                {
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    _socket.SendTo(datagram, target);
                }
            }
            catch (SocketException)
            {
                // That network is going away: the others still get it.
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _stop.Token);
                Received?.Invoke(buffer[..result.ReceivedBytes], ((IPEndPoint)result.RemoteEndPoint).Address);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                // e.g. a datagram too big, or a network that went away: carry on.
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _socket.Dispose();
    }
}
