using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Radios;

namespace UpsTools.Core.Ble;

/// <summary>The PC's Bluetooth LE adapter: checks that it can be used, and scans.</summary>
internal static class BleAdapter
{
    /// <summary>Throws with a readable message when there is no usable Bluetooth LE adapter.</summary>
    public static async Task EnsureOnAsync()
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync()
                      ?? throw new IOException("No Bluetooth adapter found");
        if (!adapter.IsLowEnergySupported) throw new IOException("This Bluetooth adapter does not support Bluetooth LE");
        var radio = await adapter.GetRadioAsync();
        if (radio is not null && radio.State != RadioState.On) throw new IOException("Bluetooth is turned off");
    }

    /// <summary>Reports every advertisement heard (scan responses too) until the duration elapses or the token is cancelled.</summary>
    public static async Task ScanAsync(Action<BluetoothLEAdvertisementReceivedEventArgs> onReceived, TimeSpan duration,
        CancellationToken cancellationToken)
    {
        await EnsureOnAsync();

        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        var stopped = new TaskCompletionSource<BluetoothError>(TaskCreationOptions.RunContinuationsAsynchronously);

        watcher.Received += (_, args) => onReceived(args);
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
}

public static class BluetoothAddressFormat
{
    public static string Format(ulong address) =>
        string.Join(':', Enumerable.Range(0, 6).Select(i => ((address >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    public static ulong Parse(string address) =>
        Convert.ToUInt64(address.Replace(":", "").Replace("-", ""), 16);
}
