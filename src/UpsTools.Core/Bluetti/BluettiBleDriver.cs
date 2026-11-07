using UpsTools.Core.Ble;

namespace UpsTools.Core.Bluetti;

public sealed class BluettiBleDriver(Action<string>? log = null) : IUpsDriver
{
    public const string DriverId = "bluetti-ble";

    private static readonly IReadOnlyList<GattChannel> Channels = [GattChannel.FromShortIds(0xff01, 0xff02)];

    public string Id => DriverId;
    public string DisplayName => "Bluetti (Bluetooth LE)";

    public Task DiscoverAsync(Action<DiscoveredUps> onFound, TimeSpan duration, CancellationToken cancellationToken) =>
        BleAdapter.ScanAsync(args =>
        {
            var name = args.Advertisement.LocalName;
            if (BluettiModels.FromAdvertisedName(name) is not { } model) return;
            onFound(new DiscoveredUps(DriverId, BluetoothAddressFormat.Format(args.BluetoothAddress), name, model.Name, args.RawSignalStrengthInDBm));
        }, duration, cancellationToken);

    public IUpsConnection CreateConnection(PairedUps device)
    {
        var model = BluettiModels.FromName(device.Model)
                    ?? throw new NotSupportedException($"Bluetti model '{device.Model}' is not supported");
        var address = BluetoothAddressFormat.Parse(device.Address);
        Action<string>? trace = BluettiConnection.TraceEnabled ? message => log?.Invoke($"[trace] {message}") : null;
        return new BluettiConnection(model, BluetoothAddressFormat.Format(address),
            ct => GattLink.ConnectAsync(address, Channels, "Bluetti", trace, ct), log);
    }
}
