using UpsTools.Core.Ble;

namespace UpsTools.Core.Bluetti;

/// <param name="ble">The platform's Bluetooth LE: <see cref="BlePlatform.Default"/> unless given.</param>
public sealed class BluettiBleDriver(Action<string>? log = null, IBlePlatform? ble = null) : IUpsDriver
{
    public const string DriverId = "bluetti-ble";

    private static readonly IReadOnlyList<GattChannel> Channels = [GattChannel.FromShortIds(0xff01, 0xff02)];

    private readonly IBlePlatform _ble = ble ?? BlePlatform.Default;

    public string Id => DriverId;
    public string DisplayName => "Bluetti (Bluetooth LE)";

    public Task DiscoverAsync(Action<DiscoveredUps> onFound, TimeSpan duration, CancellationToken cancellationToken) =>
        _ble.ScanAsync(ad =>
        {
            if (FromAdvertisement(ad) is { } found) onFound(found);
        }, duration, cancellationToken);

    /// <summary>The Bluetti UPS that sent the advertisement, if a supported one did.</summary>
    internal static DiscoveredUps? FromAdvertisement(BleAdvertisement ad) =>
        BluettiModels.FromAdvertisedName(ad.LocalName ?? "") is { } model
            ? new DiscoveredUps(DriverId, BluetoothAddressFormat.Format(ad.Address), ad.LocalName!, model.Name, ad.Rssi)
            : null;

    public IUpsConnection CreateConnection(PairedUps device)
    {
        var model = BluettiModels.FromName(device.Model)
                    ?? throw new NotSupportedException($"Bluetti model '{device.Model}' is not supported");
        var address = BluetoothAddressFormat.Parse(device.Address);
        Action<string>? trace = BluettiConnection.TraceEnabled ? message => log?.Invoke($"[trace] {message}") : null;
        return new BluettiConnection(model, BluetoothAddressFormat.Format(address),
            ct => _ble.ConnectAsync(address, Channels, "Bluetti", trace, ct), log);
    }
}
