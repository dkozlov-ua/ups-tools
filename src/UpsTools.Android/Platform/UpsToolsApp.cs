using System.Net;
using Android.App;
using Android.OS;
using Android.Runtime;
using UpsTools.Core;
using UpsTools.Services;

namespace UpsTools.Droid.Platform;

/// <summary>
/// The process: one <see cref="MonitorController"/> for the activities and <see cref="MonitorService"/>, which runs
/// the monitors while it is in the foreground.
/// </summary>
[Application(Label = "UPS Tools", Icon = "@mipmap/ic_launcher", RoundIcon = "@mipmap/ic_launcher", AllowBackup = false)]
public sealed class UpsToolsApp(IntPtr handle, JniHandleOwnership ownership) : Application(handle, ownership)
{
    private static UpsToolsApp? _current;

    public static UpsToolsApp Current => _current ?? throw new InvalidOperationException("The app hasn't started");

    public MonitorController Controller { get; private set; } = null!;

    public Notifier Notifier { get; private set; } = null!;

    public override void OnCreate()
    {
        base.OnCreate();
        _current = this;
        var folder = FilesDir!.AbsolutePath;
        SettingsStore.DefaultFolder = folder;
        var log = new AppLog(folder);
        Notifier = new Notifier(this);
        var ble = new AndroidBle(this);
        IReadOnlyList<IUpsDriver> drivers = new UpsDrivers(log.Info, ble).All;
#if DEBUG
        // Debug builds only (the emulator has no Bluetooth): files/simulator.json, written with
        // 'adb shell run-as com.dkozlov.upstools', makes the simulated UPS the only one (see SimulatedUpsDriver).
        var simulator = Path.Combine(folder, "simulator.json");
        if (File.Exists(simulator))
        {
            drivers = [new SimulatedUpsDriver(simulator)];
            log.Info("Debug build: using the simulated UPS of simulator.json");
        }
#endif
        Controller = new MonitorController(new MainThread(), log, drivers, DeviceName(),
            Networks, Notifier) { Broadcast = new EventBroadcast(this) };
        log.Info($"UPS Tools {AppVersion.Of(this)} started on Android {Build.VERSION.Release}");
    }

    /// <summary>What the other devices on the network call this one: its name in Android's settings, or its model.</summary>
    private string DeviceName()
    {
        string? name = null;
        try
        {
            name = Android.Provider.Settings.Global.GetString(ContentResolver, "device_name");
        }
        catch (Exception)
        {
            // Not readable on this phone.
        }
        return string.IsNullOrWhiteSpace(name) ? $"{Build.Manufacturer} {Build.Model}".Trim() : name.Trim();
    }

    /// <summary>
    /// The IPv4 addresses of the phone's networks that can carry the announcements: Wi-Fi, Ethernet, its own hotspot;
    /// not the mobile network, where nobody else is.
    /// </summary>
    private static IReadOnlyList<IPAddress> Networks()
    {
        var addresses = new List<IPAddress>();
        var interfaces = Java.Net.NetworkInterface.NetworkInterfaces;
        while (interfaces?.HasMoreElements == true)
        {
            if (interfaces.NextElement() is not Java.Net.NetworkInterface n) continue;
            try
            {
                if (!n.IsUp || n.IsLoopback || n.IsVirtual || !n.SupportsMulticast()) continue;
                var name = n.Name ?? "";
                if (name.StartsWith("rmnet") || name.StartsWith("ccmni") || name.StartsWith("pdp") || name.StartsWith("dummy")) continue;
                var inet = n.InetAddresses;
                while (inet?.HasMoreElements == true)
                    if (inet.NextElement() is Java.Net.Inet4Address v4 && v4.GetAddress() is { } bytes)
                        addresses.Add(new IPAddress(bytes));
            }
            catch (Java.Net.SocketException)
            {
                // Went away while being looked at.
            }
        }
        return addresses;
    }

    /// <summary>Runs code on the main (UI) thread, in order.</summary>
    private sealed class MainThread : IUiThread
    {
        private readonly Handler _handler = new(Looper.MainLooper!);

        public void Post(Action action) => _handler.Post(action);
    }
}

internal static class AppVersion
{
    /// <summary>The app's version, e.g. "0.9.0".</summary>
    public static string Of(Android.Content.Context context)
    {
        var packages = context.PackageManager!;
        var info = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? packages.GetPackageInfo(context.PackageName!, Android.Content.PM.PackageManager.PackageInfoFlags.Of(0L))
#pragma warning disable CA1422, CS0618 // before Android 13
            : packages.GetPackageInfo(context.PackageName!, 0);
#pragma warning restore CA1422, CS0618
        return info?.VersionName ?? "?";
    }
}
