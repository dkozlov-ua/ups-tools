using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace UpsTools.Core.Bluetti;

/// <summary>
/// Persistent BLE link to one Bluetti power station. Connects lazily, performs the encryption handshake
/// when the device starts one, and reconnects from scratch after any error or disconnect.
/// </summary>
internal sealed class BluettiBleConnection(ulong address, BluettiModel model, Action<string>? log = null) : IUpsConnection
{
    private static readonly Guid NotifyUuid = Guid.Parse("0000ff01-0000-1000-8000-00805f9b34fb");
    private static readonly Guid WriteUuid = Guid.Parse("0000ff02-0000-1000-8000-00805f9b34fb");

    /// <summary>Encrypted devices send their challenge right after notifications are enabled.</summary>
    private static readonly TimeSpan ChallengeWait = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5); // normally ~1.3 s
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly BluettiCrypto _crypto = new();
    private readonly List<byte> _encrypted = [];
    private readonly List<byte> _response = [];

    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private List<GattDeviceService> _services = [];
    private GattCharacteristic? _notify;
    private GattCharacteristic? _write;
    private GattWriteOption _writeOption;
    private Channel<byte[]>? _inbox;
    private Task? _inboxPump;
    private TaskCompletionSource? _challengeSeen;
    private TaskCompletionSource? _handshakeDone;
    private TaskCompletionSource<byte[]>? _pending;
    private bool _useEncryption;
    private bool _deviceUsesEncryption; // remembered across reconnects so we never silently fall back to plain text
    private volatile bool _connected;

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
            await Task.Delay(RetryDelay, ct);
            await ConnectAsync(ct);
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        await DropAsync();
        log?.Invoke($"Connecting to {model.Name} ({BluetoothAddressFormat.Format(address)})");

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct)
                  ?? throw new IOException("Bluetooth device not found. Is it powered on and in range?");
        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask(ct);
        _session.MaintainConnection = true;

        var services = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct);
        if (services.Status != GattCommunicationStatus.Success)
            throw new IOException($"Could not list GATT services ({services.Status})");
        _services = [.. services.Services];

        var problems = new List<string>();
        foreach (var service in _services)
        {
            var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(ct);
            if (characteristics.Status != GattCommunicationStatus.Success)
            {
                problems.Add($"{service.Uuid.ToString()[..8]}: {characteristics.Status}");
                continue;
            }
            foreach (var c in characteristics.Characteristics)
            {
                if (c.Uuid == NotifyUuid) _notify = c;
                else if (c.Uuid == WriteUuid) _write = c;
            }
        }
        if (_notify is null || _write is null)
        {
            var detail = problems.Count > 0 ? $"; {string.Join(", ", problems)}" : "";
            throw new IOException($"Bluetti data characteristics not available ({_services.Count} services{detail})");
        }

        _writeOption = _write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse
            : GattWriteOption.WriteWithoutResponse;

        _inbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        _inboxPump = Task.Run(() => PumpInboxAsync(_inbox.Reader));
        _challengeSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handshakeDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _notify.ValueChanged += OnValueChanged;
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        await SetNotificationsAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify, ct);
        var challenged = await WaitForChallengeAsync(ct);
        if (!challenged)
        {
            // Windows may still hold the previous physical link, so the device never saw a new connection.
            // Re-subscribing makes it start a fresh handshake.
            Trace("no challenge yet; re-subscribing");
            await SetNotificationsAsync(GattClientCharacteristicConfigurationDescriptorValue.None, ct);
            await SetNotificationsAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify, ct);
            challenged = await WaitForChallengeAsync(ct);
        }

        if (challenged)
        {
            await _handshakeDone.Task.WaitAsync(HandshakeTimeout, ct);
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

    private async Task SetNotificationsAsync(GattClientCharacteristicConfigurationDescriptorValue value, CancellationToken ct)
    {
        var characteristic = _notify ?? throw new IOException("Not connected");
        var result = await characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(value).AsTask(ct);
        if (result.Status != GattCommunicationStatus.Success)
            throw new IOException($"Could not change notification subscription ({result.Status})");
    }

    private async Task<bool> WaitForChallengeAsync(CancellationToken ct)
    {
        var seen = _challengeSeen!.Task;
        var first = await Task.WhenAny(seen, Task.Delay(ChallengeWait, ct));
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

        var frame = await pending.Task.WaitAsync(ResponseTimeout, ct);
        return Modbus.ParseReadResponse(frame, block.Count);
    }

    private async Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        var characteristic = _write ?? throw new IOException("Not connected");
        Trace($"tx {data.Length} B ({_writeOption})");
        var result = await characteristic.WriteValueWithResultAsync(data.AsBuffer(), _writeOption).AsTask(ct)
            .WaitAsync(WriteTimeout, ct);
        Trace($"tx done: {result.Status}{(result.ProtocolError is { } e ? $", protocol error {e}" : "")}");
        if (result.Status != GattCommunicationStatus.Success)
            throw new IOException($"Bluetooth write failed ({result.Status})");
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = args.CharacteristicValue.ToArray();
        Trace($"rx {data.Length} B: {Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 24)))}");
        _inbox?.Writer.TryWrite(data);
    }

    /// <summary>Verbose protocol tracing for diagnostics; enable with the environment variable UPSTOOLS_TRACE=1.</summary>
    private void Trace(string message)
    {
        if (TraceEnabled) log?.Invoke($"[trace] {message}");
    }

    private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("UPSTOOLS_TRACE") == "1";

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected || !_connected) return;
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

        if (_notify is not null)
        {
            _notify.ValueChanged -= OnValueChanged;
            try
            {
                // Unsubscribe so the device ends its session even if Windows keeps the physical link a while.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SetNotificationsAsync(GattClientCharacteristicConfigurationDescriptorValue.None, timeout.Token);
            }
            catch
            {
                // Link already gone.
            }
        }
        if (_device is not null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged;

        _inbox?.Writer.TryComplete();
        if (_inboxPump is not null)
        {
            try { await _inboxPump.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        }

        foreach (var service in _services) service.Dispose();
        _services = [];
        _session?.Dispose();
        _device?.Dispose();

        _notify = _write = null;
        _session = null;
        _device = null;
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
