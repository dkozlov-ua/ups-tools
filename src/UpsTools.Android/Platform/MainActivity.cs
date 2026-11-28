using Android.App;
using Android.Bluetooth;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using UpsTools.Services;
using UpsTools.ViewModels;
using View = Android.Views.View;

namespace UpsTools.Droid.Platform;

/// <summary>
/// The dashboard: every UPS with its readings, what keeps monitoring from working (permissions, Bluetooth off,
/// stopped), and the recent activity.
/// </summary>
[Activity(Label = "UPS Tools", MainLauncher = true, Exported = true, Theme = "@style/AppTheme", LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout)]
public sealed class MainActivity : ScreenActivity
{
    private const int PermissionsRequest = 1;
    private const int ActivityShown = 30;

    protected override bool HasBack => false;
    protected override string ScreenTitle => "UPS Tools";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (Permissions.Missing(this) is { Length: > 0 } missing) RequestPermissions(missing, PermissionsRequest);
        else StartMonitoringIfPaired();
    }

    protected override void OnResume()
    {
        base.OnResume();
        Controller.Changed += QueueRebuild;
        Controller.ActivityAdded += OnActivity;
    }

    protected override void OnPause()
    {
        Controller.Changed -= QueueRebuild;
        Controller.ActivityAdded -= OnActivity;
        base.OnPause();
    }

    private void OnActivity(LogEntry entry) => QueueRebuild();

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        StartMonitoringIfPaired();
        Rebuild();
    }

    /// <summary>Opening the app starts monitoring, as starting the Windows app does.</summary>
    private void StartMonitoringIfPaired()
    {
        if (Controller.Upses.Count > 0 && !MonitorService.Running && Permissions.HasBluetooth(this)) MonitorService.Start(this);
    }

    protected override void BuildActions(LinearLayout actions)
    {
        actions.AddView(Ui.Button(this, "Add UPS", () => StartActivity(typeof(PairActivity))));
        actions.AddView(Ui.Button(this, "Settings", () => StartActivity(typeof(SettingsActivity))));
    }

    protected override void Build(LinearLayout content)
    {
        BuildProblems(content);

        if (Controller.Upses.Count == 0)
        {
            var card = Ui.Card(this);
            card.AddView(Ui.Text(this, "No UPS paired", 18, bold: true));
            card.AddView(Ui.Text(this, "Add your UPS to start monitoring it. It has to be on and nearby, or connected to a PC or phone that shares it on this Wi-Fi network.",
                14, secondary: true));
            card.AddView(Ui.Button(this, "Add UPS…", () => StartActivity(typeof(PairActivity)), filled: true));
            content.AddView(card, Ui.Match());
        }

        foreach (var unit in Controller.Upses) content.AddView(UpsCard(unit), Ui.Match());

        if (Controller.Activity.Count > 0)
        {
            content.AddView(Ui.Heading(this, "Recent activity"));
            var card = Ui.Card(this, 6);
            foreach (var entry in Controller.Activity.Take(ActivityShown))
            {
                var row = Ui.Row(this, 10);
                row.SetGravity(GravityFlags.Top);
                row.AddView(Ui.Text(this, entry.TimeText, 12, secondary: true));
                var color = entry.Level switch
                {
                    LogLevel.Warning => Ui.ToneColor(this, Tone.Bad),
                    LogLevel.Event => Ui.ToneColor(this, Tone.Warning),
                    _ => Ui.Primary(this),
                };
                row.AddView(Ui.Text(this, entry.Message, 13, bold: entry.Level == LogLevel.Event, color: color), Ui.Weight());
                card.AddView(row, Ui.Match());
            }
            content.AddView(card, Ui.Match());
        }
    }

    /// <summary>What keeps monitoring from working, each with what to do about it.</summary>
    private void BuildProblems(LinearLayout content)
    {
        var problem = Ui.Palette.Bad;
        if (!Permissions.HasBluetooth(this))
        {
            content.AddView(Ui.Banner(this,
                OperatingSystem.IsAndroidVersionAtLeast(31)
                    ? "UPS Tools needs access to nearby devices to find your UPS and connect to it over Bluetooth."
                    : "UPS Tools needs the location permission to find your UPS over Bluetooth (Android asks for it for Bluetooth scans).",
                problem, "Allow", AskForPermissions), Ui.Match());
        }
        else if (BluetoothOff())
        {
            content.AddView(Ui.Banner(this, "Bluetooth is off: UPS Tools can't reach a UPS over Bluetooth.", problem, "Turn on",
                () => StartActivity(new Intent(BluetoothAdapter.ActionRequestEnable))), Ui.Match());
        }
        if (!Permissions.HasNotifications(this))
        {
            content.AddView(Ui.Banner(this, "Notifications are off: you won't be told when the grid goes down.", Ui.Palette.Warning,
                "Allow", AskForPermissions), Ui.Match());
        }
        if (Controller.Upses.Count > 0 && !MonitorService.Running)
        {
            content.AddView(Ui.Banner(this, "Monitoring is off.", Ui.Palette.Warning, "Start", () =>
            {
                if (Permissions.HasBluetooth(this) || Controller.Settings.Sharing.Enabled) MonitorService.Start(this);
                else AskForPermissions();
                QueueRebuild();
            }), Ui.Match());
        }
    }

    private bool BluetoothOff()
    {
        var manager = (BluetoothManager?)GetSystemService(BluetoothService);
        return manager?.Adapter is { IsEnabled: false };
    }

    /// <summary>Asks again; once Android stops asking (denied twice), opens the app's settings page instead.</summary>
    private void AskForPermissions()
    {
        var missing = Permissions.Missing(this);
        if (missing.Length == 0) return;
        if (missing.Any(ShouldShowRequestPermissionRationale) || !Asked)
        {
            Asked = true;
            RequestPermissions(missing, PermissionsRequest);
        }
        else
        {
            StartActivity(new Intent(Android.Provider.Settings.ActionApplicationDetailsSettings,
                Android.Net.Uri.FromParts("package", PackageName, null)));
        }
    }

    private static bool Asked { get; set; }

    /// <summary>One UPS: its name and link, then its readings in two columns of three, as the compact dashboard on Windows.</summary>
    private View UpsCard(UpsUnit unit)
    {
        var d = unit.Dashboard;
        var card = Ui.Card(this, 10);

        var top = Ui.Row(this);
        top.AddView(Ui.Text(this, d.DeviceTitle, 18, bold: true), Ui.Weight());
        var (linkText, linkColor) = !MonitorService.Running
            ? ("Not monitoring", Ui.Palette.Neutral)
            : d.Link switch
            {
                LinkStatus.Online => (d.LinkText, Ui.ToneColor(this, Tone.Good)),
                LinkStatus.Unreachable => (d.LinkText, Ui.ToneColor(this, Tone.Bad)),
                LinkStatus.Retrying => (d.LinkText, Ui.ToneColor(this, Tone.Warning)),
                _ => (d.LinkText, Ui.Palette.Neutral),
            };
        top.AddView(Ui.Chip(this, linkText, linkColor));
        card.AddView(top, Ui.Match());
        card.AddView(Ui.Text(this, d.DeviceSubtitle, 12, secondary: true));

        var tiles = new GridLayout(this) { ColumnCount = 2 };
        AddTile(tiles, "Grid", d.GridValue, d.GridDetail, d.GridTone);
        AddTile(tiles, "Battery", d.BatteryValue, d.BatteryDetail, d.BatteryTone, d.BatteryValue == "—" ? null : d.BatteryPercent);
        AddTile(tiles, "Charge rate", d.FlowValue, d.FlowDetail, d.FlowTone);
        AddTile(tiles, "Time left", d.RuntimeValue, d.RuntimeDetail, Tone.Neutral);
        AddTile(tiles, "Input", d.InputValue, d.InputDetail, Tone.Neutral);
        AddTile(tiles, "Load", d.OutputValue, d.OutputDetail, Tone.Neutral);
        tiles.Alpha = d.IsStale || !MonitorService.Running ? 0.55f : 1f;
        card.AddView(tiles, Ui.Match());

        if (d.LastUpdated.Length > 0) card.AddView(Ui.Text(this, d.LastUpdated, 12, secondary: true));

        card.Clickable = true;
        card.Click += (_, _) => StartActivity(new Intent(this, typeof(HistoryActivity)).PutExtra(HistoryActivity.ExtraId, unit.Settings.Id));
        return card;
    }

    private void AddTile(GridLayout grid, string caption, string value, string detail, Tone tone, double? percent = null)
    {
        var tile = Ui.Column(this, 2);
        tile.SetPadding(0, Ui.Dp(this, 6), Ui.Dp(this, 8), Ui.Dp(this, 6));
        tile.AddView(Ui.Text(this, caption, 12, secondary: true));
        tile.AddView(Ui.Text(this, value, 22, bold: true, color: Ui.ToneColor(this, tone)));
        if (percent is { } p)
        {
            var bar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 100, Progress = (int)p };
            bar.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(Ui.ToneColor(this, tone));
            tile.AddView(bar, Ui.Match());
        }
        if (detail.Length > 0) tile.AddView(Ui.Text(this, detail, 12, secondary: true));

        var layout = new GridLayout.LayoutParams(GridLayout.InvokeSpec(GridLayout.Undefined), GridLayout.InvokeSpec(GridLayout.Undefined, 1f))
        {
            Width = 0,
        };
        grid.AddView(tile, layout);
    }
}
