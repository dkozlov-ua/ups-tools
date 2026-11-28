using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.Content.PM;
using UpsTools.Core.Ble;
using Context = Android.Content.Context;

namespace UpsTools.Droid.Platform;

/// <summary>
/// Bluetooth LE through Android's APIs: one scan shared by every driver that asks (Android throttles apps that start
/// scans often), and GATT links (<see cref="AndroidGattLink"/>).
/// </summary>
public sealed class AndroidBle(Context context) : IBlePlatform
{
    private readonly Lock _lock = new();
    private readonly List<Subscriber> _subscribers = [];
    private Scan? _scan;

    private sealed record Subscriber(Action<BleAdvertisement> OnReceived, TaskCompletionSource<Exception> Failed);

    private BluetoothAdapter Adapter()
    {
        var manager = (BluetoothManager?)context.GetSystemService(Context.BluetoothService);
        var adapter = manager?.Adapter ?? throw new IOException("This phone has no Bluetooth");
        if (!Permissions.HasBluetooth(context))
            throw new IOException("UPS Tools isn't allowed to use Bluetooth: open it and allow access to nearby devices");
        if (!adapter.IsEnabled) throw new IOException("Bluetooth is turned off");
        return adapter;
    }

    public async Task ScanAsync(Action<BleAdvertisement> onReceived, TimeSpan duration, CancellationToken cancellationToken)
    {
        var adapter = Adapter();
        var subscriber = new Subscriber(onReceived, new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_lock)
        {
            if (_scan is null)
            {
                var scanner = adapter.BluetoothLeScanner ?? throw new IOException("Bluetooth is turned off");
                var scan = new Scan(this, scanner);
                var settings = new ScanSettings.Builder().SetScanMode(Android.Bluetooth.LE.ScanMode.LowLatency)!.Build();
                scanner.StartScan(null, settings, scan);
                _scan = scan;
            }
            _subscribers.Add(subscriber);
        }

        try
        {
            var finished = await Task.WhenAny(subscriber.Failed.Task, Task.Delay(duration, cancellationToken));
            if (finished == subscriber.Failed.Task) throw subscriber.Failed.Task.Result;
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the caller.
        }
        finally
        {
            lock (_lock)
            {
                _subscribers.Remove(subscriber);
                if (_subscribers.Count == 0 && _scan is { } scan)
                {
                    _scan = null;
                    try { scan.Scanner.StopScan(scan); }
                    catch (Exception) { /* Bluetooth went off meanwhile */ }
                }
            }
        }
    }

    private void Received(ScanResult result)
    {
        if (result.Device?.Address is not { } address) return;
        var raw = result.ScanRecord?.GetBytes() ?? [];
        var ad = AdvertisementParser.Parse(BluetoothAddressFormat.Parse(address), (short)result.Rssi, raw);
        Subscriber[] subscribers;
        lock (_lock) subscribers = [.. _subscribers];
        foreach (var s in subscribers) s.OnReceived(ad);
    }

    private void Failed(ScanFailure error)
    {
        var message = (int)error switch
        {
            6 => "Bluetooth scan refused: scanning too often; wait half a minute and try again",
            _ => $"Bluetooth scan failed ({error})",
        };
        Subscriber[] subscribers;
        lock (_lock)
        {
            subscribers = [.. _subscribers];
            _scan = null;
        }
        foreach (var s in subscribers) s.Failed.TrySetResult(new IOException(message));
    }

    private sealed class Scan(AndroidBle owner, BluetoothLeScanner scanner) : ScanCallback
    {
        public BluetoothLeScanner Scanner { get; } = scanner;

        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
        {
            if (result is not null) owner.Received(result);
        }

        public override void OnBatchScanResults(IList<ScanResult>? results)
        {
            foreach (var result in results ?? []) owner.Received(result);
        }

        public override void OnScanFailed(ScanFailure errorCode) => owner.Failed(errorCode);
    }

    public Task<IBleLink> ConnectAsync(ulong address, IReadOnlyList<GattChannel> channels, string brand, Action<string>? trace,
        CancellationToken cancellationToken)
    {
        var adapter = Adapter();
        var device = adapter.GetRemoteDevice(BluetoothAddressFormat.Format(address))
                     ?? throw new IOException("Bluetooth device not found");
        return AndroidGattLink.ConnectAsync(context, device, channels, brand, trace, cancellationToken);
    }
}

/// <summary>The runtime permissions the app needs, by Android version.</summary>
public static class Permissions
{
    /// <summary>To find and connect to UPSes: "Nearby devices" from Android 12; before, location (which scans need).</summary>
    public static string[] Bluetooth =>
        OperatingSystem.IsAndroidVersionAtLeast(31)
            ? [Android.Manifest.Permission.BluetoothScan, Android.Manifest.Permission.BluetoothConnect]
            : [Android.Manifest.Permission.AccessFineLocation];

    /// <summary>To show notifications: asked for from Android 13.</summary>
    public static string[] Notifications =>
        OperatingSystem.IsAndroidVersionAtLeast(33) ? [Android.Manifest.Permission.PostNotifications] : [];

    public static bool HasBluetooth(Context context) => Bluetooth.All(p => Granted(context, p));

    public static bool HasNotifications(Context context) => Notifications.All(p => Granted(context, p));

    public static string[] Missing(Context context) =>
        [.. Bluetooth.Concat(Notifications).Where(p => !Granted(context, p))];

    private static bool Granted(Context context, string permission) =>
        context.CheckSelfPermission(permission) == Permission.Granted;
}
