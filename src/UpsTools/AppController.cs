using System.Collections.ObjectModel;
using System.Windows.Threading;
using UpsTools.Core;
using UpsTools.Services;
using UpsTools.ViewModels;
using UpsTools.Views;

namespace UpsTools;

/// <summary>Owns settings, the UPS monitor and the view models; everything here runs on the UI thread.</summary>
public sealed class AppController
{
    private readonly Dispatcher _ui;
    private readonly SettingsStore _store;
    private readonly UpsDrivers _drivers;
    private readonly Func<ScriptAction, string, IReadOnlyDictionary<string, string?>, CancellationToken, Task<StepResult>> _runScript;
    private readonly PowerRunner _power;
    private UpsMonitor? _monitor;
    private ITray? _tray;
    private INotifications? _notifier;
    private DateTimeOffset? _gridLostAt;
    private readonly Dictionary<UpsEventKind, (ScriptChain Chain, CancellationTokenSource Cancel)> _chains = [];

    public AppController(Dispatcher ui) : this(ui, new AppLog())
    {
    }

    /// <param name="ui">The UI thread: everything here runs on it.</param>
    /// <param name="log">The log; its folder holds the settings too.</param>
    /// <param name="drivers">The UPS drivers: the real ones (<see cref="UpsDrivers"/>) unless a test says otherwise.</param>
    /// <param name="runScript">Runs a script: <see cref="ScriptRunner.RunAsync"/> unless a test says otherwise.</param>
    /// <param name="power">
    /// Sleeps, hibernates or shuts down the PC: for real only with the real UPS drivers, otherwise (the simulator, a
    /// test's drivers) it just logs.
    /// </param>
    /// <param name="countdown">The countdown before a power action: <see cref="CountdownWindow"/> unless a test says otherwise.</param>
    internal AppController(Dispatcher ui, AppLog log, IReadOnlyList<IUpsDriver>? drivers = null,
        Func<ScriptAction, string, IReadOnlyDictionary<string, string?>, CancellationToken, Task<StepResult>>? runScript = null,
        IPowerControl? power = null, PowerCountdown? countdown = null)
    {
        _ui = ui;
        Log = log;
        _store = new SettingsStore(Log, log.Folder);
        Settings = _store.Load();
        _drivers = drivers is null ? new UpsDrivers(Log.Info) : new UpsDrivers(drivers);
        Elevated = new ElevatedTasks(Log);
        _runScript = runScript ?? new ScriptRunner(Log, Elevated).RunAsync;
        _power = new PowerRunner(Log,
            power ?? (drivers is null && SimulatedUpsDriver.FromEnvironment() is null ? new WindowsPower() : new SimulatedPower(Log)),
            countdown ?? CountdownWindow.ShowAsync);

        Dashboard = new DashboardViewModel();
        foreach (var entry in Log.Recent) Dashboard.AddActivity(entry);
        Log.EntryAdded += entry => _ui.InvokeAsync(() => Dashboard.AddActivity(entry));

        EventScripts = new ObservableCollection<EventScriptsViewModel>(
            Enum.GetValues<UpsEventKind>().Select(kind => new EventScriptsViewModel(kind, Settings.ScriptsFor(kind), Elevated, Save)));

        Configuration = new ConfigurationViewModel(Settings, Log, Save, ApplyToMonitor);

        Dashboard.SetDevice(Settings.Ups);
    }

    public AppLog Log { get; }
    public ElevatedTasks Elevated { get; }
    public AppSettings Settings { get; }
    public string SettingsFolder => _store.Folder;
    public DashboardViewModel Dashboard { get; }
    public ObservableCollection<EventScriptsViewModel> EventScripts { get; }
    public ConfigurationViewModel Configuration { get; }
    public IReadOnlyList<IUpsDriver> Drivers => _drivers.All;

    public void Save() => _store.Save(Settings);

    public void AttachTray(ITray tray, INotifications notifier)
    {
        _tray = tray;
        _notifier = notifier;
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

        var monitor = new UpsMonitor(connection, TimeSpan.FromSeconds(Settings.UpdatePeriodSeconds)) { Rules = Settings.Rules() };
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
        monitor.EventRaised += e => _ui.InvokeAsync(() =>
        {
            if (_monitor == monitor) OnUpsEvent(e.Kind, e.Status, e.GridGoneSince);
        });

        _monitor = monitor;
        Dashboard.SetDevice(ups);
        Dashboard.SetLink(LinkStatus.Connecting);
        UpdateTray();
        monitor.Start();
    }

    /// <summary>Applies the settings the monitor uses: its period and the events' limits.</summary>
    private void ApplyToMonitor()
    {
        if (_monitor is null) return;
        var period = TimeSpan.FromSeconds(Settings.UpdatePeriodSeconds);
        if (_monitor.Period != period) _monitor.Period = period;
        _monitor.Rules = Settings.Rules();
    }

    public async Task StopMonitoringAsync()
    {
        var monitor = _monitor;
        _monitor = null;
        _gridLostAt = null;
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

    /// <summary>"Run now" on an event: its enabled scripts, as if the event had fired, without the notification.</summary>
    public void RunScripts(EventScriptsViewModel group, string reason) =>
        StartChain(group.Kind, group.EnabledActions.ToList(), reason, Variables(group.Kind, manual: true),
            finished: (c, _) => NotifyProblems(c, "Manual run"));

    /// <summary>"Run now" on one script.</summary>
    public void RunScript(ScriptAction action, string reason)
    {
        var kind = EventScripts.FirstOrDefault(g => g.Items.Any(i => i.Model == action))?.Kind;
        StartChain(null, [action], reason, Variables(kind, manual: true), finished: (c, _) => NotifyProblems(c, "Manual run"));
    }

    /// <summary>What the scripts of <paramref name="kind"/> are told about the UPS (<see cref="ScriptEnvironment"/>).</summary>
    private IReadOnlyDictionary<string, string?> Variables(UpsEventKind? kind, bool manual, UpsStatus? status = null,
        TimeSpan? outage = null) =>
        ScriptEnvironment.For(kind, status ?? _monitor?.LastStatus,
            reachable: _monitor is null ? null : _monitor.LinkState != UpsLinkState.Unreachable && kind != UpsEventKind.UpsUnreachable,
            outage ?? (_gridLostAt is { } lost ? (status?.Timestamp ?? DateTimeOffset.Now) - lost : null), manual);

    /// <summary>
    /// Starts <paramref name="actions"/> in order (see <see cref="ScriptChain"/>) and returns the chain as it
    /// stands once it is waiting for a script or done. For an event (<paramref name="kind"/>), the chain
    /// replaces one of the same event that is still running, and the opposite event can stop it.
    /// </summary>
    /// <param name="finished">Called when the chain is done; the flag says whether it had to wait for a script.</param>
    private ScriptChain StartChain(UpsEventKind? kind, IReadOnlyList<ScriptAction> actions, string reason,
        IReadOnlyDictionary<string, string?> environment, Action<ScriptChain, bool> finished)
    {
        if (actions.Count == 0) Log.Info($"{reason}: no scripts to run");
        if (kind is { } k) StopChain(k, $"{reason} started again");

        // A built-in action is waited for (its countdown, and sleep until the PC wakes up), and stopping the chain stops its countdown.
        var chain = new ScriptChain([.. actions.Select(a => new ScriptStep(a.Name, a.WaitForExit || a.Power is not null,
            UndoneByStopping: a.Power is not null,
            TimeLimit: a.Power is null && a.WaitForExit && a.WaitLimitMinutes > 0 ? a.WaitLimitMinutes * WaitLimitUnit : null))]);
        var cancel = new CancellationTokenSource();
        if (kind is { } key) _chains[key] = (chain, cancel);

        var run = chain.RunAsync((i, _, ct) =>
                actions[i].Power is null ? _runScript(actions[i], reason, environment, ct) : _power.RunAsync(actions[i], reason, ct),
            cancel.Token,
            timedOut: step => Log.Warn($"{reason}: {step.Name} is still running after {UpsFormat.Duration(step.TimeLimit!.Value)}; going on without it"));
        _ = FinishAsync();
        return chain;

        async Task FinishAsync()
        {
            var waited = !run.IsCompleted;
            try { await run; }
            finally
            {
                if (kind is { } key && _chains.TryGetValue(key, out var current) && current.Chain == chain) _chains.Remove(key);
                cancel.Dispose();
            }

            if (chain.StoppedAt is { } stop && chain.Skipped.Count > 0)
                Log.Warn($"{reason}: {stop.Name} failed, so {ScriptReport.List(chain.Skipped)} didn't run");
            finished(chain, waited);
        }
    }

    /// <summary>Stops a running chain of <paramref name="kind"/>: it starts no more scripts.</summary>
    /// <returns>The scripts it won't run, or null when none of that event was running.</returns>
    private IReadOnlyList<string>? StopChain(UpsEventKind kind, string why)
    {
        if (!_chains.Remove(kind, out var running) || running.Chain.Done) return null;
        var skipped = running.Chain.Unstarted;
        running.Cancel.Cancel();
        Log.Info(skipped.Count > 0
            ? $"{why}: stopped the {kind.Title()} scripts; {ScriptReport.List(skipped)} won't run"
            : $"{why}: stopped waiting for the {kind.Title()} scripts");
        return skipped;
    }

    private void NotifyProblems(ScriptChain chain, string label)
    {
        if (ScriptReport.Problems(chain, label) is { } problem)
            _notifier?.Show("scripts", TrayState.Problem, problem.Title, problem.Body, attribution: DeviceTitle, important: true);
    }

    public void SendTestNotification()
    {
        _notifier?.Show("test", TrayState.GridOnline, "Test notification",
            "This is how UPS Tools tells you that the grid went down or came back.", attribution: DeviceTitle);
        Log.Info("Sent a test notification");
    }

    private string? DeviceTitle => Settings.Ups is { } ups ? $"Bluetti {ups.Model}" : null;

    /// <summary>What one of <see cref="ScriptAction.WaitLimitMinutes"/> is: a minute, unless a test says otherwise.</summary>
    internal static TimeSpan WaitLimitUnit { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The running monitor, if any (for the tests).</summary>
    internal UpsMonitor? Monitor => _monitor;

    public void OnResumedFromSleep()
    {
        Log.Info("PC resumed from sleep; reconnecting to the UPS");
        _monitor?.NotifyResumed();
    }

    /// <param name="gridGoneSince">For "Grid lost": when the grid went, if earlier than the reading (a delay was waited out).</param>
    internal void OnUpsEvent(UpsEventKind kind, UpsStatus? status, DateTimeOffset? gridGoneSince = null)
    {
        Log.Event(kind.Title());

        TimeSpan? outage = null;
        var at = status?.Timestamp ?? DateTimeOffset.Now;
        if (kind == UpsEventKind.GridLost)
        {
            _gridLostAt = gridGoneSince ?? at;
        }
        else if (kind == UpsEventKind.GridRestored)
        {
            if (_gridLostAt is { } lost) outage = at - lost;
            _gridLostAt = null;
        }

        // The grid came back (or the UPS answers again): what was queued for the earlier state mustn't run now.
        var stopped = new List<(UpsEventKind, IReadOnlyList<string>)>();
        foreach (var other in kind.Stops())
            if (StopChain(other, kind.Title()) is { } skipped) stopped.Add((other, skipped));

        var notify = Settings.NotifyOn(kind);
        var chain = StartChain(kind, EventScripts.First(g => g.Kind == kind).EnabledActions.ToList(), kind.Title(),
            Variables(kind, manual: false, status, outage),
            // Problems found before the notification below went out are in it already.
            finished: (c, waited) => { if (waited && notify) NotifyProblems(c, kind.Title()); });

        // "UPS unreachable" comes without a reading; the last good one says when the UPS last answered.
        if (notify)
            _notifier?.Show(UpsNotice.For(kind, status ?? _monitor?.LastStatus, outage, chain, stopped,
                unreachable: kind == UpsEventKind.BatteryLow && status is null), DeviceTitle);
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
