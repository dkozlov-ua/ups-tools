using System.Globalization;
using UpsTools.Core;
using UpsTools.Droid.Tests.Support;
using UpsTools.Services;
using UpsTools.ViewModels;

namespace UpsTools.Droid.Tests;

/// <summary>The phone's controller with a scripted UPS: pairing, readings, events and their notifications, settings.</summary>
public sealed class ControllerTests : IDisposable
{
    private readonly TestUiThread _ui = new();
    private readonly TempFolder _folder = new();
    private readonly FakeUps _ups = new();
    private readonly FakeNotifications _notifications = new();
    private MonitorController? _controller;

    public ControllerTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    public void Dispose()
    {
        if (_controller is { } controller) _ui.Run(controller.StopAsync).Wait(TimeSpan.FromSeconds(10));
        _ui.Dispose();
        _folder.Dispose();
    }

    private async Task<MonitorController> Start(AppSettings? settings = null)
    {
        var log = new AppLog(_folder.Path);
        if (settings is not null) new SettingsStore(log, _folder.Path).Save(settings);
        _controller = await _ui.Run(() =>
        {
            var controller = new MonitorController(_ui, log, [_ups], "Phone", notifications: _notifications);
            controller.Settings.UpdatePeriodSeconds = 0; // the fake UPS sets the pace
            controller.Start();
            return controller;
        });
        return _controller;
    }

    private async Task Until(Func<bool> condition, string what, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await _ui.Run(condition))
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what}");
            await Task.Delay(20);
        }
    }

    private static AppSettings OneUps(Action<UpsSettings>? setup = null)
    {
        var ups = new UpsSettings { Device = FakeUps.Paired };
        setup?.Invoke(ups);
        return new AppSettings { Upses = [ups] };
    }

    [Fact]
    public async Task A_paired_ups_is_monitored_and_kept()
    {
        var controller = await Start();
        Assert.Equal("No UPS paired", controller.Status.Title);

        var unit = await _ui.Run(() => controller.PairAsync(FakeUps.Paired));
        _ups.Answer(true, 80);
        await Until(() => unit.Dashboard.BatteryValue == "80 %" && unit.Dashboard.Link == LinkStatus.Online, "the first reading");

        var status = await _ui.Run(() => controller.Status);
        Assert.Equal((TrayState.GridOnline, "Grid online"), (status.State, status.Title));
        Assert.Equal("Grid online · 80 % · 120 W load", Assert.Single(status.Lines));

        var saved = new SettingsStore(new AppLog(_folder.Path), _folder.Path).Load();
        Assert.Equal(FakeUps.Paired, Assert.Single(saved.Upses).Device);
    }

    [Fact]
    public async Task An_outage_notifies_and_the_grid_coming_back_replaces_it()
    {
        var controller = await Start(OneUps());
        var unit = controller.Upses[0];
        _ups.Answer(true, 80);
        _ups.Answer(false, 79);
        await Until(() => !_notifications.Shown.IsEmpty, "the Grid lost notification");

        var (lost, ups, source) = _notifications.Shown.Single();
        Assert.Equal(("Grid lost — running on battery", "Bluetti EL100V2", unit.Source), (lost.Title, ups, source));
        Assert.Equal(TrayState.OnBattery, (await _ui.Run(() => controller.Status)).State);

        _ups.Answer(true, 79);
        await Until(() => _notifications.Shown.Count == 2, "the Grid restored notification");
        var restored = _notifications.Shown.Last().Notice;
        Assert.Equal("Grid restored", restored.Title);
        Assert.Equal(lost.Tag, restored.Tag); // replaces it
        Assert.StartsWith("Power is back after less than a minute on battery", restored.Body);
    }

    [Fact]
    public async Task An_event_turned_off_neither_notifies_nor_colours_the_status()
    {
        var controller = await Start(OneUps(ups =>
        {
            ups.Notifications[UpsEventKind.GridLost] = false;
            ups.StatusEvents[UpsEventKind.GridLost] = false;
        }));
        _ups.Answer(true, 80);
        _ups.Answer(false, 79);
        await Until(() => controller.Activity.Any(e => e.Message == "Grid lost"), "Grid lost");

        Assert.Empty(_notifications.Shown);
        var status = await _ui.Run(() => controller.Status);
        Assert.Equal(TrayState.GridOnline, status.State);
        Assert.StartsWith("On battery · 79 %", status.Lines[0]); // the line still says what it is
    }

    [Fact]
    public async Task A_ups_that_stops_answering_notifies_and_turns_the_status_red()
    {
        var controller = await Start(OneUps());
        _ups.Answer(true, 80);
        for (var i = 0; i < 3; i++) _ups.Fail();
        await Until(() => _notifications.Shown.Any(n => n.Notice.Kind == UpsEventKind.UpsUnreachable), "UPS unreachable", seconds: 30);

        var status = await _ui.Run(() => controller.Status);
        Assert.Equal((TrayState.Problem, "Needs attention", "UPS unreachable"), (status.State, status.Title, status.Lines[0]));
    }

    [Fact]
    public async Task With_several_each_line_is_named_and_a_name_given_is_used()
    {
        var second = FakeUps.Paired with { Address = "00:11:22:33:44:66", Name = "FAKE456" };
        var settings = OneUps();
        settings.Upses.Add(new UpsSettings { Device = second });
        var controller = await Start(settings);

        var titles = await _ui.Run(() => controller.Upses.Select(u => u.Title).ToList());
        Assert.Equal(["Bluetti EL100V2 (FAKE123)", "Bluetti EL100V2 (FAKE456)"], titles);

        await _ui.Run(() => controller.Rename(controller.Upses[1], "  Server rack "));
        Assert.Equal("Server rack", controller.Settings.Upses[1].Label);
        Assert.Equal(["Bluetti EL100V2", "Server rack"], await _ui.Run(() => controller.Upses.Select(u => u.Title).ToList()));

        await _ui.Run(() => controller.Settings.Upses[0].ShowInStatus = false);
        var lines = (await _ui.Run(() => controller.Status)).Lines;
        Assert.StartsWith("Server rack: ", Assert.Single(lines));
    }

    [Fact]
    public async Task Forgetting_the_ups_stops_it_and_removes_it_from_the_settings()
    {
        var controller = await Start(OneUps());
        await _ui.Run(() => controller.ForgetAsync(controller.Upses[0]));

        Assert.Empty(controller.Upses);
        Assert.Empty(new SettingsStore(new AppLog(_folder.Path), _folder.Path).Load().Upses);
    }

    [Fact]
    public void Settings_out_of_range_are_brought_in_and_a_broken_file_is_kept()
    {
        var log = new AppLog(_folder.Path);
        var store = new SettingsStore(log, _folder.Path);
        var settings = OneUps(ups => ups.BatteryLowPercent = 500);
        settings.UpdatePeriodSeconds = 1;
        settings.Sharing.Code = "abcd efgh jkmn pqrs";
        store.Save(settings);

        var loaded = store.Load();
        Assert.Equal((AppSettings.MaxBatteryLowPercent, AppSettings.MinUpdatePeriodSeconds, "ABCD-EFGH-JKMN-PQRS"),
            (loaded.Upses[0].BatteryLowPercent, loaded.UpdatePeriodSeconds, loaded.Sharing.Code));
        Assert.True(loaded.KeepAwake && loaded.StartAtBoot);

        File.WriteAllText(store.FilePath, "{ not json");
        Assert.Empty(store.Load().Upses);
        Assert.True(File.Exists(store.FilePath + ".broken"));
    }
}
