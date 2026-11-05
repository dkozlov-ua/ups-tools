using System.Windows.Automation;
using Microsoft.Win32;
using UpsTools.UiTests.Support;

namespace UpsTools.UiTests;

/// <summary>The Configuration tab and the Find UPS dialog.</summary>
public sealed class ConfigurationUiTests : IDisposable
{
    private readonly AppUnderTest _app = new();

    public void Dispose() => _app.Dispose();

    private string? RunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue(_app.RunValueName) as string;
    }

    private static string RepoVersion()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "VERSION"))) return File.ReadAllText(Path.Combine(dir.FullName, "VERSION")).Trim();
        throw new FileNotFoundException("VERSION");
    }

    [Fact]
    public void General_settings_are_saved()
    {
        _app.Seed();
        _app.Start();
        var window = _app.Tab("Configuration");

        window.HasText("PairedTitle", "Bluetti Simulator");
        window.HasText("PairedDetail", "SIMULATOR0001 · 00:00:00:00:00:01 · via Bluetooth LE");
        window.HasText("Version", "Version " + RepoVersion());

        var closeToTray = window.ByName("Close to tray");
        Assert.True(closeToTray.IsChecked());
        closeToTray.SetChecked(false);
        Ui.Until(() => !(bool)_app.Settings()["CloseToTray"]!, "Close to tray to be saved");

        var grid = window.ByName("Grid lost");
        Assert.False(grid.IsChecked());
        grid.SetChecked(true);
        Ui.Until(() => (bool)_app.Settings()["Notifications"]!["GridLost"]!, "the notification switch to be saved");

        var period = window.ByName("UPS update period in seconds");
        period.SetFocus();
        period.SetValue("1");
        Ui.Until(() => (int)_app.Settings()["UpdatePeriodSeconds"]! == 2, "the period to be saved, at least 2 s");
        closeToTray.SetFocus();
        Ui.Until(() => period.Value() == "2", "the box to show what applies");

        period.SetFocus();
        period.SetValue("45");
        Ui.Until(() => (int)_app.Settings()["UpdatePeriodSeconds"]! == 45, "45 s to be saved");
    }

    [Fact]
    public void Battery_low_limits_are_saved()
    {
        _app.Seed();
        _app.Start();
        var window = _app.Tab("Configuration");

        var percent = window.ByName("Battery low at this charge in percent");
        var minutes = window.ByName("Battery low at this time left in minutes");
        Assert.Equal(("20", "10"), (percent.Value(), minutes.Value()));

        percent.SetFocus();
        percent.SetValue("150");
        Ui.Until(() => (int)_app.Settings()["BatteryLowPercent"]! == 99, "the charge limit to be saved, at most 99 %");
        minutes.SetFocus();
        Ui.Until(() => percent.Value() == "99", "the box to show what applies");

        minutes.SetValue("0");
        Ui.Until(() => (int)_app.Settings()["BatteryLowMinutes"]! == 0, "the time limit to be turned off");
    }

    [Fact]
    public void The_wait_before_grid_lost_is_saved()
    {
        _app.Seed();
        _app.Start();
        var window = _app.Tab("Configuration");

        var delay = window.ByName("Wait before Grid lost in seconds");
        Assert.Equal("0", delay.Value());
        delay.SetFocus();
        delay.SetValue("90");
        Ui.Until(() => (int)_app.Settings()["GridLostDelaySeconds"]! == 90, "the delay to be saved");

        var unreachable = window.ByName("Battery low after this many minutes unreachable on battery");
        Assert.Equal("0", unreachable.Value());
        unreachable.SetFocus();
        unreachable.SetValue("5");
        Ui.Until(() => (int)_app.Settings()["UnreachableOnBatteryMinutes"]! == 5, "the unreachable limit to be saved");
    }

    [Fact]
    public void Start_with_windows_adds_and_removes_the_run_entry()
    {
        _app.Seed();
        _app.Start();
        var window = _app.Tab("Configuration");
        var startWithWindows = window.ByName("Start with Windows");
        Assert.False(startWithWindows.IsChecked());

        startWithWindows.SetChecked(true);
        Ui.Until(() => RunValue() is not null, "the Run entry");
        Assert.Equal($"\"{AppUnderTest.Exe}\" --background --profile {_app.Profile}", RunValue());

        startWithWindows.SetChecked(false);
        Ui.Until(() => RunValue() is null, "the Run entry to go");
    }

    [Fact]
    public void A_run_entry_left_by_an_older_location_is_pointed_at_this_exe_on_start()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            key.SetValue(_app.RunValueName, "\"C:\\Old place\\UpsTools.exe\" --background");
        _app.Seed();
        _app.Start();

        Ui.Until(() => RunValue()!.StartsWith($"\"{AppUnderTest.Exe}\""), "the Run entry to be updated");
        Assert.True(_app.Tab("Configuration").ByName("Start with Windows").IsChecked());
    }

    [Fact]
    public void Forget_asks_first_then_forgets_the_ups()
    {
        _app.Seed();
        _app.Start();
        var window = _app.Tab("Configuration");

        window.ByName("Forget").InvokeAsync();
        Assert.Equal("Stop monitoring this UPS and forget it?", Ui.Answer(_app.Pid, "No"));
        window.HasText("PairedTitle", "Bluetti Simulator");

        window.ByName("Forget").InvokeAsync();
        Ui.Answer(_app.Pid, "Yes");

        window.HasText("PairedTitle", "No UPS paired");
        window.HasText("PairedDetail", "Find your UPS to start monitoring it.");
        Ui.Until(() => _app.Settings()["Ups"] is null, "the UPS to be forgotten");
        Ui.Until(() => window.TryByName("Forget") is null || window.ByName("Forget").Current.IsOffscreen, "Forget to hide");
        _app.Tab("Dashboard").HasText("DeviceTitle", "No UPS paired");
    }

    [Fact]
    public void Find_ups_finds_the_simulator_and_pairs_it()
    {
        _app.Seed(paired: false);
        _app.Start();
        var window = _app.Tab("Configuration");

        window.ByName("Find UPS…").InvokeAsync();
        var dialog = Ui.WindowOf(_app.Pid, "Find UPS");
        dialog.HasText("StatusText", "Found 1 UPS. Select it and click Pair.");
        dialog.ByName("Bluetti Simulator");
        var pair = dialog.ById("PairButton");
        Ui.Until(() => pair.Current.IsEnabled, "Pair to be enabled");
        pair.Invoke();

        Ui.Until(() => dialog.Current.IsOffscreen, "the dialog to close", TimeSpan.FromSeconds(30));
        window.HasText("PairedTitle", "Bluetti Simulator");
        Ui.Until(() => (string?)_app.Settings()["Ups"]?["DriverId"] == "simulated", "the pairing to be saved");
        Assert.Contains("Pairing check OK: Simulator, serial SIM0001, battery 80 %", _app.Log());
        _app.Tab("Dashboard").HasText("LinkText", "Connected");
    }

    [Fact]
    public void Double_clicking_the_found_ups_pairs_it()
    {
        _app.Seed(paired: false);
        _app.Start();
        var window = _app.Tab("Configuration");

        window.ByName("Find UPS…").InvokeAsync();
        var dialog = Ui.WindowOf(_app.Pid, "Find UPS");
        var item = dialog.ByName("Bluetti Simulator");
        Ui.Until(() => dialog.ById("PairButton").Current.IsEnabled, "the UPS to be selected");
        Input.Click(item.Centre());
        Input.Click(item.Centre());

        window.HasText("PairedTitle", "Bluetti Simulator", TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Find_ups_without_any_ups_says_so_and_cancel_keeps_things_as_they_were()
    {
        File.Delete(_app.SimulatorFile);
        _app.Seed(paired: false);
        _app.Start();
        var window = _app.Tab("Configuration");

        window.ByName("Find UPS…").InvokeAsync();
        var dialog = Ui.WindowOf(_app.Pid, "Find UPS");
        dialog.HasText("StatusText", "No supported UPS found. Check that it is on and nearby, then scan again.");
        Assert.False(dialog.ById("PairButton").Current.IsEnabled);

        _app.Simulate(new { GridPresent = true });
        dialog.ById("ScanButton").Invoke();
        dialog.HasText("StatusText", "Found 1 UPS. Select it and click Pair.");

        dialog.ByName("Cancel").Invoke();
        Ui.Until(() => dialog.Current.IsOffscreen, "the dialog to close");
        window.HasText("PairedTitle", "No UPS paired");
        Assert.Null(_app.Settings()["Ups"]);
    }

    [Fact]
    public void A_ups_that_does_not_answer_is_not_paired()
    {
        _app.Simulate(new { Fail = "the UPS is asleep" });
        _app.Seed(paired: false);
        _app.Start();
        var window = _app.Tab("Configuration");

        window.ByName("Find UPS…").InvokeAsync();
        var dialog = Ui.WindowOf(_app.Pid, "Find UPS");
        dialog.HasText("StatusText", "Found 1 UPS. Select it and click Pair.");
        dialog.ById("PairButton").Invoke();
        dialog.HasText("StatusText", "Could not connect: the UPS is asleep");

        dialog.ByName("Cancel").Invoke();
        window.HasText("PairedTitle", "No UPS paired");
    }

    [Fact]
    public void Monitoring_resumes_when_the_dialog_is_cancelled()
    {
        _app.Seed();
        _app.Start();
        var window = _app.Tab("Configuration");

        window.ByName("Find UPS…").InvokeAsync();
        var dialog = Ui.WindowOf(_app.Pid, "Find UPS");
        dialog.ByName("Cancel").Invoke();

        _app.Simulate(new { GridPresent = true, BatteryPercent = 55 });
        _app.Tab("Dashboard").HasText("BatteryValue", "55 %");
    }

    [Fact]
    public void The_test_notification_reaches_windows()
    {
        _app.Seed();
        _app.Start();
        var window = _app.Tab("Configuration");

        window.ByName("Send test notification").Invoke();

        Ui.Until(() => _app.Log().Contains("Sent a test notification"), "the log");
        Ui.Until(() => Windows.UI.Notifications.ToastNotificationManager.History.GetHistory(_app.AppUserModelId).Any(t => t.Tag == "test"),
            "the toast in the notification history");
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\AppUserModelId\{_app.AppUserModelId}");
        Assert.Equal($"UPS Tools ({_app.Profile})", key!.GetValue("DisplayName"));
        Assert.True(File.Exists((string)key.GetValue("IconUri")!));
        Assert.DoesNotContain("Windows notifications are unavailable", _app.Log());
    }
}
