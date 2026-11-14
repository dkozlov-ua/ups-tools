using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UpsTools.Core;
using UpsTools.Core.EcoFlow;
using UpsTools.Core.Sharing;
using UpsTools.Services;

namespace UpsTools.Views;

/// <summary>Scans for supported UPS devices and verifies the chosen one with a real read before pairing.</summary>
public partial class PairWindow : Window
{
    private static readonly TimeSpan ScanDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromSeconds(20);
    private static readonly HttpClient Http = new();

    private readonly IReadOnlyList<IUpsDriver> _drivers;
    private readonly AppLog _log;
    private readonly Func<IReadOnlyList<NetworkUps>> _network;
    private readonly ObservableCollection<DiscoveredUps> _found = [];
    private CancellationTokenSource? _work;

    /// <param name="userId">The user ID the paired UPS uses, if any: the account panel starts with it.</param>
    /// <param name="network">The UPSes other PCs are connected to (<see cref="UpsCoordinator.NetworkUpses"/>).</param>
    public PairWindow(IReadOnlyList<IUpsDriver> drivers, AppLog log, string? userId = null, Func<IReadOnlyList<NetworkUps>>? network = null)
    {
        InitializeComponent();
        _drivers = drivers;
        _log = log;
        _network = network ?? (() => []);
        UserIdBox.Text = userId ?? "";
        DeviceList.ItemsSource = _found;
        Loaded += async (_, _) => await ScanAsync();
        Closed += (_, _) => _work?.Cancel();
    }

    public PairedUps? Result { get; private set; }

    /// <summary>For a UPS found on the network: what the PC connected to it calls it.</summary>
    public string? ResultLabel { get; private set; }

    /// <summary>Cancel whatever is running (scan or connect) and start a new unit of work.</summary>
    private CancellationTokenSource StartWork(TimeSpan? timeout = null)
    {
        _work?.Cancel();
        _work?.Dispose();
        _work = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        return _work;
    }

    private async Task ScanAsync()
    {
        var work = StartWork();
        _found.Clear();
        foreach (var shared in _network())
            AddOrUpdate(new DiscoveredUps(shared.Ups.Device.DriverId, shared.Ups.Device.Address, shared.Ups.Device.Name,
                shared.Ups.Device.Model, null, shared.Pc.Pc, shared.Ups.Title == shared.Ups.Device.Title ? null : shared.Ups.Title));
        SetState(Mode.Scanning, "Scanning for Bluetooth devices…");

        try
        {
            await Task.WhenAll(_drivers.Select(driver =>
                driver.DiscoverAsync(Found, ScanDuration, work.Token)));
            // Devices reported from another thread at the last moment are still queued: add them before counting.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);

            if (work.IsCancellationRequested) return;
            SetState(Mode.Idle, _found.Count switch
            {
                0 => "No supported UPS found. Check that it is on and nearby, then scan again.",
                1 => "Found 1 UPS. Select it and click Pair.",
                var n => $"Found {n} devices. Select yours and click Pair.",
            });
            if (_found.Count == 1) DeviceList.SelectedIndex = 0;
        }
        catch (Exception ex) when (!work.IsCancellationRequested)
        {
            SetState(Mode.Idle, $"Scan failed: {ex.Message}", error: true);
        }
    }

    private void Found(DiscoveredUps device)
    {
        if (Dispatcher.CheckAccess()) AddOrUpdate(device);
        else Dispatcher.InvokeAsync(() => AddOrUpdate(device));
    }

    private void AddOrUpdate(DiscoveredUps device)
    {
        var index = _found.ToList().FindIndex(d => d.DriverId == device.DriverId && d.Address == device.Address);
        if (index < 0) _found.Add(device);
        else if (_found[index].ConnectedTo is not null) return; // another PC is connected to it: it is read through that one
        else if (_found[index].Rssi != device.Rssi && DeviceList.SelectedIndex != index) _found[index] = device;
    }

    private async Task PairAsync()
    {
        if (DeviceList.SelectedItem is not DiscoveredUps selected) return;
        if (selected.ConnectedTo is { } pc)
        {
            // Read through that PC: nothing to check here, and connecting would take the UPS away from it. The user ID
            // matters only if this PC takes over one day.
            var id = UserIdBox.Text.Trim();
            Result = new PairedUps(selected.DriverId, selected.Address, selected.Name, selected.Model, id.Length > 0 ? id : null);
            ResultLabel = selected.Label;
            _log.Info($"Pairing {selected.Name}, which {pc} is connected to");
            DialogResult = true;
            return;
        }
        var driver = _drivers.First(d => d.Id == selected.DriverId);
        var userId = driver.RequiresUserId ? UserIdBox.Text.Trim() : null;
        if (userId == "")
        {
            SetState(Mode.Idle, "Enter the EcoFlow user ID, or sign in to look it up.", error: true);
            UserIdBox.Focus();
            return;
        }
        var candidate = new PairedUps(selected.DriverId, selected.Address, selected.Name, selected.Model, userId);

        var work = StartWork(ConnectTimeout); // also stops a running scan
        SetState(Mode.Connecting, $"Connecting to {selected.Name}…");

        try
        {
            UpsStatus status;
            // Release the link fully before closing: the monitor connects as soon as the dialog returns.
            await using (var connection = driver.CreateConnection(candidate))
            {
                status = await connection.ReadAsync(work.Token);
            }
            _log.Info($"Pairing check OK: {status.Model}, serial {status.Serial}, battery {status.BatteryPercent} %");
            Result = candidate;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? "the UPS did not answer in time" : ex.Message;
            SetState(Mode.Idle, $"Could not connect: {message}", error: true);
        }
    }

    private async Task SignInAsync()
    {
        if (EmailBox.Text.Trim() == "" || PasswordBox.Password == "")
        {
            SetState(Mode.Idle, "Enter the email address and password of your EcoFlow account.", error: true);
            return;
        }

        var work = StartWork(SignInTimeout); // also stops a running scan
        SetState(Mode.SigningIn, "Signing in to EcoFlow…");
        try
        {
            UserIdBox.Text = await EcoFlowAccount.GetUserIdAsync(Http, EmailBox.Text, PasswordBox.Password, work.Token);
            PasswordBox.Clear();
            _log.Info("Looked up the EcoFlow user ID");
            SetState(Mode.Idle, "Found your user ID. Click Pair to connect.");
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? "EcoFlow did not answer in time" : ex.Message;
            SetState(Mode.Idle, message, error: true);
        }
    }

    private enum Mode
    {
        Idle,
        Scanning,
        Connecting,
        SigningIn,
    }

    private Mode _mode;

    private void SetState(Mode mode, string status, bool error = false)
    {
        _mode = mode;
        Busy.Visibility = mode == Mode.Idle ? Visibility.Hidden : Visibility.Visible;
        StatusText.Text = status;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
        ScanButton.IsEnabled = mode == Mode.Idle;
        DeviceList.IsEnabled = mode is not (Mode.Connecting or Mode.SigningIn);
        AccountPanel.IsEnabled = mode is not (Mode.Connecting or Mode.SigningIn);
        UpdatePairButton();
    }

    // Pairing is allowed while a scan is still running; it stops the scan.
    private void UpdatePairButton()
    {
        PairButton.IsEnabled = DeviceList.SelectedItem is not null && _mode is not (Mode.Connecting or Mode.SigningIn);
        var selected = DeviceList.SelectedItem as DiscoveredUps;
        var driver = selected is null ? null : _drivers.FirstOrDefault(d => d.Id == selected.DriverId);
        AccountPanel.Visibility = driver?.RequiresUserId == true && selected?.ConnectedTo is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e) => await SignInAsync();

    // Enter after the password looks the user ID up rather than pairing.
    private async void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SignInAsync();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void Pair_Click(object sender, RoutedEventArgs e) => await PairAsync();

    private async void DeviceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PairButton.IsEnabled) await PairAsync();
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePairButton();
}
