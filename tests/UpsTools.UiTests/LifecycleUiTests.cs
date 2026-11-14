using System.Diagnostics;
using System.Windows.Automation;
using Microsoft.Win32;
using UpsTools.UiTests.Support;

namespace UpsTools.UiTests;

/// <summary>Starting, closing, the single instance, and the command-line modes the installer uses.</summary>
public sealed class LifecycleUiTests : IDisposable
{
    private readonly AppUnderTest _app = new();

    public void Dispose() => _app.Dispose();

    private static void Close(AutomationElement window) =>
        ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();

    [Fact]
    public void Closing_the_window_keeps_it_running_in_the_tray_and_starting_it_again_shows_it()
    {
        _app.Seed();
        var window = _app.Start()!;

        Close(window);

        Ui.Until(() => _app.TryMainWindow() is null, "the window to hide");
        Thread.Sleep(500);
        Assert.False(_app.HasExited);

        // Starting it again only brings the running copy's window back.
        using var second = Process.Start(_app.StartInfo())!;
        Assert.True(second.WaitForExit(TimeSpan.FromSeconds(20)));
        Assert.Equal(0, second.ExitCode);
        _app.MainWindow().HasText("LinkText", "Connected");
        Assert.False(_app.HasExited);
    }

    [Fact]
    public void Closing_the_window_exits_when_close_to_tray_is_off()
    {
        _app.Seed(app: s => s["CloseToTray"] = false);
        var window = _app.Start()!;

        Close(window);

        Assert.True(_app.Process!.WaitForExit(TimeSpan.FromSeconds(15)));
        Assert.Contains("UPS Tools exiting", _app.Log());
    }

    [Fact]
    public void Background_starts_without_a_window_and_exit_stops_it()
    {
        _app.Seed();
        _app.Start("--background");
        Thread.Sleep(1500);
        Assert.Null(_app.TryMainWindow());

        Assert.Equal(0, _app.Run("--exit"));

        Assert.True(_app.Process!.WaitForExit(TimeSpan.FromSeconds(5))); // --exit returns once it has gone
        Assert.Contains("UPS Tools exiting", _app.Log());
    }

    [Theory]
    [InlineData(false, "PC is shutting down; disconnecting from the UPS")]
    [InlineData(true, "Signing out of Windows; disconnecting from the UPS")]
    public void Shutting_down_or_signing_out_disconnects_and_exits(bool signingOut, string logged)
    {
        _app.Seed();
        _app.Start("--background");
        Ui.Until(() => _app.Log().Contains("UPS Tools started"), "the app to start");
        Thread.Sleep(1000);

        Session.End(_app.Pid, signingOut);

        Assert.True(_app.Process!.WaitForExit(TimeSpan.FromSeconds(15)));
        var log = _app.Log();
        Assert.Contains(logged, log);
        Assert.True(log.IndexOf(logged, StringComparison.Ordinal) < log.IndexOf("UPS Tools exiting", StringComparison.Ordinal));
    }

    [Fact]
    public void Exit_without_a_running_copy_succeeds_at_once()
    {
        var watch = Stopwatch.StartNew();
        Assert.Equal(0, _app.Run("--exit"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Uninstall_exits_the_app_and_removes_what_it_registered()
    {
        _app.Seed();
        _app.Start();
        var window = _app.Tab("Configuration");
        window.ByName("Start with Windows").SetChecked(true);
        window.ByName("Send test notification").Invoke();
        Ui.Until(() => Directory.Exists(_app.CacheFolder), "the notification images");

        Assert.Equal(0, _app.Run("--uninstall"));

        Assert.True(_app.Process!.WaitForExit(TimeSpan.FromSeconds(5)));
        using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            Assert.Null(run?.GetValue(_app.RunValueName));
        Assert.Null(Registry.CurrentUser.OpenSubKey($@"Software\Classes\AppUserModelId\{_app.AppUserModelId}"));
        Assert.False(Directory.Exists(_app.CacheFolder));
        Assert.True(File.Exists(_app.SettingsFile)); // the user's
        Assert.Contains("Uninstall cleanup finished", _app.Log());
    }

    [Fact]
    public void An_unreadable_settings_file_is_kept_aside()
    {
        Directory.CreateDirectory(_app.SettingsFolder);
        File.WriteAllText(_app.SettingsFile, "{ broken");

        var window = _app.Start()!;

        window.HasText("DeviceTitle", "No UPS paired");
        Assert.Equal("{ broken", File.ReadAllText(_app.SettingsFile + ".broken"));
        Ui.Until(() => window.AllById("ActivityMessage").Any(m => m.Text().StartsWith("Settings file is unreadable")), "the warning");
    }

    [Fact]
    public void A_profile_never_talks_to_another_copy()
    {
        using var other = new AppUnderTest();
        other.Seed(paired: false);
        _app.Seed();

        _app.Start();
        other.Start();

        Assert.NotEqual(_app.Pid, other.Pid);
        _app.MainWindow().HasText("DeviceTitle", "Bluetti Simulator");
        other.MainWindow().HasText("DeviceTitle", "No UPS paired");
        Assert.Equal(0, other.Run("--exit"));
        Assert.True(other.Process!.WaitForExit(TimeSpan.FromSeconds(5)));
        Assert.False(_app.HasExited);
    }
}
