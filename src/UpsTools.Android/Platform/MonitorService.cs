using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.OS;
using UpsTools.Core.Sharing;

namespace UpsTools.Droid.Platform;

/// <summary>
/// Keeps the monitors running while the app isn't on screen: a foreground service (Android's equivalent of the tray
/// icon), with the status notification, a wake lock so that polls go on with the screen off
/// (<see cref="Services.AppSettings.KeepAwake"/>), and, while sharing, a multicast lock so that the phone hears the
/// other devices' announcements over Wi-Fi. It also tells the controller when the phone is plugged in or unplugged,
/// so that a device on mains power takes the UPSes over from it while it runs on its battery.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
public sealed class MonitorService : Service
{
    public const string ActionStop = "com.dkozlov.upstools.STOP";

    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);

    private MonitorController? _controller;
    private PowerManager.WakeLock? _wakeLock;
    private WifiManager.MulticastLock? _multicastLock;
    private Handler? _handler;
    private PowerReceiver? _powerReceiver;
    private bool _statusQueued;

    /// <summary>Whether it runs: the activities show "Monitoring is off" otherwise.</summary>
    public static bool Running { get; private set; }

    /// <summary>Starts monitoring (or keeps it running), from the foreground or a broadcast that allows it.</summary>
    public static void Start(Context context) =>
        context.StartForegroundService(new Intent(context, typeof(MonitorService)));

    /// <summary>Stops monitoring until the app is opened again.</summary>
    public static void Stop(Context context) =>
        context.StartService(new Intent(context, typeof(MonitorService)).SetAction(ActionStop));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var app = UpsToolsApp.Current;
        var controller = app.Controller;
        if (intent?.Action == ActionStop)
        {
            controller.Log.Info("Monitoring stopped from the notification or the app");
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        // Android wants the notification within seconds of the start, before anything else.
        var notification = app.Notifier.Status(controller);
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(Notifier.StatusId, notification, ForegroundService.TypeConnectedDevice);
        else
            StartForeground(Notifier.StatusId, notification);

        if (_controller is null)
        {
            _controller = controller;
            _handler = new Handler(Looper.MainLooper!);
            controller.Changed += QueueStatus;
            controller.SharingChanged += ApplyLocks;
            Running = true;
            _powerReceiver = new PowerReceiver(controller);
            // A sticky broadcast: what the phone runs on now comes back at once, and each change after.
            if (RegisterReceiver(_powerReceiver, new IntentFilter(Intent.ActionBatteryChanged)) is { } battery)
                controller.SetPowerSource(PowerReceiver.SourceOf(battery));
            controller.Start();
            ApplyLocks();
        }
        QueueStatus();
        return StartCommandResult.Sticky;
    }

    /// <summary>Updates the status notification at most once a second: readings of several UPSes come together.</summary>
    internal void QueueStatus()
    {
        if (_statusQueued || _handler is null) return;
        _statusQueued = true;
        _handler.PostDelayed(() =>
        {
            _statusQueued = false;
            if (_controller is { } controller && Running)
            {
                UpsToolsApp.Current.Notifier.UpdateStatus(UpsToolsApp.Current.Notifier.Status(controller));
                Glance.Update(this);
            }
        }, (long)StatusInterval.TotalMilliseconds);
    }

    /// <summary>Holds the wake lock and the multicast lock as the settings say.</summary>
    internal void ApplyLocks()
    {
        if (_controller is not { } controller) return;

        if (controller.Settings.KeepAwake && _wakeLock is null)
        {
            var power = (PowerManager)GetSystemService(PowerService)!;
            _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "UpsTools:monitor");
            _wakeLock!.SetReferenceCounted(false);
            _wakeLock.Acquire();
        }
        else if (!controller.Settings.KeepAwake && _wakeLock is not null)
        {
            _wakeLock.Release();
            _wakeLock = null;
        }

        var sharing = controller.Settings.Sharing.Enabled;
        if (sharing && _multicastLock is null)
        {
            var wifi = (WifiManager?)ApplicationContext!.GetSystemService(WifiService);
            _multicastLock = wifi?.CreateMulticastLock("UpsTools:sharing");
            _multicastLock?.SetReferenceCounted(false);
            _multicastLock?.Acquire();
        }
        else if (!sharing && _multicastLock is not null)
        {
            _multicastLock.Release();
            _multicastLock = null;
        }
    }

    /// <summary>The running service, for settings that change its locks.</summary>
    public static void SettingsChanged()
    {
        if (Instance is not { } service) return;
        service.ApplyLocks();
        service.QueueStatus();
    }

    private static MonitorService? Instance { get; set; }

    public override void OnCreate()
    {
        base.OnCreate();
        Instance = this;
    }

    public override void OnDestroy()
    {
        Running = false;
        Glance.Update(this); // the widget and the tile say monitoring is off
        Instance = null;
        if (_controller is { } controller)
        {
            controller.Changed -= QueueStatus;
            controller.SharingChanged -= ApplyLocks;
            _ = controller.StopAsync();
        }
        if (_powerReceiver is not null) UnregisterReceiver(_powerReceiver);
        _powerReceiver = null;
        _controller = null;
        _wakeLock?.Release();
        _wakeLock = null;
        _multicastLock?.Release();
        _multicastLock = null;
        base.OnDestroy();
    }
}

/// <summary>Hears the battery's broadcasts (on the main thread) and tells the controller whether the phone is plugged in.</summary>
internal sealed class PowerReceiver(MonitorController controller) : BroadcastReceiver
{
    /// <summary>Plugged in (to a charger, a PC's USB port or a wireless pad): mains power; otherwise its battery.</summary>
    public static PowerSource SourceOf(Intent battery) =>
        battery.GetIntExtra(BatteryManager.ExtraPlugged, 0) != 0 ? PowerSource.Mains : PowerSource.Battery;

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action == Intent.ActionBatteryChanged) controller.SetPowerSource(SourceOf(intent));
    }
}
