using Android.Bluetooth;
using Android.Content;
using UpsTools.Core.Ble;
using Context = Android.Content.Context;

namespace UpsTools.Droid.Platform;

/// <summary>
/// The Bluetooth LE link to a device through Android's GATT API: its two data characteristics. Android runs one GATT
/// operation at a time per link and reports each in a callback, so they are queued here and awaited one by one.
/// </summary>
internal sealed class AndroidGattLink : IBleLink
{
    /// <summary>The largest ATT MTU there is; the device answers with what it can do. Android starts at 23 bytes.</summary>
    private const int WantedMtu = 517;

    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private static readonly Java.Util.UUID ClientConfiguration = Java.Util.UUID.FromString("00002902-0000-1000-8000-00805f9b34fb")!;

    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly Action<string>? _trace;
    private readonly TaskCompletionSource<bool> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>The operation waiting for its callback, and its kind: a late callback of an earlier one doesn't complete it.</summary>
    private (Op Kind, TaskCompletionSource<GattStatus> Done)? _pending;

    private enum Op
    {
        Mtu,
        Services,
        Descriptor,
        Write,
    }
    private BluetoothGatt? _gatt;
    private BluetoothGattCharacteristic? _notify;
    private BluetoothGattCharacteristic? _write;
    private volatile bool _open;
    private volatile bool _closed;

    private AndroidGattLink(Action<string>? trace) => _trace = trace;

    public event Action<byte[]>? Received;
    public event Action? Disconnected;

    /// <param name="channels">The data channels the device may have; the first one it has is used.</param>
    /// <param name="brand">Names the device in errors, e.g. "Bluetti".</param>
    public static async Task<IBleLink> ConnectAsync(Context context, BluetoothDevice device, IReadOnlyList<GattChannel> channels,
        string brand, Action<string>? trace, CancellationToken ct)
    {
        var link = new AndroidGattLink(trace);
        try
        {
            link._gatt = device.ConnectGatt(context, false, new Callback(link), BluetoothTransports.Le)
                         ?? throw new IOException("Could not start the Bluetooth connection");
            using (ct.Register(() => link._connected.TrySetCanceled(ct)))
            {
                await link._connected.Task.WaitAsync(TimeSpan.FromSeconds(30), ct)
                    .ConfigureAwait(false);
            }

            // The default MTU (23) cuts writes to 20 bytes; the devices send and expect more.
            try
            {
                await link.RunAsync(Op.Mtu, "ask for a larger MTU", g => g.RequestMtu(WantedMtu), ct, TimeSpan.FromSeconds(5));
            }
            catch (IOException ex)
            {
                trace?.Invoke($"MTU not changed: {ex.Message}");
            }

            await link.RunAsync(Op.Services, "list the GATT services", g => g.DiscoverServices(), ct, TimeSpan.FromSeconds(20));

            var found = new Dictionary<Guid, BluetoothGattCharacteristic>();
            var services = link._gatt.Services ?? [];
            foreach (var service in services)
                foreach (var c in service.Characteristics ?? [])
                    if (c.Uuid is { } uuid) found.TryAdd(Guid.Parse(uuid.ToString()), c);
            foreach (var channel in channels)
            {
                if (found.TryGetValue(channel.Notify, out var notify) && found.TryGetValue(channel.Write, out var write))
                {
                    (link._notify, link._write) = (notify, write);
                    link._open = true;
                    return link;
                }
            }
            throw new IOException($"{brand} data characteristics not available ({services.Count} services)");
        }
        catch (TimeoutException)
        {
            await link.DisposeAsync();
            throw new IOException("Bluetooth device not found. Is it powered on, in range, and not connected to another device?");
        }
        catch
        {
            await link.DisposeAsync();
            throw;
        }
    }

    /// <summary>Starts a GATT operation and waits for its callback; one at a time.</summary>
    /// <param name="start">Starts it; false when Android refused to.</param>
    private async Task RunAsync(Op kind, string what, Func<BluetoothGatt, bool> start, CancellationToken ct, TimeSpan? timeout = null)
    {
        await _operations.WaitAsync(ct);
        try
        {
            if (_closed || _gatt is not { } gatt) throw new IOException("The Bluetooth link dropped");
            var pending = new TaskCompletionSource<GattStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = (kind, pending);
            if (!start(gatt)) throw new IOException($"Could not {what}: Android refused");
            GattStatus status;
            try
            {
                status = await pending.Task.WaitAsync(timeout ?? OperationTimeout, ct);
            }
            catch (TimeoutException)
            {
                throw new IOException($"Could not {what}: no answer");
            }
            if (status != GattStatus.Success) throw new IOException($"Could not {what} ({Describe(status)})");
        }
        finally
        {
            _pending = null;
            _operations.Release();
        }
    }

    public Task SetNotificationsAsync(bool on, CancellationToken ct)
    {
        var notify = _notify ?? throw new InvalidOperationException("Not connected");
        var descriptor = notify.GetDescriptor(ClientConfiguration)
                         ?? throw new IOException("The device's data characteristic can't notify");
        var indicate = !notify.Properties.HasFlag(GattProperty.Notify) && notify.Properties.HasFlag(GattProperty.Indicate);
        byte[] value = !on ? [0x00, 0x00] : indicate ? [0x02, 0x00] : [0x01, 0x00];
        return RunAsync(Op.Descriptor, "change the notification subscription", gatt =>
        {
            if (!gatt.SetCharacteristicNotification(notify, on)) return false;
            if (OperatingSystem.IsAndroidVersionAtLeast(33)) return gatt.WriteDescriptor(descriptor, value) == 0; // BluetoothStatusCodes.SUCCESS
#pragma warning disable CA1422, CS0618 // the API before Android 13
            descriptor.SetValue(value);
            return gatt.WriteDescriptor(descriptor);
#pragma warning restore CA1422, CS0618
        }, ct);
    }

    public Task WriteAsync(byte[] data, CancellationToken ct)
    {
        var write = _write ?? throw new InvalidOperationException("Not connected");
        var type = write.Properties.HasFlag(GattProperty.Write) ? GattWriteType.Default : GattWriteType.NoResponse;
        _trace?.Invoke($"tx {data.Length} B ({type})");
        return RunAsync(Op.Write, "write to the device", gatt =>
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(33)) return gatt.WriteCharacteristic(write, data, (int)type) == 0; // BluetoothStatusCodes.SUCCESS
#pragma warning disable CA1422, CS0618 // the API before Android 13
            write.WriteType = type;
            write.SetValue(data);
            return gatt.WriteCharacteristic(write);
#pragma warning restore CA1422, CS0618
        }, ct);
    }

    private static string Describe(GattStatus status) => (int)status switch
    {
        8 => "connection timed out",
        19 => "the device closed the connection",
        22 => "the phone closed the connection",
        133 => "error 133",
        _ => status.ToString(),
    };

    private void OnConnectionState(GattStatus status, ProfileState state)
    {
        _trace?.Invoke($"connection state {state} ({Describe(status)})");
        if (state == ProfileState.Connected && status == GattStatus.Success)
        {
            _connected.TrySetResult(true);
            return;
        }
        if (state != ProfileState.Disconnected && status == GattStatus.Success) return;

        var wasOpen = _open;
        _closed = true;
        _open = false;
        var error = new IOException(status == GattStatus.Success
            ? "The Bluetooth link dropped"
            : $"Could not connect over Bluetooth ({Describe(status)})");
        _connected.TrySetException(error);
        _pending?.Done.TrySetException(error);
        if (wasOpen) Disconnected?.Invoke();
    }

    private void OnChanged(BluetoothGattCharacteristic? characteristic, byte[]? value)
    {
        if (characteristic is null || value is null || _notify is not { } notify) return;
        if (characteristic.Uuid?.ToString() != notify.Uuid?.ToString()) return;
        _trace?.Invoke($"rx {value.Length} B: {Convert.ToHexString(value.AsSpan(0, Math.Min(value.Length, 24)))}");
        Received?.Invoke(value);
    }

    private void Completed(Op kind, GattStatus status)
    {
        if (_pending is { } pending && pending.Kind == kind) pending.Done.TrySetResult(status);
    }

    public ValueTask DisposeAsync()
    {
        _closed = true;
        _open = false;
        var gatt = _gatt;
        _gatt = null;
        if (gatt is not null)
        {
            try
            {
                gatt.Disconnect();
                gatt.Close();
            }
            catch (Exception)
            {
                // Bluetooth went off meanwhile: nothing left to close.
            }
            gatt.Dispose();
        }
        _pending?.Done.TrySetException(new IOException("The Bluetooth link was closed"));
        return ValueTask.CompletedTask;
    }

    /// <summary>Android's callbacks, on its Binder threads.</summary>
    private sealed class Callback(AndroidGattLink link) : BluetoothGattCallback
    {
        public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState) =>
            link.OnConnectionState(status, newState);

        public override void OnMtuChanged(BluetoothGatt? gatt, int mtu, GattStatus status)
        {
            link._trace?.Invoke($"MTU {mtu} ({Describe(status)})");
            link.Completed(Op.Mtu, status);
        }

        public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status) => link.Completed(Op.Services, status);

        public override void OnDescriptorWrite(BluetoothGatt? gatt, BluetoothGattDescriptor? descriptor, GattStatus status) =>
            link.Completed(Op.Descriptor, status);

        public override void OnCharacteristicWrite(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status) =>
            link.Completed(Op.Write, status);

        // Android 13 and later: the value comes with the call (the characteristic's own may already be the next one's).
        public override void OnCharacteristicChanged(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] value) =>
            link.OnChanged(characteristic, value);

#pragma warning disable CA1422, CS0672, CS0618 // before Android 13
        public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic) =>
            link.OnChanged(characteristic, characteristic?.GetValue());
#pragma warning restore CA1422, CS0672, CS0618
    }
}
