using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace UpsTools.Core.Ble;

/// <summary>The Bluetooth LE link to a device: its two data characteristics, through the WinRT GATT API.</summary>
internal sealed class GattLink : IBleLink
{
    private readonly BluetoothLEDevice _device;
    private readonly GattSession _session;
    private readonly List<GattDeviceService> _services;
    private readonly GattCharacteristic _notify;
    private readonly GattCharacteristic _write;
    private readonly GattWriteOption _writeOption;
    private readonly Action<string>? _trace;

    private GattLink(BluetoothLEDevice device, GattSession session, List<GattDeviceService> services,
        GattCharacteristic notify, GattCharacteristic write, Action<string>? trace)
    {
        _device = device;
        _session = session;
        _services = services;
        _notify = notify;
        _write = write;
        _trace = trace;
        _writeOption = write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse
            : GattWriteOption.WriteWithoutResponse;
        _notify.ValueChanged += OnValueChanged;
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public event Action<byte[]>? Received;
    public event Action? Disconnected;

    /// <param name="channels">The data channels the device may have; the first one it has is used.</param>
    /// <param name="brand">Names the device in errors, e.g. "Bluetti".</param>
    /// <param name="trace">Protocol tracing, or null when it is off.</param>
    public static async Task<IBleLink> ConnectAsync(ulong address, IReadOnlyList<GattChannel> channels, string brand,
        Action<string>? trace, CancellationToken ct)
    {
        BluetoothLEDevice? device = null;
        GattSession? session = null;
        List<GattDeviceService> services = [];
        try
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct)
                     ?? throw new IOException("Bluetooth device not found. Is it powered on and in range?");
            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(ct);
            session.MaintainConnection = true;

            var result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct);
            if (result.Status != GattCommunicationStatus.Success)
                throw new IOException($"Could not list GATT services ({result.Status})");
            services = [.. result.Services];

            var found = new Dictionary<Guid, GattCharacteristic>();
            var problems = new List<string>();
            foreach (var service in services)
            {
                var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(ct);
                if (characteristics.Status != GattCommunicationStatus.Success)
                {
                    problems.Add($"{service.Uuid.ToString()[..8]}: {characteristics.Status}");
                    continue;
                }
                foreach (var c in characteristics.Characteristics) found.TryAdd(c.Uuid, c);
            }
            foreach (var channel in channels)
            {
                if (found.TryGetValue(channel.Notify, out var notify) && found.TryGetValue(channel.Write, out var write))
                    return new GattLink(device, session, services, notify, write, trace);
            }

            var detail = problems.Count > 0 ? $"; {string.Join(", ", problems)}" : "";
            throw new IOException($"{brand} data characteristics not available ({services.Count} services{detail})");
        }
        catch
        {
            foreach (var service in services) service.Dispose();
            session?.Dispose();
            device?.Dispose();
            throw;
        }
    }

    public async Task SetNotificationsAsync(bool on, CancellationToken ct)
    {
        var value = on
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.None;
        var result = await _notify.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(value).AsTask(ct);
        if (result.Status != GattCommunicationStatus.Success)
            throw new IOException($"Could not change notification subscription ({result.Status})");
    }

    public async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        _trace?.Invoke($"tx {data.Length} B ({_writeOption})");
        var result = await _write.WriteValueWithResultAsync(data.AsBuffer(), _writeOption).AsTask(ct);
        _trace?.Invoke($"tx done: {result.Status}{(result.ProtocolError is { } e ? $", protocol error {e}" : "")}");
        if (result.Status != GattCommunicationStatus.Success)
            throw new IOException($"Bluetooth write failed ({result.Status})");
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = args.CharacteristicValue.ToArray();
        _trace?.Invoke($"rx {data.Length} B: {Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 24)))}");
        Received?.Invoke(data);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected) Disconnected?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        _notify.ValueChanged -= OnValueChanged;
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        foreach (var service in _services) service.Dispose();
        _session.Dispose();
        _device.Dispose();
        return ValueTask.CompletedTask;
    }
}
