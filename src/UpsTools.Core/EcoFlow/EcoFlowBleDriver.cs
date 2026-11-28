using System.Text;
using UpsTools.Core.Ble;

namespace UpsTools.Core.EcoFlow;

/// <summary>
/// EcoFlow power stations over Bluetooth LE. They advertise their serial number in manufacturer data, and only talk to
/// the EcoFlow account they're bound to, so pairing needs that account's user ID.
/// </summary>
/// <param name="ble">The platform's Bluetooth LE: <see cref="BlePlatform.Default"/> unless given.</param>
public sealed class EcoFlowBleDriver(Action<string>? log = null, IBlePlatform? ble = null) : IUpsDriver
{
    public const string DriverId = "ecoflow-ble";

    /// <summary>EcoFlow's Bluetooth company ID, under which the manufacturer data is advertised.</summary>
    private const ushort CompanyId = 0xB5B5;

    /// <summary>The data channels EcoFlow devices use, per ha-ef-ble: the first, or Nordic UART on some models.</summary>
    private static readonly IReadOnlyList<GattChannel> Channels =
    [
        GattChannel.FromShortIds(0x0003, 0x0002),
        new(Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e"), Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e")),
    ];

    private readonly IBlePlatform _ble = ble ?? BlePlatform.Default;

    public string Id => DriverId;
    public string DisplayName => "EcoFlow (Bluetooth LE)";
    public bool RequiresUserId => true;

    public Task DiscoverAsync(Action<DiscoveredUps> onFound, TimeSpan duration, CancellationToken cancellationToken) =>
        _ble.ScanAsync(ad =>
        {
            foreach (var found in FromAdvertisement(ad)) onFound(found);
        }, duration, cancellationToken);

    /// <summary>The supported EcoFlow devices the advertisement names (normally one, or none).</summary>
    internal static IEnumerable<DiscoveredUps> FromAdvertisement(BleAdvertisement ad)
    {
        foreach (var data in ad.ManufacturerDataOf(CompanyId))
        {
            if (SerialFromAdvertisement(data) is not { } serial) continue;
            if (EcoFlowModels.FromSerial(serial) is not { } model) continue;
            yield return new DiscoveredUps(DriverId, BluetoothAddressFormat.Format(ad.Address), serial, model.Name, ad.Rssi);
        }
    }

    /// <summary>The serial number in EcoFlow manufacturer data: a version byte, then 16 ASCII characters.</summary>
    internal static string? SerialFromAdvertisement(byte[] data)
    {
        if (data.Length < 17) return null;
        var serial = Encoding.ASCII.GetString(data, 1, 16).TrimEnd('\0');
        return serial.Length >= 4 && serial.All(char.IsAsciiLetterOrDigit) ? serial : null;
    }

    public IUpsConnection CreateConnection(PairedUps device)
    {
        var model = EcoFlowModels.FromSerial(device.Name)
                    ?? throw new NotSupportedException($"EcoFlow device '{device.Name}' is not supported");
        if (string.IsNullOrWhiteSpace(device.UserId))
            throw new InvalidOperationException("No EcoFlow user ID: pair the UPS again and enter it");
        var address = BluetoothAddressFormat.Parse(device.Address);
        Action<string>? trace = BleTrace.Enabled ? message => log?.Invoke($"[trace] {message}") : null;
        return new EcoFlowConnection(model, device.Name, device.UserId.Trim(), BluetoothAddressFormat.Format(address),
            ct => _ble.ConnectAsync(address, Channels, "EcoFlow", trace, ct), log);
    }
}
