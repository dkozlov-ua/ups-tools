using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using UpsTools.Core.Ble;

namespace UpsTools.Core.EcoFlow;

/// <summary>
/// Persistent link to one EcoFlow power station. Connects lazily: key exchange, session key, then authentication with
/// the EcoFlow user ID the device is bound to. The device then pushes its state on its own; a read returns the latest.
/// Reconnects from scratch after any error, disconnect, or silence.
/// </summary>
/// <param name="serial">The device's serial number: part of the authentication.</param>
/// <param name="userId">The EcoFlow account the device is bound to (<see cref="EcoFlowAccount"/>).</param>
/// <param name="address">For the log.</param>
/// <param name="connect">Opens the link (<see cref="GattLink"/> over Bluetooth LE).</param>
internal sealed class EcoFlowConnection(
    EcoFlowModel model,
    string serial,
    string userId,
    string address,
    BleLinkFactory connect,
    Action<string>? log = null,
    EcoFlowTimings? timings = null,
    Func<DateTimeOffset>? clock = null) : IUpsConnection
{
    // Addresses and commands
    private const byte App = 0x21;
    private const byte Auth = 0x35;      // the device's Bluetooth module; also the authentication command set
    private const byte AuthStatus = 0x89;
    private const byte Authenticate = 0x86;
    private const byte Pd = 0x02;        // the device's main board
    private const byte DisplayCmdSet = 0xFE;
    private const byte DisplayUpload = 0x15;
    private const byte TimeCmdSet = 0x01;
    private const byte SetRtcTime = 0x52;
    private const byte CheckRtcTime = 0x53;

    private readonly EcoFlowTimings _timings = timings ?? EcoFlowTimings.Default;
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly Lock _gate = new();
    private readonly EcoFlowCrypto _crypto = new();
    private readonly EcoFlowFrameReader _frames = new();
    private readonly ProtoFields _fields = new();

    private IBleLink? _link;
    private Channel<byte[]>? _inbox;
    private Task? _inboxPump;
    private Waiter<byte[]>? _handshakeReply;
    private Waiter<EcoFlowPacket>? _packetReply;
    private TaskCompletionSource? _firstData;
    private DateTimeOffset _lastData;
    private DateTimeOffset? _lastTimeReply;
    private volatile bool _connected;

    private sealed record Waiter<T>(Func<T, bool> Matches, TaskCompletionSource<T> Reply);

    public async Task<UpsStatus> ReadAsync(CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken);
        try
        {
            if (!_connected) await ConnectWithRetryAsync(cancellationToken);

            try
            {
                await _firstData!.Task.WaitAsync(_timings.FirstData, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new IOException("The UPS sent no data");
            }

            lock (_gate)
            {
                var now = _clock();
                if (now - _lastData > _timings.Silence)
                    throw new IOException($"No data from the UPS for {(now - _lastData).TotalSeconds:0} s");
                return EcoFlowStatusParser.Parse(model, serial, _fields, now);
            }
        }
        catch
        {
            await DropAsync();
            throw;
        }
        finally
        {
            _io.Release();
        }
    }

    public async Task ResetAsync()
    {
        await _io.WaitAsync();
        try { await DropAsync(); }
        finally { _io.Release(); }
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        try
        {
            await ConnectAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not EcoFlowAuthException)
        {
            log?.Invoke($"Connection attempt failed ({ex.Message}); retrying");
            await DropAsync();
            await Task.Delay(_timings.Retry, ct);
            await ConnectAsync(ct);
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        await DropAsync();
        log?.Invoke($"Connecting to {model.Name} ({address})");

        var link = await connect(ct);
        _link = link;
        _inbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        _inboxPump = Task.Run(() => PumpInboxAsync(_inbox.Reader));
        _firstData = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Received += OnReceived;
        link.Disconnected += OnDisconnected;
        await link.SetNotificationsAsync(true, ct);

        var publicKey = await RequestAsync(_crypto.StartKeyExchange(), 0x01, 3 + 40, "key exchange", ct);
        _crypto.HandlePublicKey(publicKey);
        var keyInfo = await RequestAsync([0x02], 0x02, 33, "session key", ct);
        _crypto.HandleKeyInfo(keyInfo);
        Trace("session key in place");

        await SendAsync(new EcoFlowPacket(App, Auth, Auth, AuthStatus, []), p => p.Src == Auth && p.CmdSet == Auth,
            "authentication status", ct);

        var token = Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(userId + serial)));
        var result = await SendAsync(new EcoFlowPacket(App, Auth, Auth, Authenticate, Encoding.ASCII.GetBytes(token)),
            p => p is { Src: Auth, CmdSet: Auth, CmdId: Authenticate } || IsDisplayUpload(p), "authentication", ct);
        if (!IsDisplayUpload(result)) CheckAuthResult(result.Payload);

        log?.Invoke("Authenticated");
        _connected = true;
    }

    /// <summary>What the device answers to our user ID: 0 for accepted (from ha-ef-ble's AuthErrors).</summary>
    private static void CheckAuthResult(byte[] payload)
    {
        if (payload is [0]) return;
        throw new EcoFlowAuthException(payload switch
        {
            [0x01] => "The UPS wants its pairing refreshed: open it once in the EcoFlow app",
            [0x03] => "The UPS is bound to another EcoFlow account",
            [0x04] => "The UPS isn't bound to an EcoFlow account yet: add it in the EcoFlow app first",
            [0x06] => "The UPS rejected the EcoFlow user ID: check it, and that the UPS is bound to that account",
            [0x07] => "The UPS has as many connections as it allows",
            _ => $"The UPS rejected the authentication ({Convert.ToHexString(payload)})",
        });
    }

    /// <summary>Sends a plain handshake frame and waits for the reply of the given type.</summary>
    private async Task<byte[]> RequestAsync(byte[] payload, byte replyType, int minLength, string step, CancellationToken ct)
    {
        var reply = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _handshakeReply = new(r => r.Length >= minLength && r[0] == replyType, reply);
        await WriteAsync(new EcoFlowFrame(EcoFlowFrame.Command, payload).ToBytes(), ct);
        return await WaitAsync(reply.Task, step, ct);
    }

    /// <summary>Sends a packet and waits for the first one back that matches.</summary>
    private async Task<EcoFlowPacket> SendAsync(EcoFlowPacket packet, Func<EcoFlowPacket, bool> reply, string step, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<EcoFlowPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _packetReply = new(reply, answer);
        await SendAsync(packet, ct);
        return await WaitAsync(answer.Task, step, ct);
    }

    private Task SendAsync(EcoFlowPacket packet, CancellationToken ct)
    {
        Trace($"tx {packet}");
        return WriteAsync(new EcoFlowFrame(EcoFlowFrame.Protocol, _crypto.Encrypt(packet.ToBytes())).ToBytes(), ct);
    }

    private async Task<T> WaitAsync<T>(Task<T> reply, string step, CancellationToken ct)
    {
        try
        {
            return await reply.WaitAsync(_timings.Reply, ct);
        }
        catch (TimeoutException)
        {
            throw new IOException($"The UPS did not answer ({step})");
        }
    }

    private Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        var link = _link ?? throw new IOException("Not connected");
        return link.WriteAsync(data, ct).WaitAsync(_timings.Write, ct);
    }

    private void OnReceived(byte[] data) => _inbox?.Writer.TryWrite(data);

    private void OnDisconnected()
    {
        log?.Invoke("Bluetooth link dropped");
        _connected = false;
        var error = new IOException("Bluetooth link dropped");
        lock (_gate)
        {
            _handshakeReply?.Reply.TrySetException(error);
            _packetReply?.Reply.TrySetException(error);
        }
    }

    private async Task PumpInboxAsync(ChannelReader<byte[]> reader)
    {
        await foreach (var data in reader.ReadAllAsync())
        {
            foreach (var frame in _frames.Add(data))
            {
                try
                {
                    await HandleFrameAsync(frame);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Protocol error: {ex.Message}");
                }
            }
        }
    }

    private async Task HandleFrameAsync(EcoFlowFrame frame)
    {
        if (frame.Type == EcoFlowFrame.Command)
        {
            // A handshake reply; devices may repeat them, so ones nobody waits for are dropped.
            Trace($"rx handshake frame {Convert.ToHexString(frame.Payload.AsSpan(0, Math.Min(frame.Payload.Length, 4)))}…");
            lock (_gate)
            {
                if (_handshakeReply is { } waiter && waiter.Matches(frame.Payload))
                {
                    _handshakeReply = null;
                    waiter.Reply.TrySetResult(frame.Payload);
                }
            }
            return;
        }

        if (!_crypto.HasSessionKey)
        {
            Trace("rx packet before the session key; dropped");
            return;
        }

        var packet = EcoFlowPacket.Parse(_crypto.Decrypt(frame.Payload));
        Trace($"rx {packet}");

        if (IsDisplayUpload(packet))
        {
            lock (_gate)
            {
                _fields.Merge(packet.Payload);
                _lastData = _clock();
                if (EcoFlowStatusParser.IsComplete(_fields)) _firstData?.TrySetResult();
            }
        }
        else if (packet is { Src: Auth, CmdSet: TimeCmdSet, CmdId: SetRtcTime, Payload.Length: 0 })
        {
            await ReplyWithTimeAsync();
        }

        lock (_gate)
        {
            if (_packetReply is { } waiter && waiter.Matches(packet))
            {
                _packetReply = null;
                waiter.Reply.TrySetResult(packet);
            }
        }
    }

    private static bool IsDisplayUpload(EcoFlowPacket p) => p is { Src: Pd, CmdSet: DisplayCmdSet, CmdId: DisplayUpload };

    /// <summary>
    /// The device asks for the time after connecting and keeps asking until it gets it; answered as the EcoFlow app
    /// does (per ha-ef-ble), at most every 30 seconds.
    /// </summary>
    private async Task ReplyWithTimeAsync()
    {
        var now = _clock();
        if (_lastTimeReply is { } last && now - last < TimeSpan.FromSeconds(30)) return;
        _lastTimeReply = now;

        var utc = (uint)now.ToUnixTimeSeconds();
        var offset = now.Offset.TotalHours;
        var hours = (sbyte)offset;
        var fraction = (sbyte)((offset - hours) * 100); // as the app sends it: 5.5 h is 5 and 50
        byte[] rtc = [(byte)utc, (byte)(utc >> 8), (byte)(utc >> 16), (byte)(utc >> 24), (byte)hours, (byte)fraction];

        Trace("the UPS asked for the time");
        await SendAsync(new EcoFlowPacket(App, 0x0B, TimeCmdSet, 0x55, ProtoFields.EncodeVarint(1, utc), 0x13), CancellationToken.None);
        await SendAsync(new EcoFlowPacket(App, Auth, TimeCmdSet, SetRtcTime, rtc), CancellationToken.None);
        await SendAsync(new EcoFlowPacket(App, Auth, TimeCmdSet, CheckRtcTime, rtc), CancellationToken.None);
    }

    private void Trace(string message)
    {
        if (BleTrace.Enabled) log?.Invoke($"[trace] {message}");
    }

    private async Task DropAsync()
    {
        _connected = false;
        lock (_gate)
        {
            _handshakeReply?.Reply.TrySetCanceled();
            _packetReply?.Reply.TrySetCanceled();
            _handshakeReply = null;
            _packetReply = null;
        }

        var link = _link;
        _link = null;
        if (link is not null)
        {
            link.Received -= OnReceived;
            link.Disconnected -= OnDisconnected;
            try
            {
                // Unsubscribe so the device ends its session even if Windows keeps the physical link a while.
                using var timeout = new CancellationTokenSource(_timings.Unsubscribe);
                await link.SetNotificationsAsync(false, timeout.Token).WaitAsync(timeout.Token);
            }
            catch
            {
                // Link already gone.
            }
        }

        _inbox?.Writer.TryComplete();
        if (_inboxPump is not null)
        {
            try { await _inboxPump.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        }

        if (link is not null)
        {
            try { await link.DisposeAsync(); } catch { /* ignore */ }
        }

        _inbox = null;
        _inboxPump = null;
        _crypto.Reset();
        _frames.Reset();
        lock (_gate) _fields.Clear();
        _lastTimeReply = null;
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _crypto.Dispose();
        _io.Dispose();
    }
}

/// <summary>The UPS refused our EcoFlow user ID; trying again won't help.</summary>
public sealed class EcoFlowAuthException(string message) : IOException(message);

/// <summary>How long <see cref="EcoFlowConnection"/> waits for each step.</summary>
/// <param name="Silence">With no data for this long the link counts as lost.</param>
internal sealed record EcoFlowTimings(
    TimeSpan Reply,
    TimeSpan FirstData,
    TimeSpan Silence,
    TimeSpan Write,
    TimeSpan Retry,
    TimeSpan Unsubscribe)
{
    public static EcoFlowTimings Default { get; } = new(
        Reply: TimeSpan.FromSeconds(5),
        FirstData: TimeSpan.FromSeconds(15),
        Silence: TimeSpan.FromSeconds(60),
        Write: TimeSpan.FromSeconds(5),
        Retry: TimeSpan.FromSeconds(1),
        Unsubscribe: TimeSpan.FromSeconds(2));
}
