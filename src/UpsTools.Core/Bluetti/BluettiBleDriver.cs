using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Radios;

namespace UpsTools.Core.Bluetti;

public sealed class BluettiBleDriver(Action<string>? log = null) : IUpsDriver
{
    public const string DriverId = "bluetti-ble";

    public string Id => DriverId;
    public string DisplayName => "Bluetti (Bluetooth LE)";

    public async Task DiscoverAsync(Action<DiscoveredUps> onFound, TimeSpan duration, CancellationToken cancellationToken)
    {
        await EnsureBluetoothOnAsync();

        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        var stopped = new TaskCompletionSource<BluetoothError>(TaskCreationOptions.RunContinuationsAsynchronously);

        watcher.Received += (_, args) =>
        {
            var name = args.Advertisement.LocalName;
            if (BluettiModels.FromAdvertisedName(name) is not { } model) return;
            onFound(new DiscoveredUps(DriverId, BluetoothAddressFormat.Format(args.BluetoothAddress), name, model.Name, args.RawSignalStrengthInDBm));
        };
        watcher.Stopped += (_, args) => stopped.TrySetResult(args.Error);

        watcher.Start();
        try
        {
            var finished = await Task.WhenAny(stopped.Task, Task.Delay(duration, cancellationToken));
            if (finished == stopped.Task && stopped.Task.Result != BluetoothError.Success)
                throw new IOException($"Bluetooth scan stopped: {stopped.Task.Result}");
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the caller.
        }
        finally
        {
            watcher.Stop();
        }
    }

    public IUpsConnection CreateConnection(PairedUps device)
    {
        var model = BluettiModels.FromName(device.Model)
                    ?? throw new NotSupportedException($"Bluetti model '{device.Model}' is not supported");
        return new BluettiBleConnection(BluetoothAddressFormat.Parse(device.Address), model, log);
    }

    private static async Task EnsureBluetoothOnAsync()
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync()
                      ?? throw new IOException("No Bluetooth adapter found");
        if (!adapter.IsLowEnergySupported) throw new IOException("This Bluetooth adapter does not support Bluetooth LE");
        var radio = await adapter.GetRadioAsync();
        if (radio is not null && radio.State != RadioState.On) throw new IOException("Bluetooth is turned off");
    }
}

public static class BluetoothAddressFormat
{
    public static string Format(ulong address) =>
        string.Join(':', Enumerable.Range(0, 6).Select(i => ((address >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    public static ulong Parse(string address) =>
        Convert.ToUInt64(address.Replace(":", "").Replace("-", ""), 16);
}
