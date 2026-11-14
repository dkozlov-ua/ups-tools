using System.Collections.ObjectModel;
using System.Windows.Threading;
using UpsTools.Core;
using UpsTools.Core.Sharing;
using UpsTools.Services;
using UpsTools.ViewModels;
using UpsTools.Views;

namespace UpsTools;

/// <summary>
/// Owns the settings, the paired UPSes (<see cref="UpsUnit"/>: each with its monitor, scripts and notifications) and
/// the view models; everything here runs on the UI thread.
/// </summary>
public sealed class AppController : ObservableObject
{
    private readonly Dispatcher _ui;
    private readonly SettingsStore _store;
    private readonly UpsDrivers _drivers;
    private readonly Func<ScriptAction, string, IReadOnlyDictionary<string, string?>, CancellationToken, Task<StepResult>> _runScript;
    private readonly PowerRunner _power;
    private readonly DashboardViewModel _noUps = new();
    private ITray? _tray;
    private INotifications? _notifier;
    private SharingServer? _server;
    private volatile UpsCoordinator? _coordinator;

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

        foreach (var entry in Log.Recent) Activity.Post(entry);
        Log.EntryAdded += entry => _ui.InvokeAsync(() => Activity.Post(entry));

        Configuration = new ConfigurationViewModel(Settings, Log, Save, ApplyPeriod);
        Sharing = new SharingViewModel(Settings.Sharing, () =>
        {
            Save();
            StartSharing();
        });

        foreach (var ups in Settings.Upses) Upses.Add(CreateUnit(ups));
        Upses.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasUps));
            OnPropertyChanged(nameof(HasSeveral));
        };
        RefreshTitles();
        Selected = Upses.FirstOrDefault();
    }

    public AppLog Log { get; }
    public ElevatedTasks Elevated { get; }
    public AppSettings Settings { get; }
    public string SettingsFolder => _store.Folder;
    public IReadOnlyList<IUpsDriver> Drivers => _drivers.All;

    /// <summary>The app-wide settings.</summary>
    public ConfigurationViewModel Configuration { get; }

    /// <summary>Sharing this PC's UPSes with other PCs.</summary>
    public SharingViewModel Sharing { get; }

    /// <summary>What this PC shares: the UPSes it is connected to itself, with their latest readings.</summary>
    public SharingHub Hub { get; } = new();

    /// <summary>The paired UPSes, in the order they were paired.</summary>
    public ObservableCollection<UpsUnit> Upses { get; } = [];

    public bool HasUps => Upses.Count > 0;

    /// <summary>More than one UPS is paired: the window shows which one it is about.</summary>
    public bool HasSeveral => Upses.Count > 1;

    /// <summary>The UPS the dashboard, the scripts and its settings show.</summary>
    public UpsUnit? Selected
    {
        get;
        set
        {
            if (Set(ref field, value)) OnPropertyChanged(nameof(Dashboard));
        }
    }

    /// <summary>The selected UPS's readings, or "No UPS paired".</summary>
    public DashboardViewModel Dashboard => Selected?.Dashboard ?? _noUps;

    /// <summary>What the log says, about every UPS.</summary>
    public ActivityFeed Activity { get; } = new();

    /// <summary>The EcoFlow user ID a UPS was paired with, to start the next pairing with.</summary>
    public string? KnownUserId => Upses.Select(u => u.Device.UserId).LastOrDefault(id => id is not null);

    public void Save() => _store.Save(Settings);

    public void AttachTray(ITray tray, INotifications notifier)
    {
        _tray = tray;
        _notifier = notifier;
        UpdateTray();
    }

    private UpsUnit CreateUnit(UpsSettings settings)
    {
        UpsUnit unit = null!;
        unit = new UpsUnit(settings, Elevated, Save, () => ApplyRules(unit), RefreshTitles);
        return unit;
    }

    /// <summary>
    /// Names each UPS (<see cref="UpsUnit.Title"/>): two of the same model without a name of their own are told apart
    /// by what they call themselves. With several, the log says which one each line is about.
    /// </summary>
    private void RefreshTitles()
    {
        foreach (var unit in Upses)
        {
            var title = unit.Settings.Label ?? unit.Device.Title;
            if (unit.Settings.Label is null && Upses.Count(u => u.Settings.Label is null && u.Device.Title == unit.Device.Title) > 1)
                title += $" ({unit.Device.Name})";
            unit.Title = title;
            unit.LogPrefix = HasSeveral ? title + ": " : "";
            unit.Dashboard.SetDevice(unit.Device, title: title);
            Hub.Rename(unit.Device, title);
            _coordinator?.Rename(unit.Device, title);
        }
        UpdateTray();
    }

    /// <summary>
    /// Starts sharing with the other PCs on the network, or stops, as the settings say (again, e.g. with a new code).
    /// While sharing, this PC finds the others that share with the same code, and they agree which one connects to
    /// each UPS over Bluetooth (<see cref="UpsCoordinator"/>); the others read it through that one (<see cref="SharingServer"/>).
    /// </summary>
    public void StartSharing()
    {
        StopSharing();
        var sharing = Settings.Sharing;
        if (!sharing.Enabled)
        {
            Sharing.Show(null);
            return;
        }
        if (sharing.Code is null)
        {
            sharing.Code = SharingCode.New();
            Save();
        }

        SharingServer? server = null;
        UpsCoordinator? coordinator = null;
        try
        {
            var loopback = SharingServer.LoopbackFromEnvironment;
            server = new SharingServer(Hub, sharing.Code, sharing.Port, loopback, Log.Info);
            try
            {
                server.Start();
            }
            catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode is System.Net.Sockets.SocketError.AddressAlreadyInUse
                or System.Net.Sockets.SocketError.AccessDenied)
            {
                // Another program (or another copy of UPS Tools on this PC) has the port, or Windows keeps it for
                // itself: any other will do, as the announcements say which one it is.
                server.Dispose();
                server = new SharingServer(Hub, sharing.Code, 0, loopback, Log.Info);
                server.Start();
            }
            coordinator = new UpsCoordinator(Hub.Instance, Hub.Pc, sharing.Code, server.Port, new MulticastTransport(sharing.Port, loopback),
                Log.Info, CoordinatorTimings);
            coordinator.RouteChanged += OnRouteChanged;
            coordinator.NeighboursChanged += () => _ui.InvokeAsync(ShowNeighbours);
            coordinator.Start();
            (_server, _coordinator) = (server, coordinator);
            Log.Info($"Sharing with the other PCs on the network (port {sharing.Port}, readings on {server.Port})");
            Sharing.Show(server.Port);
        }
        catch (Exception ex)
        {
            coordinator?.Dispose();
            server?.Dispose();
            Log.Warn($"Could not share with the other PCs: {ex.Message}");
            Sharing.Show(null, ex.Message);
            return;
        }

        // The UPSes monitored already: one this PC is connected to stays so, unless another PC is too.
        foreach (var unit in Upses.Where(u => u.Monitor is not null))
            coordinator.Join(unit.Device, unit.Title,
                connected: unit.Connection?.LastReadDirect == true && unit.Monitor!.LinkState == UpsLinkState.Online);
        foreach (var unit in Upses) ApplyRoute(unit);
    }

    /// <summary>How fast the PCs agree; the defaults unless a test says otherwise.</summary>
    internal static UpsCoordinator.Timings CoordinatorTimings { get; set; } = UpsCoordinator.Timings.Default;

    /// <summary>The port other PCs reach this one on, while it shares its UPSes.</summary>
    internal int? SharingPort => _server?.Port;

    /// <summary>
    /// Stops sharing: says goodbye to the other PCs (they take over the UPSes this PC was connected to), and connects
    /// to every UPS over Bluetooth again.
    /// </summary>
    private void StopSharing()
    {
        var coordinator = _coordinator;
        _coordinator = null;
        coordinator?.Dispose();
        _server?.Dispose();
        _server = null;
        if (coordinator is null) return;
        foreach (var unit in Upses) ApplyRoute(unit);
        ShowNeighbours();
    }

    /// <summary>How the UPS is read now: over Bluetooth, unless sharing says through another PC.</summary>
    private UpsRoute RouteOf(PairedUps device) => _coordinator?.RouteOf(device) ?? UpsRoute.Direct;

    /// <summary>A UPS's route changed (on a background thread): its connection follows at once, the rest on the UI thread.</summary>
    private void OnRouteChanged(PairedUps device, UpsRoute route)
    {
        if (_connections.TryGetValue(SharingHub.KeyOf(device), out var connection)) connection.RouteChanged();
        _ui.InvokeAsync(() =>
        {
            if (Find(device) is { } unit) ApplyRoute(unit, connected: true);
        });
    }

    /// <summary>
    /// Shows how a UPS is read, and shares its readings with the other PCs while this PC is the one connected to it.
    /// </summary>
    /// <param name="connected">Its connection has been told already.</param>
    private void ApplyRoute(UpsUnit unit, bool connected = false)
    {
        var route = RouteOf(unit.Device);
        if (!connected) unit.Connection?.RouteChanged();
        if (route.Kind == UpsRouteKind.Direct && unit.Monitor is not null)
            Hub.Share(unit.Device, unit.Title, TimeSpan.FromSeconds(Settings.UpdatePeriodSeconds));
        else
            Hub.Unshare(unit.Device);
        unit.Configuration.Route = route.Kind switch
        {
            UpsRouteKind.Through => $"through {route.Peer!.Pc}",
            UpsRouteKind.Waiting => "waiting for the other PCs",
            _ => "via Bluetooth LE",
        };
        unit.Dashboard.SetThrough(unit.Device, route.Peer?.Pc, unit.Title);
    }

    private void ShowNeighbours() => Sharing.ShowNeighbours(_coordinator?.Neighbours ?? []);

    /// <summary>The UPSes other PCs are connected to, for the pairing dialog.</summary>
    public IReadOnlyList<NetworkUps> NetworkUpses => _coordinator?.NetworkUpses ?? [];

    /// <summary>The connection of each monitored UPS, by <see cref="SharingHub.KeyOf"/>.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CoordinatedUpsConnection> _connections = new();

    /// <summary>Starts monitoring every paired UPS that isn't monitored yet.</summary>
    public void StartMonitoring()
    {
        foreach (var unit in Upses) StartMonitoring(unit);
        UpdateTray();
    }

    private void StartMonitoring(UpsUnit unit)
    {
        if (unit.Monitor is not null) return;

        IUpsConnection bluetooth;
        var device = unit.Device;
        try
        {
            var driver = _drivers.Find(device.DriverId) ?? throw new NotSupportedException($"Unknown UPS driver '{device.DriverId}'");
            bluetooth = driver.CreateConnection(device);
        }
        catch (Exception ex)
        {
            Log.Warn($"Cannot monitor {unit.Device.Name}: {ex.Message}");
            unit.Dashboard.SetLink(LinkStatus.Unreachable);
            return;
        }

        // Over Bluetooth, or through the PC connected to it, as the PCs sharing on the network agree.
        var key = SharingHub.KeyOf(device);
        var connection = new CoordinatedUpsConnection(bluetooth, () => RouteOf(device),
            peer => new NetworkUpsConnection(peer.Address, peer.Port, Settings.Sharing.Code ?? "", key, peer.Pc));
        unit.Connection = connection;
        _connections[key] = connection;

        var monitor = new UpsMonitor(connection, TimeSpan.FromSeconds(Settings.UpdatePeriodSeconds)) { Rules = unit.Settings.Rules() };
        monitor.Log += message => Log.Info(unit.LogPrefix + message);

        // The readings of a UPS this PC is connected to go to the PCs that read it through this one.
        monitor.StatusUpdated += status =>
        {
            if (connection.LastReadDirect) Hub.Publish(device, status);
        };
        monitor.ReadFailed += error =>
        {
            if (connection.LastReadDirect) Hub.PublishFailure(device, error);
        };
        monitor.LinkStateChanged += state =>
        {
            if (connection.LastReadDirect) _coordinator?.ReportLink(device, state != UpsLinkState.Unreachable);
        };
        monitor.StatusUpdated += status => _ui.InvokeAsync(() =>
        {
            if (unit.Monitor != monitor) return;
            unit.Dashboard.SetDevice(unit.Device, status.Serial, unit.Title);
            unit.Dashboard.Apply(status);
            UpdateTray();
        });
        monitor.LinkStateChanged += state => _ui.InvokeAsync(() =>
        {
            if (unit.Monitor != monitor) return;
            unit.Dashboard.SetLink(state switch
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
            if (unit.Monitor == monitor) OnUpsEvent(unit, e.Kind, e.Status, e.GridGoneSince);
        });

        unit.Monitor = monitor;
        unit.Dashboard.SetLink(LinkStatus.Connecting);
        _coordinator?.Join(device, unit.Title);
        ApplyRoute(unit);
        monitor.Start();
    }

    /// <summary>Applies the poll period to every monitor.</summary>
    private void ApplyPeriod()
    {
        var period = TimeSpan.FromSeconds(Settings.UpdatePeriodSeconds);
        Hub.SetPeriod(period);
        foreach (var monitor in Upses.Select(u => u.Monitor).OfType<UpsMonitor>())
            if (monitor.Period != period) monitor.Period = period;
    }

    /// <summary>Applies a UPS's event limits to its monitor.</summary>
    private static void ApplyRules(UpsUnit unit)
    {
        if (unit.Monitor is { } monitor) monitor.Rules = unit.Settings.Rules();
    }

    /// <summary>Stops monitoring every UPS.</summary>
    public Task StopMonitoringAsync() => Task.WhenAll(Upses.ToList().Select(StopMonitoringAsync));

    private async Task StopMonitoringAsync(UpsUnit unit)
    {
        var monitor = unit.Monitor;
        unit.Monitor = null;
        unit.GridLostAt = null;
        if (monitor is null) return;
        _coordinator?.Leave(unit.Device);
        Hub.Unshare(unit.Device);
        if (unit.Connection is { } connection) _connections.TryRemove(new(SharingHub.KeyOf(unit.Device), connection));
        unit.Connection = null;
        await monitor.DisposeAsync();
    }

    /// <summary>
    /// Adds a UPS, or updates the one already paired at that address (e.g. with another user ID), and starts
    /// monitoring. Call with that UPS not monitored (<see cref="PairAsync"/>).
    /// </summary>
    /// <param name="label">For a UPS not paired yet: what to call it, e.g. what the PC connected to it calls it.</param>
    public UpsUnit Pair(PairedUps ups, string? label = null)
    {
        var unit = Find(ups);
        if (unit is null)
        {
            var settings = new UpsSettings { Device = ups, Label = string.IsNullOrWhiteSpace(label) || label == ups.Title ? null : label };
            Settings.Upses.Add(settings);
            unit = CreateUnit(settings);
            Upses.Add(unit);
            Log.Info($"Paired with {ups.Name} ({ups.Address})");
        }
        else
        {
            unit.Settings.Device = ups;
            unit.Configuration.RefreshDevice();
            Log.Info($"Paired with {ups.Name} ({ups.Address}) again");
        }
        Save();
        RefreshTitles();
        Selected = unit;
        StartMonitoring();
        return unit;
    }

    /// <summary>The UPS paired as <paramref name="device"/> (the same driver and address), if any.</summary>
    public UpsUnit? Find(PairedUps device) =>
        Upses.FirstOrDefault(u => u.Device.DriverId == device.DriverId && u.Device.Address == device.Address);

    /// <summary>
    /// <see cref="Pair"/>, after stopping the monitor of the same UPS if it is paired already; it keeps its scripts and
    /// settings.
    /// </summary>
    /// <param name="label">For a UPS not paired yet: what to call it, e.g. what the PC connected to it calls it.</param>
    public async Task<UpsUnit> PairAsync(PairedUps device, string? label = null)
    {
        if (Find(device) is { } existing) await StopMonitoringAsync(existing);
        return Pair(device, label);
    }

    /// <summary>
    /// Stops monitoring <paramref name="unit"/> and forgets it with its scripts; scripts of it set to run as
    /// administrator have their tasks deleted, which may show an administrator prompt.
    /// </summary>
    public async Task ForgetAsync(UpsUnit unit)
    {
        await StopMonitoringAsync(unit);
        foreach (var (_, cancel) in unit.Chains.Values) cancel.Cancel();
        unit.Chains.Clear();
        foreach (var action in unit.Settings.Scripts.Values.SelectMany(list => list).Where(a => a.RunElevated))
            await Elevated.UnregisterAsync(action); // a task left behind goes with the uninstaller

        Settings.Upses.Remove(unit.Settings);
        Upses.Remove(unit);
        Save();
        if (Selected == unit) Selected = Upses.FirstOrDefault();
        Log.Info($"Forgot {unit.Device.Name}");
        RefreshTitles();
    }

    /// <summary>"Run now" on an event: its enabled scripts, as if the event had fired, without the notification.</summary>
    public void RunScripts(EventScriptsViewModel group, string reason)
    {
        var unit = Upses.First(u => u.EventScripts.Contains(group));
        StartChain(unit, group.Kind, group.EnabledActions.ToList(), unit.LogPrefix + reason, Variables(unit, group.Kind, manual: true),
            finished: (c, _) => NotifyProblems(unit, c, "Manual run"));
    }

    /// <summary>"Run now" on one script.</summary>
    public void RunScript(ScriptAction action, string reason)
    {
        var unit = Upses.First(u => u.GroupOf(action) is not null);
        StartChain(unit, null, [action], unit.LogPrefix + reason, Variables(unit, unit.GroupOf(action)!.Kind, manual: true),
            finished: (c, _) => NotifyProblems(unit, c, "Manual run"));
    }

    /// <summary>What the scripts of <paramref name="kind"/> are told about the UPS (<see cref="ScriptEnvironment"/>).</summary>
    private static IReadOnlyDictionary<string, string?> Variables(UpsUnit unit, UpsEventKind? kind, bool manual, UpsStatus? status = null,
        TimeSpan? outage = null) =>
        ScriptEnvironment.For(kind, status ?? unit.Monitor?.LastStatus,
            reachable: unit.Monitor is null ? null : unit.Monitor.LinkState != UpsLinkState.Unreachable && kind != UpsEventKind.UpsUnreachable,
            outage ?? (unit.GridLostAt is { } lost ? (status?.Timestamp ?? DateTimeOffset.Now) - lost : null), manual, unit.Title);

    /// <summary>
    /// Starts <paramref name="actions"/> in order (see <see cref="ScriptChain"/>) and returns the chain as it
    /// stands once it is waiting for a script or done. For an event (<paramref name="kind"/>), the chain
    /// replaces one of the same event of the same UPS that is still running, and the opposite event can stop it.
    /// </summary>
    /// <param name="finished">Called when the chain is done; the flag says whether it had to wait for a script.</param>
    private ScriptChain StartChain(UpsUnit unit, UpsEventKind? kind, IReadOnlyList<ScriptAction> actions, string reason,
        IReadOnlyDictionary<string, string?> environment, Action<ScriptChain, bool> finished)
    {
        if (actions.Count == 0) Log.Info($"{reason}: no scripts to run");
        if (kind is { } k) StopChain(unit, k, $"{reason} started again");

        // A built-in action is waited for (its countdown, and sleep until the PC wakes up), and stopping the chain stops its countdown.
        var chain = new ScriptChain([.. actions.Select(a => new ScriptStep(a.Name, a.WaitForExit || a.Power is not null,
            UndoneByStopping: a.Power is not null,
            TimeLimit: a.Power is null && a.WaitForExit && a.WaitLimitMinutes > 0 ? a.WaitLimitMinutes * WaitLimitUnit : null))]);
        var cancel = new CancellationTokenSource();
        if (kind is { } key) unit.Chains[key] = (chain, cancel);

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
                if (kind is { } key && unit.Chains.TryGetValue(key, out var current) && current.Chain == chain) unit.Chains.Remove(key);
                cancel.Dispose();
            }

            if (chain.StoppedAt is { } stop && chain.Skipped.Count > 0)
                Log.Warn($"{reason}: {stop.Name} failed, so {ScriptReport.List(chain.Skipped)} didn't run");
            finished(chain, waited);
        }
    }

    /// <summary>Stops a running chain of <paramref name="kind"/> of <paramref name="unit"/>: it starts no more scripts.</summary>
    /// <returns>The scripts it won't run, or null when none of that event was running.</returns>
    private IReadOnlyList<string>? StopChain(UpsUnit unit, UpsEventKind kind, string why)
    {
        if (!unit.Chains.Remove(kind, out var running) || running.Chain.Done) return null;
        var skipped = running.Chain.Unstarted;
        running.Cancel.Cancel();
        Log.Info(skipped.Count > 0
            ? $"{why}: stopped the {kind.Title()} scripts; {ScriptReport.List(skipped)} won't run"
            : $"{why}: stopped waiting for the {kind.Title()} scripts");
        return skipped;
    }

    private void NotifyProblems(UpsUnit unit, ScriptChain chain, string label)
    {
        if (ScriptReport.Problems(chain, label) is { } problem)
            _notifier?.Show(Notifier.TagOf("scripts", unit.Source), TrayState.Problem, problem.Title, problem.Body,
                attribution: unit.Title, important: true);
    }

    public void SendTestNotification()
    {
        _notifier?.Show("test", TrayState.GridOnline, "Test notification",
            "This is how UPS Tools tells you that the grid went down or came back.", attribution: Selected?.Title);
        Log.Info("Sent a test notification");
    }

    /// <summary>What one of <see cref="ScriptAction.WaitLimitMinutes"/> is: a minute, unless a test says otherwise.</summary>
    internal static TimeSpan WaitLimitUnit { get; set; } = TimeSpan.FromMinutes(1);

    private IEnumerable<UpsMonitor> Monitors => Upses.Select(u => u.Monitor).OfType<UpsMonitor>();

    private string TheUpses => HasSeveral ? "the UPSes" : "the UPS";

    /// <summary>The PC is about to sleep or hibernate (Windows doesn't say which): close the links now.</summary>
    public void OnGoingToSleep()
    {
        CloseLinks("PC is going to sleep or hibernate", "before sleep");
        StopSharing(); // the other PCs take over the UPSes this PC was connected to
    }

    /// <summary>
    /// Windows is shutting down, restarting or signing out, and the app is about to end with it: close the links now
    /// rather than leave them to the Bluetooth stack, which can keep a UPS thinking it is still connected.
    /// </summary>
    public void OnSessionEnding(bool signingOut)
    {
        CloseLinks(signingOut ? "Signing out of Windows" : "PC is shutting down", "before shutting down");
        StopSharing(); // the other PCs take over the UPSes this PC was connected to
    }

    /// <summary>Closes every link, waiting a few seconds at most (Windows won't wait longer); the monitors stay paused.</summary>
    private void CloseLinks(string why, string when)
    {
        var monitors = Monitors.ToList();
        if (monitors.Count == 0) return;
        Log.Info($"{why}; disconnecting from {TheUpses}");
        try
        {
            // Off this thread, so the wait can't hold up the continuations it waits for.
            if (!Task.Run(() => Task.WhenAll(monitors.Select(m => m.NotifySuspendingAsync()))).Wait(TimeSpan.FromSeconds(3)))
                Log.Info($"The link to {TheUpses} did not close in time {when}");
        }
        catch (Exception ex)
        {
            Log.Info($"Closing the link to {TheUpses} {when} failed: {ex.GetBaseException().Message}");
        }
    }

    public void OnResumedFromSleep()
    {
        Log.Info($"PC resumed from sleep; reconnecting to {TheUpses} in {UpsMonitor.DefaultResumeSettle.TotalSeconds:0} s");
        foreach (var monitor in Monitors) monitor.NotifyResumed();
        if (Settings.Sharing.Enabled) StartSharing();
    }

    /// <param name="gridGoneSince">For "Grid lost": when the grid went, if earlier than the reading (a delay was waited out).</param>
    internal void OnUpsEvent(UpsUnit unit, UpsEventKind kind, UpsStatus? status, DateTimeOffset? gridGoneSince = null)
    {
        var reason = unit.LogPrefix + kind.Title();
        Log.Event(reason);

        TimeSpan? outage = null;
        var at = status?.Timestamp ?? DateTimeOffset.Now;
        if (kind == UpsEventKind.GridLost)
        {
            unit.GridLostAt = gridGoneSince ?? at;
        }
        else if (kind == UpsEventKind.GridRestored)
        {
            if (unit.GridLostAt is { } lost) outage = at - lost;
            unit.GridLostAt = null;
        }

        // The grid came back (or the UPS answers again): what was queued for the earlier state mustn't run now.
        var stopped = new List<(UpsEventKind, IReadOnlyList<string>)>();
        foreach (var other in kind.Stops())
            if (StopChain(unit, other, reason) is { } skipped) stopped.Add((other, skipped));

        var notify = unit.Settings.NotifyOn(kind);
        var chain = StartChain(unit, kind, unit.EventScripts.First(g => g.Kind == kind).EnabledActions.ToList(), reason,
            Variables(unit, kind, manual: false, status, outage),
            // Problems found before the notification below went out are in it already.
            finished: (c, waited) => { if (waited && notify) NotifyProblems(unit, c, kind.Title()); });

        // "UPS unreachable" comes without a reading; the last good one says when the UPS last answered.
        if (notify)
            _notifier?.Show(UpsNotice.For(kind, status ?? unit.Monitor?.LastStatus, outage, chain, stopped,
                unreachable: kind == UpsEventKind.BatteryLow && status is null), unit.Title, unit.Source);
    }

    private static int Severity(TrayState state) => state switch
    {
        TrayState.Problem => 3,
        TrayState.OnBattery => 2,
        TrayState.Neutral => 1,
        _ => 0,
    };

    /// <summary>
    /// The tray icon takes the colour of the UPS that needs attention most; with several, its tooltip has a line
    /// for each.
    /// </summary>
    private void UpdateTray()
    {
        if (_tray is null) return;
        switch (Upses.Count)
        {
            case 0:
                _tray.Update(TrayState.Neutral, "No UPS paired");
                return;
            case 1:
                _tray.Update(Upses[0].Dashboard.State, Upses[0].Dashboard.Summary);
                return;
            default:
                _tray.Update(Upses.Select(u => u.Dashboard.State).MaxBy(Severity),
                    string.Join("\n", Upses.Select(u => $"{u.Title}: {u.Dashboard.Summary}")));
                return;
        }
    }
}
