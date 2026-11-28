using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using UpsTools.Core;
using UpsTools.Core.EcoFlow;
using View = Android.Views.View;

namespace UpsTools.Droid.Platform;

/// <summary>
/// Add UPS: scans for supported UPSes (and lists the ones other devices on the network share), checks the chosen one
/// with a real read, and pairs it. An EcoFlow UPS needs its account's user ID: typed in, or looked up by signing in.
/// </summary>
[Activity(Label = "Add UPS", Theme = "@style/AppTheme", Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout)]
public sealed class PairActivity : ScreenActivity
{
    private static readonly TimeSpan ScanDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromSeconds(20);
    private static readonly HttpClient Http = new();

    private readonly List<DiscoveredUps> _found = [];
    private DiscoveredUps? _selected;
    private CancellationTokenSource? _work;
    private Mode _mode;
    private string _status = "";
    private bool _error;
    private string _userId = "";
    private string _email = "";
    private EditText? _passwordField;

    private enum Mode
    {
        Idle,
        Scanning,
        Connecting,
        SigningIn,
    }

    protected override string ScreenTitle => "Add UPS";

    protected override bool RebuildOnResume => false;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _userId = Controller.KnownUserId ?? "";
        _ = ScanAsync();
    }

    protected override void OnDestroy()
    {
        _work?.Cancel();
        base.OnDestroy();
    }

    private CancellationTokenSource StartWork(TimeSpan? timeout = null)
    {
        _work?.Cancel();
        _work = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        return _work;
    }

    private void SetState(Mode mode, string status, bool error = false)
    {
        (_mode, _status, _error) = (mode, status, error);
        Refresh();
    }

    // Built once (OnResume); Refresh changes them in place, so that typing in the account fields is never interrupted.
    private LinearLayout? _statusRow;
    private LinearLayout? _list;
    private View? _account;
    private View? _userIdBox, _emailBox, _passwordBox, _lookUpButton;
    private Button? _scanButton, _pairButton;

    protected override void Build(LinearLayout content)
    {
        _statusRow = Ui.Row(this, 10);
        content.AddView(_statusRow, Ui.Match());

        _list = Ui.Card(this, 4);
        _list.SetPadding(Ui.Dp(this, 6), Ui.Dp(this, 6), Ui.Dp(this, 6), Ui.Dp(this, 6));
        content.AddView(_list, Ui.Match());

        _account = AccountPanel();
        content.AddView(_account, Ui.Match());

        var buttons = Ui.Row(this);
        _scanButton = Ui.Button(this, "Scan again", () => _ = ScanAsync());
        buttons.AddView(_scanButton);
        _pairButton = Ui.Button(this, "Pair", () => _ = PairAsync(), filled: true);
        buttons.AddView(_pairButton);
        content.AddView(buttons);

        content.AddView(Ui.Text(this,
            "A UPS accepts one Bluetooth connection at a time: close its own app (or turn off its Bluetooth there) first. " +
            "UPSes that a PC or phone with Network sharing on is connected to are listed too, and are read through that one." +
            (OperatingSystem.IsAndroidVersionAtLeast(31) ? "" : " On this Android version, Location has to be on to find Bluetooth devices."),
            13, secondary: true));
        Refresh();
    }

    private void Refresh()
    {
        if (_statusRow is null || _list is null || _account is null || _scanButton is null || _pairButton is null) return;
        var busy = _mode is Mode.Connecting or Mode.SigningIn;

        _statusRow.RemoveAllViews();
        if (_mode != Mode.Idle) _statusRow.AddView(new ProgressBar(this) { Indeterminate = true },
            new LinearLayout.LayoutParams(Ui.Dp(this, 24), Ui.Dp(this, 24)));
        _statusRow.AddView(Ui.Text(this, _status, 14, color: _error ? Ui.ToneColor(this, ViewModels.Tone.Bad) : Ui.Secondary(this)), Ui.Weight());

        _list.RemoveAllViews();
        foreach (var device in _found) _list.AddView(DeviceRow(device), Ui.Match());
        _list.Visibility = _found.Count > 0 ? ViewStates.Visible : ViewStates.Gone;

        var driver = _selected is null ? null : Controller.Drivers.FirstOrDefault(d => d.Id == _selected.DriverId);
        _account.Visibility = driver?.RequiresUserId == true && _selected?.ConnectedTo is null ? ViewStates.Visible : ViewStates.Gone;
        foreach (var view in new[] { _userIdBox, _emailBox, _passwordBox, _lookUpButton }) if (view is not null) view.Enabled = !busy;

        _scanButton.Enabled = _mode == Mode.Idle;
        _pairButton.Enabled = _selected is not null && !busy;
        _pairButton.Alpha = _pairButton.Enabled ? 1 : 0.5f;
    }

    private View DeviceRow(DiscoveredUps device)
    {
        var row = Ui.Column(this, 2);
        var pad = Ui.Dp(this, 10);
        row.SetPadding(pad, pad, pad, pad);
        var selected = _selected is { } s && s.DriverId == device.DriverId && s.Address == device.Address;
        var background = new Android.Graphics.Drawables.GradientDrawable();
        background.SetCornerRadius(Ui.Dp(this, 10));
        background.SetColor(selected ? Android.Graphics.Color.Argb(48, Ui.Palette.Accent.R, Ui.Palette.Accent.G, Ui.Palette.Accent.B)
            : Android.Graphics.Color.Transparent);
        row.Background = background;
        var top = Ui.Row(this);
        top.AddView(Ui.Text(this, device.Label ?? device.Title, 16, bold: true), Ui.Weight());
        top.AddView(Ui.Text(this, device.Where, 13, secondary: true));
        row.AddView(top, Ui.Match());
        row.AddView(Ui.Text(this, $"{device.Name} · {device.Address}", 12, secondary: true));
        row.Clickable = _mode is not (Mode.Connecting or Mode.SigningIn);
        row.Click += (_, _) =>
        {
            _selected = device;
            Refresh();
        };
        return row;
    }

    private View AccountPanel()
    {
        var card = Ui.Card(this, 6);
        card.AddView(Ui.Text(this, "EcoFlow account", 16, bold: true));
        card.AddView(Ui.Text(this,
            "An EcoFlow UPS only talks to the EcoFlow account it was added to in the EcoFlow app. Enter that account's user ID, " +
            "or sign in to look it up: only the user ID is kept, and the password goes to EcoFlow (api.ecoflow.com) and nowhere else.",
            13, secondary: true));

        var userId = Ui.Field(this, _userId, "User ID", InputTypes.ClassNumber);
        userId.TextChanged += (_, _) => _userId = userId.Text ?? "";
        _userIdField = userId;
        card.AddView(userId, Ui.Match());

        var email = Ui.Field(this, _email, "Email", InputTypes.ClassText | InputTypes.TextVariationEmailAddress);
        email.TextChanged += (_, _) => _email = email.Text ?? "";
        card.AddView(email, Ui.Match());
        var password = Ui.Field(this, "", "Password", InputTypes.ClassText | InputTypes.TextVariationPassword);
        password.ImeOptions = Android.Views.InputMethods.ImeAction.Go;
        password.EditorAction += (_, e) =>
        {
            e.Handled = true;
            _ = SignInAsync();
        };
        _passwordField = password;
        card.AddView(password, Ui.Match());
        var lookUp = Ui.Button(this, "Look up user ID", () => _ = SignInAsync());
        card.AddView(lookUp);
        (_userIdBox, _emailBox, _passwordBox, _lookUpButton) = (userId, email, password, lookUp);
        return card;
    }

    private EditText? _userIdField;

    private async Task ScanAsync()
    {
        var work = StartWork();
        _found.Clear();
        _selected = null;
        foreach (var shared in Controller.NetworkUpses)
            AddOrUpdate(new DiscoveredUps(shared.Ups.Device.DriverId, shared.Ups.Device.Address, shared.Ups.Device.Name,
                shared.Ups.Device.Model, null, shared.Pc.Pc, shared.Ups.Title == shared.Ups.Device.Title ? null : shared.Ups.Title));

        if (!Permissions.HasBluetooth(this))
        {
            SetState(Mode.Idle, _found.Count > 0
                ? "Not scanning for Bluetooth devices: UPS Tools isn't allowed to. The UPSes shared on the network are listed."
                : "UPS Tools isn't allowed to use Bluetooth: allow it on the main screen.", error: _found.Count == 0);
            return;
        }

        SetState(Mode.Scanning, "Scanning for Bluetooth devices…");
        try
        {
            await Task.WhenAll(Controller.Drivers.Select(driver => driver.DiscoverAsync(
                device => RunOnUiThread(() => AddOrUpdate(device)), ScanDuration, work.Token)));
            await Task.Yield();
            if (work.IsCancellationRequested) return;
            if (_found.Count == 1) _selected = _found[0];
            SetState(Mode.Idle, _found.Count switch
            {
                0 => "No supported UPS found. Check that it is on and nearby, then scan again.",
                1 => "Found 1 UPS. Tap Pair to add it.",
                var n => $"Found {n} devices. Tap yours, then Pair.",
            });
        }
        catch (Exception ex) when (!work.IsCancellationRequested)
        {
            SetState(Mode.Idle, $"Scan failed: {ex.Message}", error: true);
        }
    }

    private void AddOrUpdate(DiscoveredUps device)
    {
        var index = _found.FindIndex(d => d.DriverId == device.DriverId && d.Address == device.Address);
        if (index < 0) _found.Add(device);
        else if (_found[index].ConnectedTo is not null) return; // another device is connected to it: it is read through that one
        else _found[index] = device;
        Refresh();
    }

    private async Task PairAsync()
    {
        if (_selected is not { } selected) return;
        var id = _userId.Trim();
        if (selected.ConnectedTo is { } pc)
        {
            // Read through that device: connecting would take the UPS away from it. The user ID matters only if this
            // phone takes over one day.
            Controller.Log.Info($"Pairing {selected.Name}, which {pc} is connected to");
            await FinishPairingAsync(new PairedUps(selected.DriverId, selected.Address, selected.Name, selected.Model, id.Length > 0 ? id : null),
                selected.Label);
            return;
        }

        var driver = Controller.Drivers.First(d => d.Id == selected.DriverId);
        var userId = driver.RequiresUserId ? id : null;
        if (userId == "")
        {
            SetState(Mode.Idle, "Enter the EcoFlow user ID, or sign in to look it up.", error: true);
            return;
        }
        var candidate = new PairedUps(selected.DriverId, selected.Address, selected.Name, selected.Model, userId);

        var work = StartWork(ConnectTimeout); // also stops a running scan
        SetState(Mode.Connecting, $"Connecting to {selected.Name}…");

        // A UPS paired already is connected to: let go of it for the check.
        var existing = Controller.Find(candidate);
        if (existing is not null) await Controller.PauseAsync(existing);
        try
        {
            UpsStatus status;
            await using (var connection = driver.CreateConnection(candidate))
            {
                status = await connection.ReadAsync(work.Token);
            }
            Controller.Log.Info($"Pairing check OK: {status.Model}, serial {status.Serial}, battery {status.BatteryPercent} %");
            await FinishPairingAsync(candidate, null);
        }
        catch (Exception ex)
        {
            if (existing is not null) Controller.Resume(existing);
            var message = ex is System.OperationCanceledException ? "the UPS did not answer in time" : ex.Message;
            SetState(Mode.Idle, $"Could not connect: {message}", error: true);
        }
    }

    private async Task FinishPairingAsync(PairedUps device, string? label)
    {
        await Controller.PairAsync(device, label);
        if (!MonitorService.Running) MonitorService.Start(this);
        Finish();
    }

    private async Task SignInAsync()
    {
        var password = _passwordField?.Text ?? "";
        if (_email.Trim() == "" || password == "")
        {
            SetState(Mode.Idle, "Enter the email address and password of your EcoFlow account.", error: true);
            return;
        }

        var work = StartWork(SignInTimeout); // also stops a running scan
        SetState(Mode.SigningIn, "Signing in to EcoFlow…");
        try
        {
            _userId = await EcoFlowAccount.GetUserIdAsync(Http, _email, password, work.Token);
            if (_userIdField is not null) _userIdField.Text = _userId;
            _passwordField?.Text = "";
            Controller.Log.Info("Looked up the EcoFlow user ID");
            SetState(Mode.Idle, "Found your user ID. Tap Pair to connect.");
        }
        catch (Exception ex)
        {
            SetState(Mode.Idle, ex is System.OperationCanceledException ? "EcoFlow did not answer in time" : ex.Message, error: true);
        }
    }
}
