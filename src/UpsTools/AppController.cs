using System.Collections.ObjectModel;
using System.Windows.Threading;
using UpsTools.Core;
using UpsTools.Services;
using UpsTools.ViewModels;

namespace UpsTools;

/// <summary>Owns settings, the UPS monitor and the view models; everything here runs on the UI thread.</summary>
public sealed class AppController
{
    private readonly Dispatcher _ui;
    private readonly SettingsStore _store;
    private readonly UpsDrivers _drivers;
    private readonly ScriptRunner _scripts;
    private UpsMonitor? _monitor;
    private TrayIcon? _tray;

    public AppController(Dispatcher ui)
    {
        _ui = ui;
        Log = new AppLog();
        _store = new SettingsStore(Log);
        Settings = _store.Load();
        _drivers = new UpsDrivers(Log.Info);
        _scripts = new ScriptRunner(Log);

        Dashboard = new DashboardViewModel();
        foreach (var entry in Log.Recent) Dashboard.AddActivity(entry);
        Log.EntryAdded += entry => _ui.InvokeAsync(() => Dashboard.AddActivity(entry));

        EventScripts = new ObservableCollection<EventScriptsViewModel>(
            Enum.GetValues<UpsEventKind>().Select(kind => new EventScriptsViewModel(kind, Settings.ScriptsFor(kind), Save)));

        Configuration = new ConfigurationViewModel(Settings, Log, Save, period =>
        {
            if (_monitor is not null) _monitor.Period = period;
        });

        Dashboard.SetDevice(Settings.Ups);
    }

    public AppLog Log { get; }
    public AppSettings Settings { get; }
    public DashboardViewModel Dashboard { get; }
    public ObservableCollection<EventScriptsViewModel> EventScripts { get; }
    public ConfigurationViewModel Configuration { get; }
    public IReadOnlyList<IUpsDriver> Drivers => _drivers.All;

    public void Save() => _store.Save(Settings);

    public void AttachTray(TrayIcon tray)
    {
        _tray = tray;
        UpdateTray();
    }

    public void StartMonitoring()
    {
        if (_monitor is not null) return;
        if (Settings.Ups is not { } ups)
        {
            Dashboard.SetDevice(null);
            UpdateTray();
            return;
        }

        IUpsConnection connection;
        try
        {
            var driver = _drivers.Find(ups.DriverId) ?? throw new NotSupportedException($"Unknown UPS driver '{ups.DriverId}'");
            connection = driver.CreateConnection(ups);
        }
        catch (Exception ex)
        {
            Log.Warn($"Cannot monitor {ups.Name}: {ex.Message}");
            return;
        }

        var monitor = new UpsMonitor(connection, TimeSpan.FromSeconds(Settings.UpdatePeriodSeconds));
        monitor.Log += Log.Info;
        monitor.StatusUpdated += status => _ui.InvokeAsync(() =>
        {
            if (_monitor != monitor) return;
            Dashboard.SetDevice(Settings.Ups, status.Serial);
            Dashboard.Apply(status);
            UpdateTray();
        });
        monitor.LinkStateChanged += state => _ui.InvokeAsync(() =>
        {
            if (_monitor != monitor) return;
            Dashboard.SetLink(state switch
            {
                UpsLinkState.Online => LinkStatus.Online,
                UpsLinkState.Retrying => LinkStatus.Retrying,
                UpsLinkState.Unreachable => LinkStatus.Unreachable,
                _ => LinkStatus.Connecting,
            });
            UpdateTray();
        });
        monitor.EventRaised += (kind, status) => _ui.InvokeAsync(() =>
        {
            if (_monitor == monitor) OnUpsEvent(kind, status);
        });

        _monitor = monitor;
        Dashboard.SetDevice(ups);
        Dashboard.SetLink(LinkStatus.Connecting);
        UpdateTray();
        monitor.Start();
    }

    public async Task StopMonitoringAsync()
    {
        var monitor = _monitor;
        _monitor = null;
        if (monitor is not null) await monitor.DisposeAsync();
    }

    /// <summary>Call with monitoring stopped (the pairing dialog needs the Bluetooth link to itself).</summary>
    public void Pair(PairedUps ups)
    {
        Settings.Ups = ups;
        Save();
        Configuration.RefreshPairing();
        Log.Info($"Paired with {ups.Name} ({ups.Address})");
        StartMonitoring();
    }

    public async Task ForgetAsync()
    {
        await StopMonitoringAsync();
        var old = Settings.Ups;
        Settings.Ups = null;
        Save();
        Configuration.RefreshPairing();
        Dashboard.SetDevice(null);
        UpdateTray();
        if (old is not null) Log.Info($"Forgot {old.Name}");
    }

    public void RunScripts(EventScriptsViewModel group, string reason)
    {
        var actions = group.EnabledActions.ToList();
        if (actions.Count == 0)
        {
            Log.Info($"{reason}: no scripts to run");
            return;
        }
        foreach (var action in actions) _scripts.Run(action, reason);
    }

    public void RunScript(ScriptAction action, string reason) => _scripts.Run(action, reason);

    public void OnResumedFromSleep()
    {
        Log.Info("PC resumed from sleep; reconnecting to the UPS");
        _monitor?.NotifyResumed();
    }

    private void OnUpsEvent(UpsEventKind kind, UpsStatus? status)
    {
        Log.Event(kind.Title());

        var battery = status?.BatteryPercent is { } soc ? $" Battery at {soc} %." : "";
        switch (kind)
        {
            case UpsEventKind.GridLost:
                _tray?.Notify("Grid lost", $"The UPS is running on battery.{battery}", warning: true);
                break;
            case UpsEventKind.GridRestored:
                _tray?.Notify("Grid restored", $"Mains power is back.{battery}");
                break;
            case UpsEventKind.UpsUnreachable:
                _tray?.Notify("UPS unreachable", "The UPS stopped answering over Bluetooth.", warning: true);
                break;
        }

        RunScripts(EventScripts.First(g => g.Kind == kind), kind.Title());
    }

    private void UpdateTray()
    {
        if (_tray is null) return;

        if (Settings.Ups is null)
        {
            _tray.Update(TrayState.Neutral, "No UPS paired");
            return;
        }

        var d = Dashboard;
        switch (d.Link)
        {
            case LinkStatus.Unreachable:
                _tray.Update(TrayState.Problem, "UPS unreachable");
                return;
            case LinkStatus.Connecting:
                _tray.Update(TrayState.Neutral, "Connecting to the UPS…");
                return;
        }

        var status = _monitor?.LastStatus;
        if (status?.GridPresent == false)
            _tray.Update(TrayState.OnBattery, $"On battery · {d.BatteryValue} · {d.RuntimeValue} left");
        else if (status?.GridPresent == true)
            _tray.Update(TrayState.GridOnline, $"Grid online · {d.BatteryValue} · {d.OutputValue} load");
        else
            _tray.Update(TrayState.Neutral, d.LinkText);
    }
}
