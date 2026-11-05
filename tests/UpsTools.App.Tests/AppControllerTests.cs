using System.Globalization;
using UpsTools.AppTests.Support;
using UpsTools.Core;
using UpsTools.Services;
using UpsTools.ViewModels;

namespace UpsTools.AppTests;

/// <summary>
/// What the app does when the UPS changes state: dashboard, tray, scripts and notifications. Runs the real
/// controller and monitor on a WPF dispatcher, with a scripted UPS and recorded scripts and notifications.
/// </summary>
[Collection("Registry")] // the configuration reads the Start with Windows entry
public sealed class AppControllerTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly AppLog _log;
    private readonly FakeUps _ups = new();
    private readonly FakeTray _tray = new();
    private readonly FakeNotifications _notes = new();
    private readonly FakeScripts _scripts = new();
    private readonly FakePower _power = new();
    private readonly FakeCountdown _countdown = new();
    private AppController? _controller;

    public AppControllerTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        _log = new AppLog(_folder.Path);
    }

    public void Dispose()
    {
        if (_controller is { } c) UiThread.Run(c.StopMonitoringAsync).Wait(TimeSpan.FromSeconds(10));
        _folder.Dispose();
    }

    private static ScriptAction Script(string name, bool wait = false, bool enabled = true) =>
        new() { Path = $@"C:\scripts\{name}", WaitForExit = wait, Enabled = enabled };

    private static ScriptAction Power(PowerAction power, int countdown = 60) => new() { Power = power, CountdownSeconds = countdown };

    private async Task<AppController> Start(Action<AppSettings>? seed = null, bool paired = true, bool monitor = true)
    {
        var settings = new AppSettings { Ups = paired ? FakeUps.Paired : null };
        seed?.Invoke(settings);
        new SettingsStore(new AppLog(_folder.Path), _folder.Path).Save(settings);

        _controller = await UiThread.Run(() =>
        {
            var controller = new AppController(UiThread.Dispatcher, _log, [_ups], _scripts.RunAsync, _power, _countdown.ShowAsync);
            controller.Settings.UpdatePeriodSeconds = 0; // the scripted UPS sets the pace
            controller.AttachTray(_tray, _notes);
            if (monitor) controller.StartMonitoring();
            return controller;
        });
        return _controller;
    }

    private static Task<T> Ui<T>(Func<T> read) => UiThread.Run(read);

    /// <summary>Waits until <paramref name="condition"/>, read on the UI thread, holds.</summary>
    private static async Task UiUntil(Func<bool> condition, string? what = null, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await UiThread.Run(condition))
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what ?? "the condition"}");
            await Task.Delay(10);
        }
    }

    private Task Online(int battery = 80, DateTimeOffset? at = null)
    {
        _ups.Answer(true, battery, at);
        return UiUntil(() => _controller!.Dashboard.Link == LinkStatus.Online && _controller.Dashboard.BatteryValue == $"{battery} %", "the reading");
    }

    private Task GridLost(int battery = 70, DateTimeOffset? at = null)
    {
        _ups.Answer(false, battery, at);
        return UiUntil(() => _notes.All.Count > 0 || _log.Recent.Any(e => e.Message == "Grid lost"), "Grid lost");
    }

    // ---- not paired

    [Fact]
    public async Task Without_a_ups_nothing_is_monitored()
    {
        var controller = await Start(paired: false);

        Assert.Equal((TrayState.Neutral, "No UPS paired"), _tray.Current);
        Assert.Equal(0, _ups.Connections);
        Assert.Equal(LinkStatus.NotPaired, await Ui(() => controller.Dashboard.Link));
        Assert.False(await Ui(() => controller.Configuration.IsPaired));
        Assert.Equal(5, controller.EventScripts.Count);
    }

    [Fact]
    public async Task An_unknown_driver_is_reported()
    {
        await Start(s => s.Ups = FakeUps.Paired with { DriverId = "gone" });
        Assert.Contains(_log.Recent, e => e.Level == LogLevel.Warning && e.Message == "Cannot monitor FAKE123: Unknown UPS driver 'gone'");
        Assert.Equal(0, _ups.Connections);
    }

    // ---- readings

    [Fact]
    public async Task Readings_reach_the_dashboard_and_the_tray()
    {
        var controller = await Start();
        Assert.Equal((TrayState.Neutral, "Connecting to the UPS…"), _tray.Current);
        Assert.Equal(LinkStatus.Connecting, await Ui(() => controller.Dashboard.Link));

        await Online(80);

        Assert.Equal("Serial 2527008220279 · 00:11:22:33:44:55", await Ui(() => controller.Dashboard.DeviceSubtitle));
        Assert.Equal("Bluetti EL100V2", await Ui(() => controller.Dashboard.DeviceTitle));
        await UiUntil(() => _tray.Current == (TrayState.GridOnline, "Grid online · 80 % · 120 W load"), "the tray");
        Assert.Empty(_notes.All); // the first reading is a baseline
    }

    [Fact]
    public async Task On_battery_the_tray_turns_yellow_with_the_runtime()
    {
        await Start();
        await Online();
        await GridLost(70);
        await UiUntil(() => _tray.Current.State == TrayState.OnBattery);
        Assert.Equal("On battery · 70 % · 5 h 58 m left", _tray.Current.Status);
    }

    [Fact]
    public async Task A_reading_without_the_grid_state_leaves_the_tray_grey()
    {
        await Start();
        _ups.Answer(grid: null);
        await UiUntil(() => _tray.Current == (TrayState.Neutral, "Connected"));
    }

    [Fact]
    public async Task Failed_polls_show_reconnecting_then_unreachable()
    {
        var controller = await Start(s => s.ScriptsFor(UpsEventKind.UpsUnreachable).Add(Script("alarm.ps1")));
        var at = DateTimeOffset.Now.AddMinutes(-3);
        _ups.Answer(FakeUps.Status(true, at: at));
        await UiUntil(() => controller.Dashboard.Link == LinkStatus.Online);

        _ups.Fail();
        await UiUntil(() => controller.Dashboard.Link == LinkStatus.Retrying);
        Assert.Equal(TrayState.GridOnline, _tray.Current.State); // still the last reading

        _ups.Fail();
        _ups.Fail();
        await UiUntil(() => _notes.All.Count == 1);

        Assert.Equal((TrayState.Problem, "UPS unreachable"), _tray.Current);
        Assert.Equal("Unreachable", await Ui(() => controller.Dashboard.LinkText));
        var notice = Assert.Single(_notes.Notices);
        Assert.Equal(UpsEventKind.UpsUnreachable, notice.Kind);
        Assert.Contains($"(last reading at {at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)})", notice.Body);
        Assert.Equal("Started alarm.ps1", notice.Footnote);
        Assert.Equal(["alarm.ps1"], _scripts.Names);
        Assert.Contains(_log.Recent, e => e.Message == "UPS read failed: out of range");
    }

    // ---- events: scripts and notifications

    [Fact]
    public async Task Grid_lost_runs_its_enabled_scripts_in_order_and_says_so()
    {
        await Start(s => s.ScriptsFor(UpsEventKind.GridLost).AddRange(
            [Script("pause.ahk"), Script("off.ps1", enabled: false), Script("save.ps1", wait: true), Script("hibernate.bat")]));
        await Online();

        await GridLost(70);

        Assert.Equal([("pause.ahk", "Grid lost"), ("save.ps1", "Grid lost")], _scripts.Ran);
        var shown = Assert.Single(_notes.All);
        Assert.Equal(new FakeNotifications.Shown("grid", TrayState.OnBattery, "Grid lost — running on battery",
            "Battery 70 % · about 5 h 58 m left at 120 W", "Started pause.ahk; waiting for save.ps1, then hibernate.bat",
            "Bluetti EL100V2", true), shown);
        Assert.Contains(_log.Recent, e => e.Level == LogLevel.Event && e.Message == "Grid lost");

        _scripts.Exit("save.ps1");
        await Wait.Until(() => _scripts.Ran.Count == 3);
        Assert.Equal("hibernate.bat", _scripts.Names[^1]);
        await Task.Delay(100);
        Assert.Single(_notes.All); // all went well: nothing more to say
    }

    [Fact]
    public async Task Grid_restored_stops_what_grid_lost_still_had_queued_and_says_how_long_it_lasted()
    {
        await Start(s =>
        {
            s.ScriptsFor(UpsEventKind.GridLost).AddRange([Script("wait-5-min.ps1", wait: true), Script("hibernate.ps1")]);
            s.ScriptsFor(UpsEventKind.GridRestored).Add(Script("wake-nas.ahk"));
        });
        var t0 = DateTimeOffset.Now;
        await Online(at: t0);
        await GridLost(at: t0.AddMinutes(1));

        _ups.Answer(true, 75, t0.AddMinutes(13.5));
        await UiUntil(() => _notes.All.Count == 2);

        var restored = _notes.Notices[1];
        Assert.Equal("Power is back after 12 m on battery. Battery 75 %.", restored.Body);
        Assert.Equal("Stopped the Grid lost scripts before hibernate.ps1; started wake-nas.ahk", restored.Footnote);
        Assert.Equal(["wait-5-min.ps1", "wake-nas.ahk"], _scripts.Names);
        Assert.Contains(_log.Recent, e => e.Message == "Grid restored: stopped the Grid lost scripts; hibernate.ps1 won't run");
        Assert.Equal((TrayState.GridOnline, "Grid online · 75 % · 120 W load"), _tray.Current);

        await Task.Delay(100);
        Assert.Equal(2, _notes.All.Count); // the stopped chain doesn't report a problem
    }

    [Fact]
    public async Task Battery_low_runs_its_scripts_and_grid_restored_stops_them_and_grid_lost()
    {
        await Start(s =>
        {
            s.ScriptsFor(UpsEventKind.GridLost).AddRange([Script("wait-30-min.ps1", wait: true), Script("sleep.ps1")]);
            s.ScriptsFor(UpsEventKind.BatteryLow).AddRange([Script("save.ps1", wait: true), Script("hibernate.ps1")]);
        });
        await Online();
        await GridLost(30);

        _ups.Answer(false, 20);
        await UiUntil(() => _notes.All.Count == 2, "Battery low");
        Assert.Equal(new FakeNotifications.Shown("grid", TrayState.Problem, "Battery low",
            "Battery 20 % · about 1 h 42 m left at 120 W", "Waiting for save.ps1, then hibernate.ps1", "Bluetti EL100V2", true), _notes.All[1]);
        Assert.Contains(_log.Recent, e => e.Level == LogLevel.Event && e.Message == "Battery low");

        _ups.Answer(false, 19); // once per outage
        _ups.Answer(true, 19);
        await UiUntil(() => _notes.All.Count == 3, "Grid restored");

        Assert.Equal("Stopped the Grid lost scripts before sleep.ps1; stopped the Battery low scripts before hibernate.ps1",
            _notes.Notices[2].Footnote);
        Assert.Equal(["wait-30-min.ps1", "save.ps1"], _scripts.Names);
    }

    [Fact]
    public async Task Battery_low_counts_down_then_hibernates_and_runs_what_follows()
    {
        await Start(s => s.ScriptsFor(UpsEventKind.BatteryLow).AddRange([Script("save.ps1"), Power(PowerAction.Hibernate, 30), Script("after.ps1")]));
        await Online();
        await GridLost(15); // lost and low at once

        await UiUntil(() => _notes.All.Count == 2, "Battery low");
        Assert.Equal("Started save.ps1; counting down to Hibernate, then after.ps1", _notes.Notices[1].Footnote);
        Assert.Equal((PowerAction.Hibernate, TimeSpan.FromSeconds(30), "Battery low"), Assert.Single(_countdown.Shown));
        Assert.Empty(_power.Done);

        _countdown.GoAhead();
        await Wait.Until(() => _scripts.Names.Contains("after.ps1"));
        Assert.Equal([PowerAction.Hibernate], _power.Done);
        Assert.Equal(["save.ps1", "after.ps1"], _scripts.Names); // the built-in action isn't a script
        Assert.Contains(_log.Recent, e => e.Message == "Battery low: Hibernating the PC");
        await Task.Delay(100);
        Assert.Equal(2, _notes.All.Count);
    }

    [Fact]
    public async Task Grid_restored_during_the_countdown_stops_it()
    {
        await Start(s => s.ScriptsFor(UpsEventKind.BatteryLow).Add(Power(PowerAction.ShutDown)));
        await Online();
        await GridLost(15);
        await Wait.Until(() => _countdown.IsShowing);

        _ups.Answer(true, 16);
        await UiUntil(() => _notes.All.Count == 3, "Grid restored");

        Assert.Equal("Stopped the Battery low scripts before Shut down", _notes.Notices[2].Footnote);
        Assert.True(_countdown.WasStopped);
        Assert.Contains(_log.Recent, e => e.Message == "Grid restored: stopped the Battery low scripts; Shut down won't run");
        await Task.Delay(100);
        Assert.Empty(_power.Done);
        Assert.Equal(3, _notes.All.Count);
    }

    [Fact]
    public async Task Cancelling_the_countdown_skips_the_rest_without_a_problem_notification()
    {
        await Start(s => s.ScriptsFor(UpsEventKind.BatteryLow).AddRange([Power(PowerAction.Sleep), Script("after.ps1")]));
        await Online();
        await GridLost(15);
        await Wait.Until(() => _countdown.IsShowing);

        _countdown.Cancel();
        await UiUntil(() => _log.Recent.Any(e => e.Message == "Battery low: Sleep was cancelled"), "the cancel");
        await Task.Delay(100);

        Assert.Empty(_power.Done);
        Assert.Empty(_scripts.Names);
        Assert.Equal(2, _notes.All.Count); // Grid lost and Battery low; nothing about the cancel
    }

    [Fact]
    public async Task A_power_action_that_fails_is_reported()
    {
        _power.Refuse = "hibernation is turned off";
        var controller = await Start(s => s.ScriptsFor(UpsEventKind.GridLost).AddRange([Power(PowerAction.Hibernate, 0), Script("after.ps1")]), monitor: false);

        await UiThread.Run(() => controller.RunScripts(controller.EventScripts[0], "Manual run"));
        await UiUntil(() => _notes.All.Count == 1);

        Assert.Equal(("Manual run: Hibernate failed", "Hibernation is turned off, so after.ps1 didn't run."), (_notes.All[0].Title, _notes.All[0].Body));
        Assert.Empty(_countdown.Shown);
    }

    [Fact]
    public async Task Run_now_on_a_power_action_counts_down_too()
    {
        var controller = await Start(s => s.ScriptsFor(UpsEventKind.GridLost).Add(Power(PowerAction.ShutDown, 10)), monitor: false);

        await UiThread.Run(() => controller.RunScript(controller.Settings.ScriptsFor(UpsEventKind.GridLost)[0], "Manual run"));
        Assert.Equal((PowerAction.ShutDown, TimeSpan.FromSeconds(10), "Manual run"), Assert.Single(_countdown.Shown));
        _countdown.GoAhead();
        await Wait.Until(() => _power.Done.Count == 1);
    }

    [Fact]
    public async Task New_battery_low_limits_reach_the_monitor_and_are_saved()
    {
        var controller = await Start(s => s.BatteryLowMinutes = 0);
        Assert.Equal(new EventRules(20, 0), controller.Monitor!.Rules);

        await UiThread.Run(() => controller.Configuration.BatteryLowPercent.Text = "40");
        Assert.Equal(new EventRules(40, 0), controller.Monitor.Rules);
        Assert.Contains("\"BatteryLowPercent\": 40", File.ReadAllText(Path.Combine(_folder.Path, "settings.json")));

        await Online(45);
        _ups.Answer(false, 40);
        await UiUntil(() => _notes.All.Count == 2);
        Assert.Equal(["Grid lost — running on battery", "Battery low"], _notes.All.Select(n => n.Title));
    }

    [Fact]
    public async Task Grid_restored_without_a_running_chain_mentions_nothing_stopped()
    {
        await Start(s => s.ScriptsFor(UpsEventKind.GridLost).Add(Script("quick.ps1")));
        await Online();
        await GridLost();
        _ups.Answer(true);
        await UiUntil(() => _notes.All.Count == 2);
        Assert.Null(_notes.Notices[1].Footnote);
        Assert.Equal("Power is back after less than a minute on battery. Battery 80 %.", _notes.Notices[1].Body);
    }

    [Fact]
    public async Task A_waited_script_that_fails_later_gets_its_own_notification()
    {
        await Start(s => s.ScriptsFor(UpsEventKind.GridLost).AddRange([Script("save.ps1", wait: true), Script("hibernate.ps1")]));
        await Online();
        await GridLost();

        _scripts.Exit("save.ps1", code: 1);
        await UiUntil(() => _notes.All.Count == 2);

        Assert.Equal(new FakeNotifications.Shown("scripts", TrayState.Problem, "Grid lost: save.ps1 failed",
            "Exit code 1, so hibernate.ps1 didn't run.", null, "Bluetti EL100V2", true), _notes.All[1]);
        Assert.Contains(_log.Recent, e => e.Level == LogLevel.Warning && e.Message == "Grid lost: save.ps1 failed, so hibernate.ps1 didn't run");
        Assert.Equal(["save.ps1"], _scripts.Names);
    }

    [Fact]
    public async Task A_script_that_cannot_start_is_reported_in_the_event_notification_only()
    {
        _scripts.Broken.Add("gone.bat");
        await Start(s => s.ScriptsFor(UpsEventKind.GridLost).AddRange([Script("gone.bat"), Script("ok.ps1")]));
        await Online();
        await GridLost();
        await Task.Delay(100);

        Assert.Equal("Started ok.ps1; couldn't start gone.bat", Assert.Single(_notes.All).Footnote);
    }

    [Fact]
    public async Task With_its_notification_off_an_event_still_runs_its_scripts_silently()
    {
        await Start(s =>
        {
            s.Notifications[UpsEventKind.GridLost] = false;
            s.ScriptsFor(UpsEventKind.GridLost).Add(Script("save.ps1", wait: true));
        });
        await Online();
        _ups.Answer(false);
        await Wait.Until(() => _scripts.IsRunning("save.ps1"));

        _scripts.Exit("save.ps1", code: 3);
        await Task.Delay(200);

        Assert.Empty(_notes.All);
        Assert.Contains(_log.Recent, e => e.Level == LogLevel.Event && e.Message == "Grid lost");
    }

    [Fact]
    public async Task The_same_event_again_replaces_its_running_chain()
    {
        var controller = await Start(s => s.ScriptsFor(UpsEventKind.GridLost).AddRange([Script("wait.ps1", wait: true), Script("after.ps1")]));
        await UiThread.Run(() => controller.OnUpsEvent(UpsEventKind.GridLost, null));
        await UiThread.Run(() => controller.OnUpsEvent(UpsEventKind.GridLost, null));

        Assert.Contains(_log.Recent, e => e.Message == "Grid lost started again: stopped the Grid lost scripts; after.ps1 won't run");
        Assert.Equal(["wait.ps1", "wait.ps1"], _scripts.Names);
        Assert.Equal("The UPS switched to battery.", _notes.Notices[0].Body);
    }

    [Fact]
    public async Task Reachable_again_stops_the_unreachable_chain()
    {
        await Start(s =>
        {
            s.ScriptsFor(UpsEventKind.UpsUnreachable).AddRange([Script("grace.ps1", wait: true), Script("shutdown.ps1")]);
            s.ScriptsFor(UpsEventKind.UpsReachable).Add(Script("relax.ps1"));
        });
        await Online();
        _ups.Fail();
        _ups.Fail();
        _ups.Fail();
        await UiUntil(() => _notes.All.Count == 1);
        _ups.Answer(true);
        await UiUntil(() => _notes.All.Count == 2);

        Assert.Equal("Stopped the UPS unreachable scripts before shutdown.ps1; started relax.ps1", _notes.Notices[1].Footnote);
        Assert.Equal("The UPS is answering again. Grid online · Battery 80 %.", _notes.Notices[1].Body);
        Assert.Equal(["grace.ps1", "relax.ps1"], _scripts.Names);
    }

    [Fact]
    public async Task An_event_with_nothing_running_and_no_scripts_says_so_in_the_log()
    {
        var controller = await Start();
        await UiThread.Run(() => controller.OnUpsEvent(UpsEventKind.GridRestored, null));
        Assert.Contains(_log.Recent, e => e.Message == "Grid restored: no scripts to run");
        Assert.Null(_notes.Notices.Single().Footnote);
    }

    [Fact]
    public async Task Scripts_are_told_the_event_and_the_state_of_the_ups()
    {
        await Start(s =>
        {
            s.ScriptsFor(UpsEventKind.GridLost).Add(Script("lost.ps1"));
            s.ScriptsFor(UpsEventKind.GridRestored).Add(Script("back.ps1"));
            s.ScriptsFor(UpsEventKind.UpsUnreachable).Add(Script("gone.ps1"));
        });
        var t0 = DateTimeOffset.Now;
        await Online(at: t0);
        await GridLost(70, at: t0.AddMinutes(1));

        var lost = _scripts.Variables["lost.ps1"];
        Assert.Equal(("GridLost", null, "0", "1", "70", "358", "120"), (lost["UPS_EVENT"], lost["UPS_MANUAL"], lost["UPS_GRID"],
            lost["UPS_REACHABLE"], lost["UPS_BATTERY_PERCENT"], lost["UPS_RUNTIME_MINUTES"], lost["UPS_LOAD_W"]));
        Assert.Equal(("EL100V2", "2527008220279"), (lost["UPS_MODEL"], lost["UPS_SERIAL"]));
        Assert.Equal("0", lost["UPS_OUTAGE_SECONDS"]);

        _ups.Answer(true, 75, t0.AddMinutes(13.5));
        await UiUntil(() => _scripts.Variables.ContainsKey("back.ps1"), "Grid restored");
        var back = _scripts.Variables["back.ps1"];
        Assert.Equal(("GridRestored", "1", "750"), (back["UPS_EVENT"], back["UPS_GRID"], back["UPS_OUTAGE_SECONDS"]));

        _ups.Fail();
        _ups.Fail();
        _ups.Fail();
        await UiUntil(() => _scripts.Variables.ContainsKey("gone.ps1"), "UPS unreachable");
        var gone = _scripts.Variables["gone.ps1"];
        Assert.Equal(("UpsUnreachable", "0", "75"), (gone["UPS_EVENT"], gone["UPS_REACHABLE"], gone["UPS_BATTERY_PERCENT"])); // the last reading
        Assert.Null(gone["UPS_OUTAGE_SECONDS"]);
    }

    [Fact]
    public async Task On_battery_the_scripts_are_told_how_long_the_outage_has_lasted()
    {
        await Start(s => s.ScriptsFor(UpsEventKind.BatteryLow).Add(Script("low.ps1")));
        await Online();
        await GridLost(50);
        await Task.Delay(1100);
        _ups.Answer(false, 20);
        await UiUntil(() => _scripts.Variables.ContainsKey("low.ps1"), "Battery low");
        Assert.InRange(int.Parse(_scripts.Variables["low.ps1"]["UPS_OUTAGE_SECONDS"]!), 1, 10);
    }

    // ---- "Run now"

    [Fact]
    public async Task Run_now_tells_the_scripts_their_event_and_that_it_was_run_by_hand()
    {
        var controller = await Start(s => s.ScriptsFor(UpsEventKind.UpsReachable).AddRange([Script("a.ps1"), Script("b.ps1")]));
        await Online(66);

        await UiThread.Run(() => controller.RunScripts(controller.EventScripts.Single(g => g.Kind == UpsEventKind.UpsReachable), "Manual run"));
        await UiThread.Run(() => controller.RunScript(controller.Settings.ScriptsFor(UpsEventKind.UpsReachable)[1], "Manual run"));
        await Wait.Until(() => _scripts.Ran.Count == 3);

        foreach (var name in new[] { "a.ps1", "b.ps1" })
        {
            var variables = _scripts.Variables[name];
            Assert.Equal(("UpsReachable", "1", "1", "66"), (variables["UPS_EVENT"], variables["UPS_MANUAL"], variables["UPS_REACHABLE"], variables["UPS_BATTERY_PERCENT"]));
        }
    }

    [Fact]
    public async Task Without_monitoring_run_now_knows_nothing_about_the_ups()
    {
        var controller = await Start(s => s.ScriptsFor(UpsEventKind.GridLost).Add(Script("a.ps1")), monitor: false);
        await UiThread.Run(() => controller.RunScripts(controller.EventScripts[0], "Manual run"));
        var variables = _scripts.Variables["a.ps1"];
        Assert.Equal(("GridLost", "1"), (variables["UPS_EVENT"], variables["UPS_MANUAL"]));
        Assert.Null(variables["UPS_REACHABLE"]);
        Assert.Null(variables["UPS_BATTERY_PERCENT"]);
    }

    [Fact]
    public async Task Run_now_runs_an_events_enabled_scripts_without_a_notification()
    {
        var controller = await Start(s => s.ScriptsFor(UpsEventKind.GridLost).AddRange([Script("a.ps1"), Script("b.ps1", enabled: false)]), monitor: false);
        var group = controller.EventScripts.Single(g => g.Kind == UpsEventKind.GridLost);

        await UiThread.Run(() => controller.RunScripts(group, "Manual run of \"Grid lost\""));
        await Task.Delay(100);

        Assert.Equal([("a.ps1", "Manual run of \"Grid lost\"")], _scripts.Ran);
        Assert.Empty(_notes.All);
    }

    [Fact]
    public async Task Run_now_reports_what_went_wrong()
    {
        _scripts.Broken.Add("b.ps1");
        var controller = await Start(s => s.ScriptsFor(UpsEventKind.GridLost).AddRange([Script("a.ps1"), Script("b.ps1")]), monitor: false);

        await UiThread.Run(() => controller.RunScripts(controller.EventScripts[0], "Manual run"));
        await UiUntil(() => _notes.All.Count == 1);

        Assert.Equal(new FakeNotifications.Shown("scripts", TrayState.Problem, "Manual run: couldn't start b.ps1",
            "b.ps1: file not found.", null, "Bluetti EL100V2", true), _notes.All[0]);
    }

    [Fact]
    public async Task Run_now_on_one_script_waits_for_it_and_reports_its_failure()
    {
        var controller = await Start(s => s.ScriptsFor(UpsEventKind.GridLost).Add(Script("one.ps1", wait: true)), monitor: false);

        await UiThread.Run(() => controller.RunScript(controller.Settings.ScriptsFor(UpsEventKind.GridLost)[0], "Manual run"));
        _scripts.Exit("one.ps1", code: 5);
        await UiUntil(() => _notes.All.Count == 1);

        Assert.Equal("Manual run: one.ps1 failed", _notes.All[0].Title);
        Assert.Equal("Exit code 5.", _notes.All[0].Body);
    }

    // ---- pairing and configuration

    [Fact]
    public async Task Pairing_saves_the_ups_and_starts_monitoring()
    {
        var controller = await Start(paired: false);

        await UiThread.Run(() => controller.Pair(FakeUps.Paired));

        Assert.Equal(1, _ups.Connections);
        Assert.Contains("\"DriverId\": \"fake\"", File.ReadAllText(Path.Combine(_folder.Path, "settings.json")));
        Assert.True(await Ui(() => controller.Configuration.IsPaired));
        Assert.Contains(_log.Recent, e => e.Message == "Paired with FAKE123 (00:11:22:33:44:55)");
        await Online();
    }

    [Fact]
    public async Task Forgetting_stops_monitoring_and_clears_everything()
    {
        var controller = await Start();
        await Online();

        await UiThread.Run(controller.ForgetAsync);

        Assert.True(_ups.Disposed);
        Assert.Null(controller.Settings.Ups);
        Assert.Null(controller.Monitor);
        Assert.Contains("\"Ups\": null", File.ReadAllText(Path.Combine(_folder.Path, "settings.json")));
        Assert.Equal((TrayState.Neutral, "No UPS paired"), _tray.Current);
        Assert.Equal(LinkStatus.NotPaired, await Ui(() => controller.Dashboard.Link));
        Assert.Contains(_log.Recent, e => e.Message == "Forgot FAKE123");
    }

    [Fact]
    public async Task Starting_twice_keeps_one_monitor()
    {
        var controller = await Start();
        await UiThread.Run(controller.StartMonitoring);
        Assert.Equal(1, _ups.Connections);
    }

    [Fact]
    public async Task Stopping_and_starting_again_reconnects()
    {
        var controller = await Start();
        await Online();
        await UiThread.Run(controller.StopMonitoringAsync);
        await UiThread.Run(controller.StartMonitoring);
        Assert.Equal(2, _ups.Connections);
        Assert.Equal(LinkStatus.Connecting, await Ui(() => controller.Dashboard.Link));
    }

    [Fact]
    public async Task A_new_update_period_reaches_the_monitor()
    {
        var controller = await Start();
        await UiThread.Run(() => controller.Configuration.UpdatePeriod.Text = "45");
        Assert.Equal(TimeSpan.FromSeconds(45), controller.Monitor!.Period);
        Assert.Contains("\"UpdatePeriodSeconds\": 45", File.ReadAllText(Path.Combine(_folder.Path, "settings.json")));
    }

    [Fact]
    public async Task Resuming_from_sleep_reconnects()
    {
        var controller = await Start();
        await UiThread.Run(controller.OnResumedFromSleep);
        Assert.Equal(1, _ups.Resets);
        Assert.Contains(_log.Recent, e => e.Message == "PC resumed from sleep; reconnecting to the UPS");
    }

    [Fact]
    public async Task The_test_notification_is_a_green_one()
    {
        var controller = await Start(monitor: false);
        await UiThread.Run(controller.SendTestNotification);
        Assert.Equal(new FakeNotifications.Shown("test", TrayState.GridOnline, "Test notification",
            "This is how UPS Tools tells you that the grid went down or came back.", null, "Bluetti EL100V2", false), _notes.All.Single());
        Assert.Contains(_log.Recent, e => e.Message == "Sent a test notification");
    }

    [Fact]
    public async Task Log_entries_show_up_in_recent_activity_newest_first()
    {
        _log.Info("before");
        var controller = await Start(monitor: false);
        _log.Warn("after");
        await UiUntil(() => controller.Dashboard.Activity.Count == 2);
        Assert.Equal(["after", "before"], await Ui(() => controller.Dashboard.Activity.Select(a => a.Message).ToList()));
    }

    [Fact]
    public async Task Script_edits_are_saved()
    {
        var controller = await Start(monitor: false);
        await UiThread.Run(() => controller.EventScripts[1].Add(@"C:\scripts\wake.ahk"));
        Assert.Contains(@"wake.ahk", File.ReadAllText(Path.Combine(_folder.Path, "settings.json")));
        Assert.Equal(Path.Combine(_folder.Path), controller.SettingsFolder);
    }
}
