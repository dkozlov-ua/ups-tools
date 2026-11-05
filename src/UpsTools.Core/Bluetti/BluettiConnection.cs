using System.Threading.Channels;

namespace UpsTools.Core.Bluetti;

/// <summary>
/// Persistent link to one Bluetti power station. Connects lazily, performs the encryption handshake
/// when the device starts one, and reconnects from scratch after any error or disconnect.
/// </summary>
/// <param name="address">For the log.</param>
/// <param name="connect">Opens the link (<see cref="GattLink"/> over Bluetooth LE).</param>
/// <param name="deviceSigningKey">The key the device signs its public key with; Bluetti's own unless a test says otherwise.</param>
internal sealed class BluettiConnection(
    BluettiModel model,
    string address,
    BluettiLinkFactory connect,
    Action<string>? log = null,
    BluettiTimings? timings = null,
    byte[]? deviceSigningKey = null) : IUpsConnection
{
    /// <summary>Verbose protocol tracing for diagnostics; enable with the environment variable UPSTOOLS_TRACE=1.</summary>
    internal static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("UPSTOOLS_TRACE") == "1";

    private readonly BluettiTimings _timings = timings ?? BluettiTimings.Default;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly BluettiCrypto _crypto = new(deviceSigningKey);
    private readonly List<byte> _encrypted = [];
    private readonly List<byte> _response = [];

    private IBluettiLink? _link;
    private Channel<byte[]>? _inbox;
    private Task? _inboxPump;
    private TaskCompletionSource? _challengeSeen;
    private TaskCompletionSource? _handshakeDone;
    private TaskCompletionSource<byte[]>? _pending;
    private bool _useEncryption;
    private bool _deviceUsesEncryption; // remembered across reconnects so we never silently fall back to plain text
    private volatile bool _connected;

    /// <summary>The session is encrypted (for the tests).</summary>
    internal bool Encrypted => _connected && _useEncryption;

    public async Task<UpsStatus> ReadAsync(CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken);
        try
        {
            if (!_connected) await ConnectWithRetryAsync(cancellationToken);

            var blocks = new Dictionary<RegisterBlock, byte[]>();
            foreach (var block in BluettiStatusParser.BlocksFor(model))
                blocks[block] = await ReadBlockAsync(block, cancellationToken);

            return BluettiStatusParser.Parse(model, blocks, DateTimeOffset.Now);
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

    /// <summary>
    /// A fresh session occasionally stalls mid-handshake: observed on an Elite 100 V2 when a session starts
    /// right after another one ended following a BLE scan (the device never acknowledges our public key).
    /// A second attempt a moment later succeeds, so retry once before reporting a failed poll.
    /// </summary>
    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        try
        {
            await ConnectAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
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
        _challengeSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handshakeDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Received += OnReceived;
        link.Disconnected += OnDisconnected;

        await link.SetNotificationsAsync(true, ct);
        var challenged = await WaitForChallengeAsync(ct);
        if (!challenged)
        {
            // Windows may still hold the previous physical link, so the device never saw a new connection.
            // Re-subscribing makes it start a fresh handshake.
            Trace("no challenge yet; re-subscribing");
            await link.SetNotificationsAsync(false, ct);
            await link.SetNotificationsAsync(true, ct);
            challenged = await WaitForChallengeAsync(ct);
        }

        if (challenged)
        {
            await _handshakeDone.Task.WaitAsync(_timings.Handshake, ct);
            _deviceUsesEncryption = true;
            log?.Invoke("Encrypted session established");
        }
        else if (_deviceUsesEncryption)
        {
            throw new IOException("Device did not start the encryption handshake");
        }
        else
        {
            log?.Invoke("Device did not start an encryption handshake; using plain Modbus");
        }

        _useEncryption = challenged;
        _connected = true;
    }

    private async Task<bool> WaitForChallengeAsync(CancellationToken ct)
    {
        var seen = _challengeSeen!.Task;
        var first = await Task.WhenAny(seen, Task.Delay(_timings.ChallengeWait, ct));
        ct.ThrowIfCancellationRequested();
        return first == seen;
    }

    private async Task<byte[]> ReadBlockAsync(RegisterBlock block, CancellationToken ct)
    {
        var pending = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_response)
        {
            _response.Clear();
            _pending = pending;
        }

        var request = Modbus.BuildReadRequest(block.Start, block.Count);
        if (_useEncryption) request = _crypto.Encrypt(request);
        await WriteAsync(request, ct);

        var frame = await pending.Task.WaitAsync(_timings.Response, ct);
        return Modbus.ParseReadResponse(frame, block.Count);
    }

    private Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        var link = _link ?? throw new IOException("Not connected");
        return link.WriteAsync(data, ct).WaitAsync(_timings.Write, ct);
    }

    private void OnReceived(byte[] data) => _inbox?.Writer.TryWrite(data);

    private void Trace(string message)
    {
        if (TraceEnabled) log?.Invoke($"[trace] {message}");
    }

    private void OnDisconnected()
    {
        if (!_connected) return;
        log?.Invoke("Bluetooth link dropped");
        _connected = false;
        _pending?.TrySetException(new IOException("Bluetooth link dropped"));
    }

    private async Task PumpInboxAsync(ChannelReader<byte[]> reader)
    {
        await foreach (var data in reader.ReadAllAsync())
        {
            try
            {
                await HandleNotificationAsync(data);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Protocol error: {ex.Message}");
                _handshakeDone?.TrySetException(ex);
                _pending?.TrySetException(ex);
                _encrypted.Clear();
            }
        }
    }

    private async Task HandleNotificationAsync(byte[] data)
    {
        if (BluettiCrypto.IsKexFrame(data))
        {
            switch (BluettiCrypto.GetKexType(data))
            {
                case BluettiCrypto.KexType.Challenge:
                    Trace("kex: challenge received");
                    _challengeSeen?.TrySetResult();
                    await WriteAsync(_crypto.HandleChallenge(data));
                    break;
                case BluettiCrypto.KexType.ChallengeAccepted:
                    Trace("kex: challenge accepted");
                    break;
                case var other:
                    Trace($"kex: unexpected plain frame type {other}");
                    break;
            }
            return;
        }

        if (!_crypto.HasTemporaryKey)
        {
            DeliverResponseBytes(data);
            return;
        }

        // Encrypted messages may arrive split across several notifications.
        _encrypted.AddRange(data);
        while (_crypto.ExpectedEncryptedLength(_encrypted.ToArray()) is { } expected && _encrypted.Count >= expected)
        {
            var message = _encrypted.GetRange(0, expected).ToArray();
            _encrypted.RemoveRange(0, expected);
            var plain = _crypto.Decrypt(message);

            if (!BluettiCrypto.IsKexFrame(plain))
            {
                DeliverResponseBytes(plain);
                continue;
            }

            switch (BluettiCrypto.GetKexType(plain))
            {
                case BluettiCrypto.KexType.PeerPublicKey:
                    Trace("kex: device public key received");
                    await WriteAsync(_crypto.HandlePeerPublicKey(plain));
                    break;
                case BluettiCrypto.KexType.PublicKeyAccepted:
                    Trace("kex: our public key accepted");
                    _crypto.HandlePublicKeyAccepted(plain);
                    _handshakeDone?.TrySetResult();
                    break;
                case var other:
                    Trace($"kex: unexpected encrypted frame type {other}");
                    break;
            }
        }
    }

    private void DeliverResponseBytes(byte[] data)
    {
        lock (_response)
        {
            _response.AddRange(data);
            var span = (ReadOnlySpan<byte>)_response.ToArray();
            if (Modbus.ExpectedResponseLength(span) is { } length && span.Length >= length)
            {
                _pending?.TrySetResult(span[..length].ToArray());
                _response.Clear();
            }
        }
    }

    private async Task DropAsync()
    {
        _connected = false;
        _pending?.TrySetCanceled();

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
        _encrypted.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _crypto.Dispose();
        _io.Dispose();
    }
}
