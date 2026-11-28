using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using UpsTools.Core;
using UpsTools.Core.History;
using UpsTools.Core.Sharing;
using UpsTools.Services;
using UpsTools.ViewModels;

namespace UpsTools.Droid;

/// <summary>Runs code on the UI thread, in order.</summary>
public interface IUiThread
{
    void Post(Action action);
}

/// <summary>The notifications about UPS events.</summary>
public interface IEventNotifications
{
    /// <param name="ups">What the UPS is called.</param>
    /// <param name="source">Tells its notifications apart from another UPS's (<see cref="UpsUnit.Source"/>).</param>
    void Show(UpsNotice notice, string ups, string source);

    void ShowTest(string? ups);
}

/// <summary>
/// One paired UPS as the phone has it: its settings, its monitor and what the dashboard shows of it.
/// <see cref="MonitorController"/> drives it, on the UI thread.
/// </summary>
public sealed class UpsUnit
{
    /// <param name="history">What it did lately; kept in memory only unless given.</param>
    internal UpsUnit(UpsSettings settings, UpsHistory? history = null)
    {
        Settings = settings;
        Title = settings.Label ?? settings.Device.Title;
        History = history ?? new UpsHistory(null);
    }

    public UpsSettings Settings { get; }
    public PairedUps Device => Settings.Device;

    /// <summary>What it is called: the name the user gave it, or its brand and model (and its own name, to tell two apart).</summary>
    public string Title { get; internal set; }

    public DashboardViewModel Dashboard { get; } = new();

    /// <summary>A point a minute and its events, for its History screen (<see cref="UpsHistory"/>).</summary>
    public UpsHistory History { get; }

    /// <summary>How it is read: over Bluetooth, or through which device.</summary>
    public string Route { get; internal set; } = "via Bluetooth LE";

    /// <summary>Polls it; null while it isn't monitored.</summary>
    public UpsMonitor? Monitor { get; internal set; }

    internal CoordinatedUpsConnection? Connection { get; set; }

    /// <summary>Tells its notifications apart from another UPS's.</summary>
    public string Source => Settings.Id.Length > 8 ? Settings.Id[..8] : Settings.Id;

    /// <summary>Starts what the log says about it: "Title: " while several UPSes are paired, otherwise nothing.</summary>
    internal string LogPrefix { get; set; } = "";

    /// <summary>When its grid went, during an outage.</summary>
    internal DateTimeOffset? GridLostAt { get; set; }

    public override string ToString() => Title;
}

/// <summary>
/// Owns the settings and the paired UPSes, monitors them, shares them with the other devices on the network, and
/// raises the notifications of their events: the Windows app's AppController, without the scripts. Everything here
/// runs on the UI thread; the monitors' events are posted to it.
/// </summary>
public sealed class MonitorController
{
    private readonly IUiThread _ui;
    private readonly SettingsStore _store;
    private readonly UpsDrivers _drivers;
    private readonly Func<IReadOnlyList<IPAddress>>? _networks;
    private readonly ConcurrentDictionary<string, CoordinatedUpsConnection> _connections = new();
    private SharingServer? _server;
    private volatile UpsCoordinator? _coordinator;
    private CancellationTokenSource? _watchdog;
    private readonly WebhookSender _webhooks;

    /// <param name="ui">The UI thread: everything here runs on it.</param>
    /// <param name="log">The log; its folder holds the settings too.</param>
    /// <param name="drivers">The UPS drivers.</param>
    /// <param name="deviceName">What the other devices on the network call this one.</param>
    /// <param name="networks">The IPv4 addresses of the networks to share on; null: every one that carries multicast.</param>
    /// <param name="webhooks">Calls the webhooks; over the network unless a test says otherwise.</param>
    public MonitorController(IUiThread ui, AppLog log, IReadOnlyList<IUpsDriver> drivers, string deviceName,
        Func<IReadOnlyList<IPAddress>>? networks = null, IEventNotifications? notifications = null, WebhookSender? webhooks = null)
    {
        _webhooks = webhooks ?? new WebhookSender();
        _ui = ui;
        Log = log;
        _store = new SettingsStore(log, log.Folder);
        Settings = _store.Load();
        _drivers = new UpsDrivers(drivers);
        _networks = networks;
        DeviceName = deviceName;
        Hub = new SharingHub { Pc = deviceName };
        Notifications = notifications;

        foreach (var entry in Log.Recent) Activity.Post(entry);
        Log.EntryAdded += entry => _ui.Post(() =>
        {
            Activity.Post(entry);
            ActivityAdded?.Invoke(entry);
        });

        foreach (var ups in Settings.Upses) Upses.Add(NewUnit(ups));
        RefreshTitles();
    }

    private UpsUnit NewUnit(UpsSettings settings) =>
        new(settings, new UpsHistory(Path.Combine(Log.Folder, "history", settings.Id + ".txt")));

    public AppLog Log { get; }
    public AppSettings Settings { get; }
    public IReadOnlyList<IUpsDriver> Drivers => _drivers.All;
    public IEventNotifications? Notifications { get; set; }

    /// <summary>Tells the apps on the phone of each event (Tasker…); none unless set.</summary>
    public IEventBroadcast? Broadcast { get; set; }

    /// <summary>What the other devices on the network call this one.</summary>
    public string DeviceName { get; }

    /// <summary>What this device shares: the UPSes it is connected to itself, with their latest readings.</summary>
    public SharingHub Hub { get; }

    /// <summary>The paired UPSes, in the order they were paired.</summary>
    public List<UpsUnit> Upses { get; } = [];

    public bool HasSeveral => Upses.Count > 1;

    /// <summary>What the log says, about every UPS, newest first.</summary>
    public ActivityFeed Activity { get; } = new();

    /// <summary>The monitors run (between <see cref="Start"/> and <see cref="StopAsync"/>).</summary>
    public bool IsRunning { get; private set; }

    /// <summary>A UPS's readings, link, name or route changed, or one was paired or forgotten.</summary>
    public event Action? Changed;

    /// <summary>A line was logged (after <see cref="Activity"/> has it).</summary>
    public event Action<LogEntry>? ActivityAdded;

    /// <summary>Sharing started, stopped, failed, or the devices around changed.</summary>
    public event Action? SharingChanged;

    /// <summary>The EcoFlow user ID a UPS was paired with, to start the next pairing with.</summary>
    public string? KnownUserId => Upses.Select(u => u.Device.UserId).LastOrDefault(id => id is not null);

    public void Save() => _store.Save(Settings);

    public UpsUnit? Find(string id) => Upses.FirstOrDefault(u => u.Settings.Id == id);

    /// <summary>The UPS paired as <paramref name="device"/> (the same driver and address), if any.</summary>
    public UpsUnit? Find(PairedUps device) =>
        Upses.FirstOrDefault(u => u.Device.DriverId == device.DriverId && u.Device.Address == device.Address);

    /// <summary>Starts monitoring every paired UPS, and sharing if it is on.</summary>
    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        StartSharing();
        foreach (var unit in Upses) StartMonitoring(unit);
        StartWatchdog();
        Changed?.Invoke();
    }

    /// <summary>Stops sharing (the others take over) and monitoring.</summary>
    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _watchdog?.Cancel();
        _watchdog = null;
        StopSharing();
        await Task.WhenAll(Upses.ToList().Select(StopMonitoringAsync));
        Changed?.Invoke();
    }

    // ---- names ----

    /// <summary>
    /// Names each UPS: two of the same model without a name of their own are told apart by what they call
    /// themselves. With several, the log says which one each line is about.
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
    }

    /// <summary>Gives the UPS a name of its own, or (empty) its brand and model again.</summary>
    public void Rename(UpsUnit unit, string? label)
    {
        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (unit.Settings.Label == label) return;
        unit.Settings.Label = label;
        Save();
        RefreshTitles();
        Changed?.Invoke();
    }

    /// <summary>A UPS's event limits changed: its monitor follows.</summary>
    public void ApplyRules(UpsUnit unit)
    {
        Save();
        if (unit.Monitor is { } monitor) monitor.Rules = unit.Settings.Rules();
    }

    /// <summary>The poll period changed: every monitor follows.</summary>
    public void ApplyPeriod()
    {
        Save();
        var period = TimeSpan.FromSeconds(Settings.UpdatePeriodSeconds);
        Hub.SetPeriod(period);
        foreach (var monitor in Upses.Select(u => u.Monitor).OfType<UpsMonitor>())
            if (monitor.Period != period) monitor.Period = period;
    }

    // ---- pairing ----

    /// <summary>
    /// Adds a UPS, or updates the one already paired at that address (e.g. with another user ID, keeping its
    /// settings), and monitors it while monitoring runs.
    /// </summary>
    /// <param name="label">For a UPS not paired yet: what to call it, e.g. what the device connected to it calls it.</param>
    public async Task<UpsUnit> PairAsync(PairedUps device, string? label = null)
    {
        var unit = Find(device);
        if (unit is not null) await StopMonitoringAsync(unit);
        if (unit is null)
        {
            var settings = new UpsSettings { Device = device, Label = string.IsNullOrWhiteSpace(label) || label == device.Title ? null : label };
            Settings.Upses.Add(settings);
            unit = NewUnit(settings);
            Upses.Add(unit);
            Log.Info($"Paired with {device.Name} ({device.Address})");
        }
        else
        {
            unit.Settings.Device = device;
            Log.Info($"Paired with {device.Name} ({device.Address}) again");
        }
        Save();
        RefreshTitles();
        if (IsRunning) StartMonitoring(unit);
        Changed?.Invoke();
        return unit;
    }

    /// <summary>
    /// Stops monitoring a paired UPS for a while, e.g. so that pairing it again can connect to it (it accepts one
    /// connection); <see cref="PairAsync"/> or <see cref="Resume"/> monitor it again.
    /// </summary>
    public Task PauseAsync(UpsUnit unit) => StopMonitoringAsync(unit);

    /// <summary>Monitors a UPS <see cref="PauseAsync"/> paused, while monitoring runs.</summary>
    public void Resume(UpsUnit unit)
    {
        if (!IsRunning || !Upses.Contains(unit)) return;
        StartMonitoring(unit);
        Changed?.Invoke();
    }

    /// <summary>Stops monitoring the UPS and forgets it.</summary>
    public async Task ForgetAsync(UpsUnit unit)
    {
        await StopMonitoringAsync(unit);
        Settings.Upses.Remove(unit.Settings);
        Upses.Remove(unit);
        unit.History.Delete();
        Save();
        Log.Info($"Forgot {unit.Device.Name}");
        RefreshTitles();
        Changed?.Invoke();
    }

    // ---- monitoring ----

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

        // Over Bluetooth, or through the device connected to it, as the devices sharing on the network agree.
        var key = SharingHub.KeyOf(device);
        var connection = new CoordinatedUpsConnection(bluetooth, () => RouteOf(device),
            peer => new NetworkUpsConnection(peer.Address, peer.Port, Settings.Sharing.Code ?? "", key, peer.Pc) { From = DeviceName });
        unit.Connection = connection;
        _connections[key] = connection;

        var monitor = new UpsMonitor(connection, TimeSpan.FromSeconds(Settings.UpdatePeriodSeconds)) { Rules = unit.Settings.Rules() };
        monitor.Log += message => Log.Info(unit.LogPrefix + message);

        // The readings of a UPS this device is connected to go to the devices that read it through this one.
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
        monitor.StatusUpdated += status => _ui.Post(() =>
        {
            if (unit.Monitor != monitor) return;
            unit.History.Record(status);
            unit.Dashboard.SetDevice(unit.Device, status.Serial, unit.Title);
            unit.Dashboard.Apply(status);
            Changed?.Invoke();
        });
        monitor.LinkStateChanged += state => _ui.Post(() =>
        {
            if (unit.Monitor != monitor) return;
            unit.Dashboard.SetLink(state switch
            {
                UpsLinkState.Online => LinkStatus.Online,
                UpsLinkState.Retrying => LinkStatus.Retrying,
                UpsLinkState.Unreachable => LinkStatus.Unreachable,
                _ => LinkStatus.Connecting,
            });
            Changed?.Invoke();
        });
        monitor.EventRaised += e => _ui.Post(() =>
        {
            if (unit.Monitor == monitor) OnUpsEvent(unit, e.Kind, e.Status, e.GridGoneSince);
        });

        unit.Monitor = monitor;
        unit.Dashboard.SetLink(LinkStatus.Connecting);
        _coordinator?.Join(device, unit.Title);
        ApplyRoute(unit);
        monitor.Start();
    }

    private async Task StopMonitoringAsync(UpsUnit unit)
    {
        var monitor = unit.Monitor;
        unit.Monitor = null;
        unit.GridLostAt = null;
        unit.History.Flush();
        unit.Dashboard.SetBatteryLow(false);
        if (monitor is null) return;
        _coordinator?.Leave(unit.Device);
        Hub.Unshare(unit.Device);
        if (unit.Connection is { } connection) _connections.TryRemove(new(SharingHub.KeyOf(unit.Device), connection));
        unit.Connection = null;
        await monitor.DisposeAsync();
    }

    /// <param name="gridGoneSince">For "Grid lost": when the grid went, if earlier than the reading (a delay was waited out).</param>
    internal void OnUpsEvent(UpsUnit unit, UpsEventKind kind, UpsStatus? status, DateTimeOffset? gridGoneSince = null)
    {
        Log.Event(unit.LogPrefix + kind.Title());
        unit.History.Record(kind, kind == UpsEventKind.GridLost ? gridGoneSince ?? status?.Timestamp : status?.Timestamp);

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

        // Its battery stays low until the grid is back: the status notification is red till then.
        if (kind is UpsEventKind.BatteryLow or UpsEventKind.GridRestored)
        {
            unit.Dashboard.SetBatteryLow(kind == UpsEventKind.BatteryLow);
            Changed?.Invoke();
        }

        // "UPS unreachable" comes without a reading; the last good one says when the UPS last answered.
        var notice = UpsNotice.For(kind, status ?? unit.Monitor?.LastStatus, outage,
            unreachable: kind == UpsEventKind.BatteryLow && status is null);
        if (unit.Settings.NotifyOn(kind)) Notifications?.Show(notice, unit.Title, unit.Source);

        var message = Message(unit, kind, notice, status ?? unit.Monitor?.LastStatus, outage ?? (unit.GridLostAt is { } since ? at - since : null));
        try { Broadcast?.Send(message); }
        catch (Exception ex) { Log.Warn($"{unit.LogPrefix}Could not tell the other apps of {kind.Title()}: {ex.Message}"); }
        foreach (var hook in unit.Settings.Webhooks.Where(h => h.Enabled && h.Events.Contains(kind) && WebhookSettings.IsValidUrl(h.Url)))
            _ = CallAsync(unit, hook, message);
    }

    /// <summary>The event as other apps and the webhooks are told of it.</summary>
    private EventMessage Message(UpsUnit unit, UpsEventKind kind, UpsNotice notice, UpsStatus? status, TimeSpan? outage, bool test = false) =>
        new(kind, notice, ScriptEnvironment.For(kind, status,
            reachable: unit.Monitor is null ? null : unit.Monitor.LinkState != UpsLinkState.Unreachable && kind != UpsEventKind.UpsUnreachable,
            outage, manual: test, unit.Title), DeviceName, test);

    private async Task CallAsync(UpsUnit unit, WebhookSettings hook, EventMessage message)
    {
        var failure = await _webhooks.SendAsync(hook, message);
        var what = $"{unit.LogPrefix}Webhook {WebhookSender.HostOf(hook)} ({message.Kind.Title()})";
        if (failure is null) Log.Info($"{what}: sent");
        else Log.Warn($"{what} failed: {failure}");
    }

    /// <summary>
    /// Send test: the webhook gets a "Grid lost" (marked as a test) with the UPS's latest reading.
    /// </summary>
    /// <returns>Null when it took it, or why not.</returns>
    public async Task<string?> TestWebhookAsync(UpsUnit unit, WebhookSettings hook)
    {
        var status = unit.Monitor?.LastStatus;
        var message = Message(unit, UpsEventKind.GridLost, UpsNotice.For(UpsEventKind.GridLost, status), status, null, test: true);
        var failure = await _webhooks.SendAsync(hook, message);
        Log.Info($"{unit.LogPrefix}Webhook {WebhookSender.HostOf(hook)} (test): {failure ?? "sent"}");
        return failure;
    }

    public void SendTestNotification()
    {
        Notifications?.ShowTest(Upses.FirstOrDefault()?.Title);
        Log.Info("Sent a test notification");
    }

    // ---- the status notification ----

    private static int Severity(TrayState state) => state switch
    {
        TrayState.Problem => 3,
        TrayState.OnBattery => 2,
        TrayState.Neutral => 1,
        _ => 0,
    };

    /// <summary>The colour <paramref name="unit"/> gives the status notification: its state, unless its settings keep that off.</summary>
    public static TrayState StateOf(UpsUnit unit) => unit.Dashboard.TrayStateWith(
        onBattery: unit.Settings.StatusOn(UpsEventKind.GridLost), batteryLow: unit.Settings.StatusOn(UpsEventKind.BatteryLow),
        unreachable: unit.Settings.StatusOn(UpsEventKind.UpsUnreachable));

    /// <summary>
    /// What the status notification says: the colour of the UPS that needs attention most, a headline, and a line
    /// for each UPS shown in it (with its name when several are paired).
    /// </summary>
    public (TrayState State, string Title, IReadOnlyList<string> Lines) Status
    {
        get
        {
            if (Upses.Count == 0) return (TrayState.Neutral, "No UPS paired", []);
            var state = Upses.Select(StateOf).MaxBy(Severity);
            var shown = Upses.Where(u => u.Settings.ShowInStatus).ToList();
            IReadOnlyList<string> lines = HasSeveral
                ? [.. shown.Select(u => $"{u.Title}: {u.Dashboard.Summary}")]
                : [.. shown.Select(u => u.Dashboard.Summary)];
            var title = state switch
            {
                TrayState.Problem => "Needs attention",
                TrayState.OnBattery => "On battery",
                TrayState.GridOnline => HasSeveral ? "All on the grid" : "Grid online",
                _ => "Watching " + (HasSeveral ? "the UPSes" : "the UPS"),
            };
            return (state, title, lines);
        }
    }

    // ---- sharing ----

    /// <summary>Whether sharing works, or why not; empty while it is off.</summary>
    public string SharingStatus { get; private set; } = "";

    public bool SharingProblem { get; private set; }

    /// <summary>The port the others reach this device on, while it shares.</summary>
    public int? SharingPort => _server?.Port;

    /// <summary>The other devices heard on the network, one line each.</summary>
    public IReadOnlyList<string> Neighbours { get; private set; } = [];

    /// <summary>The UPSes other devices are connected to, for pairing.</summary>
    public IReadOnlyList<NetworkUps> NetworkUpses => _coordinator?.NetworkUpses ?? [];

    /// <summary>Turns sharing on or off.</summary>
    public void SetSharing(bool enabled)
    {
        if (Settings.Sharing.Enabled == enabled) return;
        Settings.Sharing.Enabled = enabled;
        Save();
        if (IsRunning) StartSharing();
        else ShowSharing(null);
    }

    /// <summary>Takes another device's code; false (and nothing changes) when it isn't a code.</summary>
    public bool SetSharingCode(string text)
    {
        var code = SharingCode.Format(text);
        if (code is null) return false;
        if (code == Settings.Sharing.Code) return true;
        Settings.Sharing.Code = code;
        Save();
        if (IsRunning) StartSharing();
        SharingChanged?.Invoke();
        return true;
    }

    /// <summary>A new code: the other devices have to be given it to keep sharing with this one.</summary>
    public void NewSharingCode()
    {
        Settings.Sharing.Code = SharingCode.New();
        Save();
        if (IsRunning) StartSharing();
        SharingChanged?.Invoke();
    }

    /// <summary>
    /// Starts sharing with the other devices on the network, or stops, as the settings say (again, e.g. with a new
    /// code). While sharing, they agree which one connects to each UPS over Bluetooth; the others read it through that one.
    /// </summary>
    private void StartSharing()
    {
        StopSharing();
        var sharing = Settings.Sharing;
        if (!sharing.Enabled || !IsRunning)
        {
            ShowSharing(null);
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
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                // Another app has the port: any other will do, as the announcements say which one it is.
                server.Dispose();
                server = new SharingServer(Hub, sharing.Code, 0, loopback, Log.Info);
                server.Start();
            }
            coordinator = new UpsCoordinator(Hub.Instance, Hub.Pc, sharing.Code, server.Port,
                new MulticastTransport(sharing.Port, loopback, _networks), Log.Info, CoordinatorTimings);
            coordinator.RouteChanged += OnRouteChanged;
            coordinator.NeighboursChanged += () => _ui.Post(ShowNeighbours);
            coordinator.Start();
            (_server, _coordinator) = (server, coordinator);
            Log.Info($"Sharing with the other devices on the network (port {sharing.Port}, readings on {server.Port})");
            ShowSharing(server.Port);
        }
        catch (Exception ex)
        {
            coordinator?.Dispose();
            server?.Dispose();
            Log.Warn($"Could not share with the other devices: {ex.Message}");
            ShowSharing(null, ex.Message);
            return;
        }

        // The UPSes monitored already: one this device is connected to stays so, unless another one is too.
        foreach (var unit in Upses.Where(u => u.Monitor is not null))
            coordinator.Join(unit.Device, unit.Title,
                connected: unit.Connection?.LastReadDirect == true && unit.Monitor!.LinkState == UpsLinkState.Online);
        foreach (var unit in Upses) ApplyRoute(unit);
    }

    /// <summary>How fast the devices agree; the defaults unless a test says otherwise.</summary>
    internal static UpsCoordinator.Timings CoordinatorTimings { get; set; } = UpsCoordinator.Timings.Default;

    /// <summary>
    /// Stops sharing: says goodbye to the others (they take over the UPSes this device was connected to), and connects
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

    private void ShowSharing(int? port, string? problem = null)
    {
        SharingProblem = problem is not null;
        SharingStatus = problem is not null ? $"Not sharing: {problem}" : port is null ? "" : SharingText();
        SharingChanged?.Invoke();
    }

    private void ShowNeighbours()
    {
        Neighbours = [.. (_coordinator?.Neighbours ?? []).Select(pc =>
            !pc.SameCode ? $"{pc.Pc}: shares with another code"
            : pc.Holding.Count == 0 ? $"{pc.Pc}: not connected to a UPS"
            : $"{pc.Pc}: connected to {string.Join(", ", pc.Holding)}")];
        if (!SharingProblem && _server is not null) SharingStatus = SharingText();
        SharingChanged?.Invoke();
    }

    private string SharingText() => Neighbours.Count == 0
        ? "Sharing. No other device found yet: they have to be on the same Wi-Fi network, with the same code."
        : "Sharing.";

    /// <summary>How the UPS is read now: over Bluetooth, unless sharing says through another device.</summary>
    private UpsRoute RouteOf(PairedUps device) => _coordinator?.RouteOf(device) ?? UpsRoute.Direct;

    /// <summary>A UPS's route changed (on a background thread): its connection follows at once, the rest on the UI thread.</summary>
    private void OnRouteChanged(PairedUps device, UpsRoute route)
    {
        if (_connections.TryGetValue(SharingHub.KeyOf(device), out var connection)) connection.RouteChanged();
        _ui.Post(() =>
        {
            if (Find(device) is { } unit) ApplyRoute(unit, connected: true);
        });
    }

    /// <summary>Shows how a UPS is read, and shares its readings with the others while this device is the one connected to it.</summary>
    /// <param name="connected">Its connection has been told already.</param>
    private void ApplyRoute(UpsUnit unit, bool connected = false)
    {
        var route = RouteOf(unit.Device);
        if (!connected) unit.Connection?.RouteChanged();
        if (route.Kind == UpsRouteKind.Direct && unit.Monitor is not null)
            Hub.Share(unit.Device, unit.Title, TimeSpan.FromSeconds(Settings.UpdatePeriodSeconds));
        else
            Hub.Unshare(unit.Device);
        unit.Route = route.Kind switch
        {
            UpsRouteKind.Through => $"through {route.Peer!.Pc}",
            UpsRouteKind.Waiting => "waiting for the other devices",
            _ => "via Bluetooth LE",
        };
        unit.Dashboard.SetThrough(unit.Device, route.Peer?.Pc, unit.Title);
        Changed?.Invoke();
    }

    // ---- being frozen ----

    /// <summary>A gap this long between two clock checks means Android froze the app, not that it was just slow.</summary>
    internal static readonly TimeSpan FrozenAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Android can freeze the app (deep sleep without <see cref="AppSettings.KeepAwake"/>, or a phone maker's battery
    /// saver) without saying so: the wall clock, which keeps running meanwhile, tells.
    /// </summary>
    private void StartWatchdog()
    {
        var stop = _watchdog = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            var last = DateTimeOffset.UtcNow;
            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(10), stop.Token); }
                catch (OperationCanceledException) { return; }
                var now = DateTimeOffset.UtcNow;
                var gap = now - last;
                last = now;
                if (gap >= FrozenAfter) _ui.Post(() => OnThawed(gap));
            }
        });
    }

    /// <summary>
    /// The app was frozen for a while: the links it had may be dead, and the other devices sharing the UPSes have
    /// stopped counting on this one, so start afresh.
    /// </summary>
    internal void OnThawed(TimeSpan frozen)
    {
        if (!IsRunning) return;
        var minutes = Math.Max(1, (int)Math.Round(frozen.TotalMinutes));
        var them = HasSeveral ? "the UPSes" : "the UPS";
        Log.Info($"Android paused UPS Tools for {minutes} min; reconnecting to {them} in {UpsMonitor.DefaultResumeSettle.TotalSeconds:0} s");
        foreach (var monitor in Upses.Select(u => u.Monitor).OfType<UpsMonitor>()) monitor.NotifyResumed();
        if (Settings.Sharing.Enabled) StartSharing();
    }
}
