using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using UpsTools.Services;

namespace UpsTools.Droid.Platform;

/// <summary>The app's settings: monitoring, network sharing, notifications, the UPSes, and about.</summary>
[Activity(Label = "Settings", Theme = "@style/AppTheme", Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout)]
public sealed class SettingsActivity : ScreenActivity
{
    private TextView? _sharingStatus;
    private LinearLayout? _neighbours;
    private EditText? _code;

    protected override string ScreenTitle => "Settings";

    protected override void OnResume()
    {
        base.OnResume();
        Controller.SharingChanged += ShowSharing;
    }

    protected override void OnPause()
    {
        CommitCode();
        Controller.SharingChanged -= ShowSharing;
        base.OnPause();
    }

    protected override void Build(LinearLayout content)
    {
        var settings = Controller.Settings;

        // ---- UPSes ----
        content.AddView(Ui.Heading(this, "UPSes"));
        var upses = Ui.Card(this, 4);
        if (Controller.Upses.Count == 0) upses.AddView(Ui.Text(this, "No UPS paired.", 14, secondary: true));
        foreach (var unit in Controller.Upses)
        {
            var row = Ui.Column(this, 2);
            row.SetPadding(0, Ui.Dp(this, 6), 0, Ui.Dp(this, 6));
            row.AddView(Ui.Text(this, unit.Title, 16, bold: true));
            row.AddView(Ui.Text(this, $"{unit.Device.Name} · {unit.Device.Address} · {unit.Route}", 12, secondary: true));
            row.Clickable = true;
            row.Click += (_, _) => StartActivity(new Intent(this, typeof(UpsSettingsActivity)).PutExtra(UpsSettingsActivity.ExtraId, unit.Settings.Id));
            upses.AddView(row, Ui.Match());
        }
        upses.AddView(Ui.Button(this, "Add UPS…", () => StartActivity(typeof(PairActivity))));
        upses.AddView(Ui.Text(this,
            "A UPS accepts one Bluetooth connection at a time. While UPS Tools is connected to it, its own app can only reach it over Wi-Fi. " +
            "To watch it from several devices, turn on Network sharing below on each of them: one connects, the others read it through that one.",
            13, secondary: true));
        content.AddView(upses, Ui.Match());

        // ---- monitoring ----
        content.AddView(Ui.Heading(this, "Monitoring"));
        var monitoring = Ui.Card(this, 14);
        monitoring.AddView(Ui.Number(this, "UPS update period",
            "How often each UPS is polled (2–600 seconds). \"UPS unreachable\" fires after 3 failed polls in a row.", "s",
            AppSettings.MinUpdatePeriodSeconds, AppSettings.MaxUpdatePeriodSeconds, () => settings.UpdatePeriodSeconds, value =>
            {
                settings.UpdatePeriodSeconds = value;
                Controller.ApplyPeriod();
            }), Ui.Match());
        monitoring.AddView(Ui.Switch(this, "Start when the phone starts",
            "Monitoring starts again after the phone restarts or UPS Tools is updated.", settings.StartAtBoot, on =>
            {
                settings.StartAtBoot = on;
                Controller.Save();
            }), Ui.Match());
        monitoring.AddView(Ui.Switch(this, "Keep running with the screen off",
            "Keeps the processor awake while monitoring, so that polls and events don't wait until the screen turns on. Uses more battery: best for a phone or tablet that stays plugged in.",
            settings.KeepAwake, on =>
            {
                settings.KeepAwake = on;
                Controller.Save();
                MonitorService.SettingsChanged();
            }), Ui.Match());
        var power = (PowerManager)GetSystemService(PowerService)!;
        if (!power.IsIgnoringBatteryOptimizations(PackageName))
        {
            monitoring.AddView(Ui.Banner(this,
                "Battery optimisation may stop UPS Tools in the background. Let it run unrestricted.", Ui.Palette.Warning, "Allow",
                () => StartActivity(new Intent(Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,
                    Android.Net.Uri.Parse("package:" + PackageName)))), Ui.Match());
        }
        var running = MonitorService.Running;
        monitoring.AddView(Ui.Button(this, running ? "Stop monitoring" : "Start monitoring", () =>
        {
            if (MonitorService.Running) MonitorService.Stop(this);
            else MonitorService.Start(this);
            Toast(MonitorService.Running ? "Monitoring stops" : "Monitoring starts");
            Finish();
        }));
        content.AddView(monitoring, Ui.Match());

        // ---- sharing ----
        content.AddView(Ui.Heading(this, "Network sharing"));
        var sharing = Ui.Card(this, 10);
        sharing.AddView(Ui.Switch(this, "Share with other devices",
            "PCs and phones running UPS Tools on this Wi-Fi network with the same sharing code find each other, and only one of them " +
            "connects to each UPS over Bluetooth: the others read it through that one, each with its own notifications. If that one " +
            "goes away, another takes over. UPSes the others are connected to show up in Add UPS.",
            settings.Sharing.Enabled, on =>
            {
                Controller.SetSharing(on);
                MonitorService.SettingsChanged();
                QueueRebuild();
            }), Ui.Match());
        if (settings.Sharing.Enabled)
        {
            sharing.AddView(Ui.Text(this, "Give every device the same code: copy it here, and paste it on the others.", 13, secondary: true));
            var codeRow = Ui.Row(this);
            _code = Ui.Field(this, settings.Sharing.Code ?? "", "Sharing code");
            _code.ImeOptions = Android.Views.InputMethods.ImeAction.Done;
            _code.FocusChange += (_, e) =>
            {
                if (!e.HasFocus) CommitCode();
            };
            _code.EditorAction += (_, e) =>
            {
                e.Handled = true;
                CommitCode();
            };
            codeRow.AddView(_code, Ui.Weight());
            codeRow.AddView(Ui.Button(this, "Copy", () =>
            {
                var clipboard = (ClipboardManager)GetSystemService(ClipboardService)!;
                clipboard.PrimaryClip = ClipData.NewPlainText("UPS Tools sharing code", settings.Sharing.Code ?? "");
                Toast("Copied");
            }));
            sharing.AddView(codeRow, Ui.Match());
            sharing.AddView(Ui.Button(this, "New code…", () =>
                new AlertDialog.Builder(this)
                    .SetTitle("Make a new sharing code?")!
                    .SetMessage("The other devices have to be given the new code to keep sharing with this one.")!
                    .SetPositiveButton("New code", (_, _) =>
                    {
                        Controller.NewSharingCode();
                        _code.Text = settings.Sharing.Code ?? "";
                    })!
                    .SetNegativeButton("Cancel", (_, _) => { })!
                    .Show()));
            _sharingStatus = Ui.Text(this, "", 13);
            sharing.AddView(_sharingStatus);
            sharing.AddView(Ui.Text(this, $"Other devices found (this one is {Controller.DeviceName}):", 13, secondary: true));
            _neighbours = Ui.Column(this, 2);
            sharing.AddView(_neighbours, Ui.Match());
            if (!MonitorService.Running) sharing.AddView(Ui.Text(this, "Sharing starts with monitoring.", 13, secondary: true));
        }
        else
        {
            (_sharingStatus, _neighbours, _code) = (null, null, null);
        }
        content.AddView(sharing, Ui.Match());
        ShowSharing();

        // ---- notifications ----
        content.AddView(Ui.Heading(this, "Notifications"));
        var notifications = Ui.Card(this, 6);
        notifications.AddView(Ui.Text(this,
            "Which events of a UPS notify you is set for each UPS. Android's settings decide how they show: \"Power problems\" pop up and sound, " +
            "\"Power back\" doesn't pop up, \"Monitoring status\" is the notification that stays while monitoring.",
            13, secondary: true));
        var buttons = Ui.Row(this);
        buttons.AddView(Ui.Button(this, "Send test notification", Controller.SendTestNotification));
        buttons.AddView(Ui.Button(this, "Android settings", () =>
            StartActivity(new Intent(Android.Provider.Settings.ActionAppNotificationSettings)
                .PutExtra(Android.Provider.Settings.ExtraAppPackage, PackageName))));
        notifications.AddView(buttons);
        content.AddView(notifications, Ui.Match());

        // ---- about ----
        content.AddView(Ui.Heading(this, "UPS Tools"));
        var about = Ui.Card(this, 6);
        var version = AppVersion.Of(this);
        about.AddView(Ui.Text(this, $"Version {version}", 14));
        _updateStatus = Ui.Text(this, "", 13);
        about.AddView(_updateStatus);
        var actions = Ui.Row(this);
        _updateButton = Ui.Button(this, "", () => _ = OnUpdateClickedAsync());
        actions.AddView(_updateButton);
        actions.AddView(Ui.Button(this, "Share log", () => LogSharing.Share(this, Controller)));
        about.AddView(actions);
        about.AddView(Ui.Button(this, "Project on GitHub", () =>
            StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(ProjectUrl)))));
        content.AddView(about, Ui.Match());
        ShowUpdate();
    }

    public const string ProjectUrl = $"https://github.com/{ApkUpdater.Repository}";

    // ---- updates ----

    private static readonly ApkUpdater Updater = new();
    private TextView? _updateStatus;
    private Button? _updateButton;

    /// <summary>Where checking for updates is: kept across rebuilds of the screen (and its rotations).</summary>
    private static string _updateText = "";
    private static bool _updateProblem;
    private static ApkRelease? _available;
    private static bool _busy;

    private void ShowUpdate()
    {
        if (_updateStatus is null || _updateButton is null) return;
        _updateStatus.Text = _updateText;
        _updateStatus.Visibility = _updateText.Length > 0 ? Android.Views.ViewStates.Visible : Android.Views.ViewStates.Gone;
        _updateStatus.SetTextColor(_updateProblem ? Ui.ToneColor(this, ViewModels.Tone.Bad) : Ui.Primary(this));
        _updateButton.Text = _available is { } release ? $"Update to {release.Version.ToString(3)}" : "Check for updates";
        _updateButton.Enabled = !_busy;
    }

    private void SetUpdate(string text, bool problem = false, bool busy = false)
    {
        (_updateText, _updateProblem, _busy) = (text, problem, busy);
        ShowUpdate();
    }

    private async Task OnUpdateClickedAsync()
    {
        if (_busy) return;
        if (_available is not { } release)
        {
            SetUpdate("Looking for a newer version…", busy: true);
            try
            {
                var latest = await Updater.GetLatestAsync();
                var current = Version.TryParse(AppVersion.Of(this), out var v) ? v : new Version(0, 0, 0);
                if (latest.Version > current)
                {
                    _available = latest;
                    SetUpdate($"UPS Tools {latest.Version.ToString(3)} is out ({latest.Size / 1048576.0:0.0} MB). What's new: {latest.PageUrl}");
                }
                else
                {
                    SetUpdate($"UPS Tools is up to date ({current.ToString(3)} is the latest).");
                }
            }
            catch (Exception ex)
            {
                SetUpdate($"Couldn't check for updates: {Message(ex)}", problem: true);
            }
            return;
        }

        if (!ApkInstaller.Allowed(this))
        {
            SetUpdate("Allow UPS Tools to install apps (Install unknown apps), come back, and tap Update again.");
            ApkInstaller.AskToAllow(this);
            return;
        }

        SetUpdate($"Downloading {release.ApkName}…", busy: true);
        try
        {
            var progress = new Progress<double>(p => SetUpdate($"Downloading {release.ApkName}… {p:P0}", busy: true));
            var apk = await Updater.DownloadAsync(release, Path.Combine(CacheDir!.AbsolutePath, "update"), progress);
            Controller.Log.Info($"Downloaded UPS Tools {release.Version.ToString(3)}; installing it");
            SetUpdate("Confirm the update in the window Android shows. Monitoring starts again after it.");
            ApkInstaller.Install(this, apk);
        }
        catch (Exception ex)
        {
            SetUpdate($"The update failed: {Message(ex)}", problem: true);
            Controller.Log.Warn($"The update failed: {Message(ex)}");
        }
    }

    private static string Message(Exception ex) =>
        ex is System.OperationCanceledException or TimeoutException ? "GitHub didn't answer in time" : ex.Message;

    /// <summary>Takes the code typed in (another device's), or shows the one kept when it isn't a code.</summary>
    private void CommitCode()
    {
        if (_code is null) return;
        var kept = Controller.Settings.Sharing.Code ?? "";
        if (_code.Text == kept) return;
        if (!Controller.SetSharingCode(_code.Text ?? ""))
            Toast("That isn't a sharing code: it has 16 letters and digits, e.g. 7KQ2-M9XD-4FTB-R1WE.");
        _code.Text = Controller.Settings.Sharing.Code ?? "";
    }

    private void ShowSharing()
    {
        if (_sharingStatus is null || _neighbours is null) return;
        _sharingStatus.Text = Controller.SharingStatus;
        _sharingStatus.SetTextColor(Controller.SharingProblem ? Ui.ToneColor(this, ViewModels.Tone.Bad) : Ui.Primary(this));
        _neighbours.RemoveAllViews();
        if (Controller.Neighbours.Count == 0) _neighbours.AddView(Ui.Text(this, "None yet.", 13, secondary: true));
        foreach (var line in Controller.Neighbours) _neighbours.AddView(Ui.Text(this, "• " + line, 13));
    }
}
