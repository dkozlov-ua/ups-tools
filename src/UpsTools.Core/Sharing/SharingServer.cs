using System.Net;
using System.Net.Sockets;

namespace UpsTools.Core.Sharing;

/// <summary>
/// Answers other PCs' requests for the UPSes in a <see cref="SharingHub"/>, over TCP, to those that have the sharing
/// code (<see cref="SecureChannel"/>). A PC that watches a UPS keeps asking for its next reading; the answer comes as
/// soon as there is one, so its events follow this PC's readings within a second.
/// </summary>
public sealed class SharingServer : IDisposable
{
    /// <summary>The TCP port, and the UDP port of the announcements (<see cref="MulticastTransport"/>), unless set otherwise.</summary>
    public const int DefaultPort = 47870;

    /// <summary>Set to 1 to listen on this PC only (the tests: no firewall prompt).</summary>
    public const string LoopbackEnvironmentVariable = "UPSTOOLS_SHARING_LOOPBACK";

    private const int MaxClients = 32;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefusalLogInterval = TimeSpan.FromMinutes(1);

    private readonly SharingHub _hub;
    private readonly byte[] _key;
    private readonly Action<string>? _log;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<TcpClient> _clients = [];
    private DateTime _lastRefusalLogged = DateTime.MinValue;

    /// <param name="port">The TCP port; 0 picks a free one (<see cref="Port"/>).</param>
    /// <param name="loopbackOnly">Listen on this PC only; otherwise on every network, which Windows Firewall may ask about.</param>
    public SharingServer(SharingHub hub, string code, int port, bool loopbackOnly = false, Action<string>? log = null)
    {
        _hub = hub;
        _key = SharingCode.Key(code);
        _log = log;
        if (loopbackOnly)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
        }
        else
        {
            _listener = new TcpListener(IPAddress.IPv6Any, port);
            _listener.Server.DualMode = true; // IPv4 too
        }
    }

    /// <summary>Whether <see cref="LoopbackEnvironmentVariable"/> asks to listen on this PC only.</summary>
    public static bool LoopbackFromEnvironment => Environment.GetEnvironmentVariable(LoopbackEnvironmentVariable) == "1";

    /// <summary>The port it listens on, once started.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Starts listening.</summary>
    /// <exception cref="SocketException">The port is in use, or not allowed.</exception>
    public void Start()
    {
        _listener.Server.ExclusiveAddressUse = true;
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // that one connection failed
            }

            lock (_clients)
            {
                if (_clients.Count >= MaxClients || _stop.IsCancellationRequested)
                {
                    client.Dispose();
                    continue;
                }
                _clients.Add(client);
            }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "another PC";
        if (remote.StartsWith("::ffff:", StringComparison.Ordinal)) remote = remote[7..];
        try
        {
            client.NoDelay = true;
            SecureChannel channel;
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                handshake.CancelAfter(HandshakeTimeout);
                channel = await SecureChannel.AcceptAsync(client.GetStream(), _key, handshake.Token);
            }

            using (channel)
            {
                var first = true;
                while (!_stop.IsCancellationRequested)
                {
                    byte[] message;
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                    {
                        idle.CancelAfter(IdleTimeout);
                        try
                        {
                            message = await channel.ReceiveAsync(idle.Token);
                        }
                        catch (WrongSharingCodeException) when (first)
                        {
                            LogRefusal(remote);
                            await channel.RefuseAsync(_stop.Token);
                            return;
                        }
                    }

                    var request = SharingJson.Read<SharingRequest>(message);
                    if (first) _log?.Invoke($"{request.From ?? "A PC"} ({remote}) connected to watch the shared UPSes");
                    first = false;
                    var response = await AnswerAsync(request);
                    await channel.SendAsync(SharingJson.Write(response), _stop.Token);
                }
            }
        }
        catch (Exception)
        {
            // It went away, timed out or sent something else: drop it.
        }
        finally
        {
            lock (_clients) _clients.Remove(client);
            client.Dispose();
        }
    }

    private void LogRefusal(string remote)
    {
        // Someone trying codes mustn't fill the log.
        if (DateTime.UtcNow - _lastRefusalLogged < RefusalLogInterval) return;
        _lastRefusalLogged = DateTime.UtcNow;
        _log?.Invoke($"Refused {remote}: wrong sharing code");
    }

    private async Task<SharingResponse> AnswerAsync(SharingRequest request)
    {
        switch (request.Op)
        {
            case SharingRequest.List:
                return new SharingResponse { Pc = new SharedPc(_hub.Pc, _hub.Instance, _hub.List()) };

            case SharingRequest.Next:
                var feed = _hub.Find(request.Ups);
                if (feed is null) return new SharingResponse { Missing = true };

                var wait = TimeSpan.FromSeconds(Math.Clamp(request.WaitSeconds, 0, (int)MaxWait.TotalSeconds));
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    timeout.CancelAfter(wait);
                    while (true)
                    {
                        var (seq, status, error, changed) = feed.Latest;
                        if (feed.Closed) return new SharingResponse { Missing = true };

                        // Only the latest: a PC that missed readings (e.g. while its link was down) catches up at once.
                        if (seq > 0 && (request.Run != feed.Run || seq > request.Seq))
                            return new SharingResponse
                            {
                                Run = feed.Run, Seq = seq, Status = status, Error = error,
                                PeriodSeconds = (int)feed.Period.TotalSeconds,
                            };

                        try
                        {
                            await changed.WaitAsync(timeout.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return new SharingResponse { Run = feed.Run, PeriodSeconds = (int)feed.Period.TotalSeconds };
                        }
                    }
                }

            default:
                throw new SharingException($"Unknown request '{request.Op}'");
        }
    }

    /// <summary>Stops listening and drops every link.</summary>
    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _listener.Stop();
        lock (_clients)
        {
            foreach (var client in _clients) client.Dispose();
            _clients.Clear();
        }
    }
}
