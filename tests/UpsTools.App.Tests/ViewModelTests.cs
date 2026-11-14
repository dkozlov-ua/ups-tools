using System.ComponentModel;
using System.Globalization;
using UpsTools.AppTests.Support;
using UpsTools.Core;
using UpsTools.Services;
using UpsTools.ViewModels;

namespace UpsTools.AppTests;

public class DashboardViewModelTests
{
    private readonly DashboardViewModel _vm = new();

    public DashboardViewModelTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    private static UpsStatus Status => new()
    {
        Timestamp = new DateTimeOffset(2026, 11, 5, 14, 3, 9, TimeSpan.Zero).ToLocalTime(),
        GridPresent = true,
        BatteryPercent = 80,
        CapacityWh = 1024,
        AcInputVoltage = 236.6,
        AcOutputVoltage = 236.1,
        AcInputW = 200,
        DcInputW = 0,
        AcOutputW = 150,
        DcOutputW = 7,
    };

    [Fact]
    public void Starts_unpaired_with_no_readings()
    {
        Assert.Equal(("No UPS paired", "Open Configuration → Add UPS to pair one.", false), (_vm.DeviceTitle, _vm.DeviceSubtitle, _vm.IsPaired));
        Assert.Equal((LinkStatus.NotPaired, "Not paired", true), (_vm.Link, _vm.LinkText, _vm.IsStale));
        Assert.Equal("—", _vm.GridValue);
        Assert.Equal((TrayState.Neutral, "No UPS paired"), (_vm.State, _vm.Summary));
    }

    [Fact]
    public void The_device_header_shows_the_model_address_and_serial()
    {
        var ups = new PairedUps("bluetti-ble", "0C:4E:A0:E0:81:E6", "EL100V2…", "EL100V2");
        _vm.SetDevice(ups);
        Assert.Equal(("Bluetti EL100V2", "0C:4E:A0:E0:81:E6", true), (_vm.DeviceTitle, _vm.DeviceSubtitle, _vm.IsPaired));
        _vm.SetDevice(ups, "2527008220279");
        Assert.Equal("Serial 2527008220279 · 0C:4E:A0:E0:81:E6", _vm.DeviceSubtitle);
    }

    [Fact]
    public void A_ups_with_a_name_of_its_own_shows_its_model_under_it()
    {
        var ups = new PairedUps("bluetti-ble", "0C:4E:A0:E0:81:E6", "EL100V2…", "EL100V2");
        _vm.SetDevice(ups, "2527008220279", "Office");
        Assert.Equal(("Office", "Bluetti EL100V2 · Serial 2527008220279 · 0C:4E:A0:E0:81:E6"), (_vm.DeviceTitle, _vm.DeviceSubtitle));
        _vm.SetDevice(ups, title: "Desk"); // the serial stays known
        Assert.Equal(("Desk", "Bluetti EL100V2 · Serial 2527008220279 · 0C:4E:A0:E0:81:E6"), (_vm.DeviceTitle, _vm.DeviceSubtitle));
    }

    [Fact]
    public void The_summary_says_what_the_tray_says()
    {
        _vm.SetDevice(new PairedUps("x", "a", "n", "EL100V2"));
        _vm.SetLink(LinkStatus.Connecting);
        Assert.Equal((TrayState.Neutral, "Connecting to the UPS…"), (_vm.State, _vm.Summary));
        _vm.SetLink(LinkStatus.Online);
        _vm.Apply(Status with { GridPresent = false });
        Assert.Equal(TrayState.OnBattery, _vm.State);
        Assert.StartsWith("On battery · ", _vm.Summary);
        _vm.SetLink(LinkStatus.Retrying); // still the last reading
        Assert.Equal(TrayState.OnBattery, _vm.State);
        _vm.SetLink(LinkStatus.Unreachable);
        Assert.Equal((TrayState.Problem, "UPS unreachable"), (_vm.State, _vm.Summary));
    }

    [Theory]
    [InlineData(LinkStatus.NotPaired, "Not paired", true)]
    [InlineData(LinkStatus.Connecting, "Connecting…", true)]
    [InlineData(LinkStatus.Online, "Connected", false)]
    [InlineData(LinkStatus.Retrying, "Reconnecting…", true)]
    [InlineData(LinkStatus.Unreachable, "Unreachable", true)]
    [InlineData((LinkStatus)99, "", true)]
    public void Link_states_have_a_label_and_only_online_is_fresh(LinkStatus link, string text, bool stale)
    {
        _vm.SetLink(link);
        Assert.Equal((link, text, stale), (_vm.Link, _vm.LinkText, _vm.IsStale));
    }

    [Fact]
    public void A_reading_on_grid_fills_every_card()
    {
        _vm.Apply(Status);

        Assert.Equal($"Updated {Status.Timestamp.LocalDateTime:HH:mm:ss}", _vm.LastUpdated);
        Assert.Equal((Tone.Good, "Online", "236.6 V at the UPS input"), (_vm.GridTone, _vm.GridValue, _vm.GridDetail));
        Assert.Equal((Tone.Good, 80.0, "80 %", "≈ 0.82 kWh of 1.02 kWh stored"), (_vm.BatteryTone, _vm.BatteryPercent, _vm.BatteryValue, _vm.BatteryDetail));
        Assert.Equal((Tone.Good, "+43 W", "Charging"), (_vm.FlowTone, _vm.FlowValue, _vm.FlowDetail));
        Assert.Equal(("5 h 13 m", "Estimated at the current 157 W load"), (_vm.RuntimeValue, _vm.RuntimeDetail));
        Assert.Equal(("200 W", "AC 200 W · DC 0 W"), (_vm.InputValue, _vm.InputDetail));
        Assert.Equal(("157 W", "AC 150 W · DC 7 W · 236.1 V"), (_vm.OutputValue, _vm.OutputDetail));
    }

    [Fact]
    public void A_reading_on_battery()
    {
        _vm.Apply(Status with { GridPresent = false, BatteryPercent = 35, AcInputW = 0, AverageOutputW = 160 });

        Assert.Equal((Tone.Bad, "Lost", "Running on battery"), (_vm.GridTone, _vm.GridValue, _vm.GridDetail));
        Assert.Equal(Tone.Warning, _vm.BatteryTone);
        Assert.Equal((Tone.Warning, "−157 W", "Discharging"), (_vm.FlowTone, _vm.FlowValue, _vm.FlowDetail));
        // 35 % of 1024 Wh at 160 W
        Assert.Equal(("2 h 14 m", "At 160 W, the average load of the last 5 minutes"), (_vm.RuntimeValue, _vm.RuntimeDetail));
    }

    [Theory]
    [InlineData(19, Tone.Bad)]
    [InlineData(20, Tone.Warning)]
    [InlineData(49, Tone.Warning)]
    [InlineData(50, Tone.Good)]
    [InlineData(null, Tone.Neutral)]
    public void Battery_colour_follows_the_charge(int? soc, Tone tone)
    {
        _vm.Apply(Status with { BatteryPercent = soc });
        Assert.Equal(tone, _vm.BatteryTone);
        if (soc is null) Assert.Equal(("—", 0.0, ""), (_vm.BatteryValue, _vm.BatteryPercent, _vm.BatteryDetail));
    }

    [Theory]
    [InlineData(105, 100, Tone.Neutral, "0 W")]   // within ±5 W: idle
    [InlineData(100, 105, Tone.Neutral, "0 W")]
    [InlineData(106, 100, Tone.Good, "+6 W")]
    [InlineData(100, 106, Tone.Warning, "−6 W")]
    public void Small_flows_count_as_idle(double input, double output, Tone tone, string value)
    {
        _vm.Apply(Status with { AcInputW = input, DcInputW = 0, AcOutputW = output, DcOutputW = 0 });
        Assert.Equal((tone, value), (_vm.FlowTone, _vm.FlowValue));
        if (tone == Tone.Neutral) Assert.Equal("Idle (battery neither charging nor discharging)", _vm.FlowDetail);
    }

    [Fact]
    public void A_sparse_reading_shows_dashes()
    {
        _vm.Apply(new UpsStatus { Timestamp = DateTimeOffset.Now });

        Assert.Equal((Tone.Neutral, "—", "Unknown"), (_vm.GridTone, _vm.GridValue, _vm.GridDetail));
        Assert.Equal((Tone.Neutral, "—", ""), (_vm.FlowTone, _vm.FlowValue, _vm.FlowDetail));
        Assert.Equal(("—", "Not enough data"), (_vm.RuntimeValue, _vm.RuntimeDetail));
        Assert.Equal(("—", "AC — · DC —"), (_vm.InputValue, _vm.InputDetail));
        Assert.Equal(("—", "AC — · DC —"), (_vm.OutputValue, _vm.OutputDetail));
    }

    [Fact]
    public void No_load_has_no_runtime_estimate()
    {
        _vm.Apply(Status with { AcOutputW = 0, DcOutputW = 0 });
        Assert.Equal(("—", "No load"), (_vm.RuntimeValue, _vm.RuntimeDetail));
        _vm.Apply(Status with { GridPresent = true, AcInputVoltage = null });
        Assert.Equal("Mains power present", _vm.GridDetail);
    }

    [Fact]
    public void Forgetting_the_device_clears_the_readings()
    {
        _vm.SetDevice(new PairedUps("x", "a", "n", "EL100V2"));
        _vm.SetLink(LinkStatus.Online);
        _vm.Apply(Status);

        _vm.SetDevice(null);

        Assert.Equal((LinkStatus.NotPaired, "", "—", "—", "—", "—", "—", "—"),
            (_vm.Link, _vm.LastUpdated, _vm.GridValue, _vm.BatteryValue, _vm.FlowValue, _vm.RuntimeValue, _vm.InputValue, _vm.OutputValue));
        Assert.Equal(0, _vm.BatteryPercent);
        Assert.False(_vm.IsPaired);
    }

    [Fact]
    public void Activity_shows_the_newest_hundred_first()
    {
        var activity = new ActivityFeed();
        for (var i = 0; i < 105; i++) activity.Post(new LogEntry(DateTime.Now, LogLevel.Info, $"#{i}"));
        Assert.Equal(100, activity.Count);
        Assert.Equal("#104", activity[0].Message);
        Assert.Equal("#5", activity[^1].Message);
    }

    [Fact]
    public void Changes_are_announced_once()
    {
        var changed = new List<string?>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _vm.SetLink(LinkStatus.Online);
        _vm.SetLink(LinkStatus.Online);
        Assert.Equal(["Link", "LinkText", "IsStale", "Summary"], changed);
    }
}

[Collection("Registry")]
public sealed class ConfigurationViewModelTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly AppSettings _settings = new();
    private readonly UpsSettings _ups = new() { Device = new PairedUps("bluetti-ble", "0C:4E:A0:E0:81:E6", "EL100V22527008220279", "EL100V2") };
    private readonly AppLog _log;
    private int _monitorChanges;
    private int _saves;
    private int _renames;
    private readonly ConfigurationViewModel _vm;
    private readonly UpsConfigurationViewModel _upsVm;

    public ConfigurationViewModelTests()
    {
        StartupRegistration.SetEnabled(false);
        _log = new AppLog(_folder.Path);
        _vm = new ConfigurationViewModel(_settings, _log, () => _saves++, () => _monitorChanges++);
        _upsVm = new UpsConfigurationViewModel(_ups, () => _saves++, () => _monitorChanges++, () => _renames++);
    }

    public void Dispose()
    {
        StartupRegistration.SetEnabled(false);
        _folder.Dispose();
    }

    [Fact]
    public void Shows_the_settings_and_the_version()
    {
        Assert.True(_vm.CloseToTray);
        Assert.False(_vm.StartWithWindows);
        Assert.Equal("60", _vm.UpdatePeriod.Text);
        Assert.Equal(("20", "10", "0", "0"), (_upsVm.BatteryLowPercent.Text, _upsVm.BatteryLowMinutes.Text, _upsVm.GridLostDelay.Text, _upsVm.UnreachableOnBattery.Text));
        Assert.Matches(@"^Version \d+\.\d+\.\d+$", _vm.Version);
        Assert.Equal(["Grid lost", "Battery low", "Grid restored", "UPS unreachable", "UPS reachable again"], _upsVm.Notifications.Select(n => n.Title));
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void The_ups_is_described()
    {
        Assert.Equal(("Bluetti EL100V2", "EL100V22527008220279 · 0C:4E:A0:E0:81:E6 · via Bluetooth LE", ""),
            (_upsVm.DefaultTitle, _upsVm.Detail, _upsVm.Label));
    }

    [Theory]
    [InlineData(" Office ", "Office")]
    [InlineData("   ", null)]
    [InlineData("", null)]
    public void The_ups_name_is_saved_trimmed(string typed, string? saved)
    {
        _ups.Label = "Old";
        _upsVm.Label = typed;
        Assert.Equal(saved, _ups.Label);
        Assert.Equal(saved ?? "", _upsVm.Label);
        Assert.Equal((1, 1, 0), (_saves, _renames, _monitorChanges));

        _upsVm.Label = typed; // unchanged
        Assert.Equal((1, 1), (_saves, _renames));
    }

    [Fact]
    public void Close_to_tray_saves_when_it_changes()
    {
        _vm.CloseToTray = true;
        Assert.Equal(0, _saves);
        _vm.CloseToTray = false;
        Assert.False(_settings.CloseToTray);
        Assert.Equal(1, _saves);
    }

    [Fact]
    public void Start_with_windows_writes_the_run_entry()
    {
        _vm.StartWithWindows = true;
        Assert.True(StartupRegistration.IsEnabled);
        Assert.True(new ConfigurationViewModel(_settings, _log, () => { }, () => { }).StartWithWindows);

        _vm.StartWithWindows = false;
        Assert.False(StartupRegistration.IsEnabled);
        Assert.Equal(0, _saves); // not a setting: Windows keeps it
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("1", 2)]
    [InlineData("0", 2)]
    [InlineData("9999", 600)]
    public void The_update_period_applies_clamped_when_it_parses(string text, int seconds)
    {
        _vm.UpdatePeriod.Text = text;

        Assert.Equal(text, _vm.UpdatePeriod.Text); // what was typed stays until the box loses focus
        Assert.Equal(seconds, _settings.UpdatePeriodSeconds);
        Assert.Equal(1, _monitorChanges);
        Assert.Equal(1, _saves);

        _vm.UpdatePeriod.Normalize();
        Assert.Equal(seconds.ToString(), _vm.UpdatePeriod.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("60")] // unchanged
    public void An_update_period_that_does_not_change_anything_is_ignored(string text)
    {
        _vm.UpdatePeriod.Text = text;
        Assert.Equal(60, _settings.UpdatePeriodSeconds);
        Assert.Equal(0, _monitorChanges);
        Assert.Equal(0, _saves);
        _vm.UpdatePeriod.Normalize();
        Assert.Equal("60", _vm.UpdatePeriod.Text);
    }

    [Theory]
    [InlineData("35", 35)]
    [InlineData("0", 0)]
    [InlineData("-4", 0)]
    [InlineData("100", 99)]
    public void The_battery_low_charge_applies_clamped(string text, int percent)
    {
        _upsVm.BatteryLowPercent.Text = text;
        Assert.Equal(percent, _ups.BatteryLowPercent);
        Assert.Equal((1, 1), (_saves, _monitorChanges));
        _upsVm.BatteryLowPercent.Normalize();
        Assert.Equal(percent.ToString(), _upsVm.BatteryLowPercent.Text);
        Assert.Equal((0, 99, percent), (_upsVm.BatteryLowPercent.Min, _upsVm.BatteryLowPercent.Max, _upsVm.BatteryLowPercent.Value));
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("1", 1)]
    [InlineData("1000", 600)]
    public void The_grid_lost_delay_applies_clamped(string text, int seconds)
    {
        _upsVm.GridLostDelay.Text = text;
        Assert.Equal(seconds, _ups.GridLostDelaySeconds);
        Assert.Equal((1, 1), (_saves, _monitorChanges));
        _upsVm.GridLostDelay.Normalize();
        Assert.Equal(seconds.ToString(), _upsVm.GridLostDelay.Text);
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("1000", 600)]
    public void The_unreachable_on_battery_limit_applies_clamped(string text, int minutes)
    {
        _upsVm.UnreachableOnBattery.Text = text;
        Assert.Equal(minutes, _ups.UnreachableOnBatteryMinutes);
        Assert.Equal((1, 1), (_saves, _monitorChanges));
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("0", 0)]
    [InlineData("1000", 600)]
    public void The_battery_low_time_left_applies_clamped(string text, int minutes)
    {
        _upsVm.BatteryLowMinutes.Text = text;
        Assert.Equal(minutes, _ups.BatteryLowMinutes);
        Assert.Equal((1, 1), (_saves, _monitorChanges));
        _upsVm.BatteryLowMinutes.Text = "x";
        _upsVm.BatteryLowMinutes.Normalize();
        Assert.Equal(minutes.ToString(), _upsVm.BatteryLowMinutes.Text);
        Assert.Equal(1, _saves);
    }

    [Fact]
    public void Notification_switches_save_per_event()
    {
        var reachable = _upsVm.Notifications.Single(n => n.Kind == UpsEventKind.UpsReachable);
        var changed = new List<string?>();
        reachable.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        reachable.IsEnabled = true; // already on
        reachable.IsEnabled = false;

        Assert.False(_ups.NotifyOn(UpsEventKind.UpsReachable));
        Assert.True(_ups.NotifyOn(UpsEventKind.GridLost));
        Assert.Equal(["IsEnabled"], changed);
        Assert.Equal(1, _saves);
    }
}

/// <summary>The Scripts tab's view models. Edits write through to the settings.</summary>
[Collection("Registry")]
public sealed class ScriptsViewModelTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly List<ScriptAction> _model = [];
    private readonly ElevatedTasks _elevated;
    private int _saves;
    private readonly EventScriptsViewModel _group;

    public ScriptsViewModelTests()
    {
        TaskFolder.Delete();
        _elevated = new ElevatedTasks(new AppLog(_folder.Path));
        _group = new EventScriptsViewModel(UpsEventKind.GridLost, _model, _elevated, () => _saves++);
    }

    public void Dispose()
    {
        TaskFolder.Delete();
        _folder.Dispose();
    }

    private string Script(string name) => _folder.File(name, "exit 0");

    [Fact]
    public void A_group_describes_its_event()
    {
        Assert.Equal("Grid lost", _group.Title);
        Assert.Equal(UpsEventKind.GridLost.Description(), _group.Description);
        Assert.True(_group.IsEmpty);
    }

    [Fact]
    public void Existing_scripts_are_listed_in_order()
    {
        _model.Add(new ScriptAction { Path = @"C:\a.ps1" });
        _model.Add(new ScriptAction { Path = @"C:\b.ps1", Enabled = false });
        var group = new EventScriptsViewModel(UpsEventKind.GridLost, _model, _elevated, () => { });

        Assert.Equal(["a.ps1", "b.ps1"], group.Items.Select(i => i.FileName));
        Assert.Equal([@"C:\a.ps1"], group.EnabledActions.Select(a => a.Path));
        Assert.False(group.IsEmpty);
    }

    [Fact]
    public void Adding_a_script_saves_it_enabled()
    {
        var changed = new List<string?>();
        _group.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var path = Script("pause.ahk");

        _group.Add(path);

        var item = Assert.Single(_group.Items);
        Assert.Equal(("pause.ahk", path, true, true), (item.FileName, item.FullPath, item.Exists, item.Enabled));
        Assert.Same(item.Model, Assert.Single(_model));
        Assert.Equal(1, _saves);
        Assert.Contains("IsEmpty", changed);
    }

    [Fact]
    public void Adding_a_built_in_action_gives_it_a_minute_of_countdown()
    {
        _group.Add(PowerAction.Hibernate);

        var item = Assert.Single(_group.Items);
        Assert.Equal(("Hibernate", PowerAction.Hibernate.Description(), true, true), (item.FileName, item.FullPath, item.Exists, item.IsPower));
        Assert.Equal(("60", 60), (item.Countdown.Text, item.Model.CountdownSeconds));
        Assert.Equal("", item.Model.Path);
        Assert.False(item.CanRepair);
        Assert.Equal(1, _saves);
        Assert.Equal([PowerAction.Hibernate], _group.EnabledActions.Select(a => a.Power));
    }

    [Theory]
    [InlineData("15", 15)]
    [InlineData("0", 0)]
    [InlineData("9999", 600)]
    public void The_countdown_of_a_built_in_action_saves_clamped(string text, int seconds)
    {
        _group.Add(PowerAction.ShutDown);
        var item = _group.Items[0];

        item.Countdown.Text = text;
        item.Countdown.Normalize();

        Assert.Equal(seconds, item.Model.CountdownSeconds);
        Assert.Equal(seconds.ToString(), item.Countdown.Text);
        Assert.Equal(2, _saves);
    }

    [Theory]
    [InlineData("15", 15)]
    [InlineData("99999", 1440)]
    public void The_time_limit_of_a_waited_script_saves_clamped(string text, int minutes)
    {
        _group.Add(Script("slow.ps1"));
        var item = _group.Items[0];
        Assert.Equal("0", item.WaitLimit.Text);

        item.WaitLimit.Text = text;
        item.WaitLimit.Normalize();

        Assert.Equal(minutes, item.Model.WaitLimitMinutes);
        Assert.Equal(minutes.ToString(), item.WaitLimit.Text);
        Assert.Equal(2, _saves);
    }

    [Fact]
    public void A_script_is_not_a_built_in_action()
    {
        _group.Add(Script("a.ps1"));
        Assert.False(_group.Items[0].IsPower);
    }

    [Fact]
    public void A_missing_file_is_flagged()
    {
        _group.Add(@"C:\nowhere\gone.bat");
        Assert.False(_group.Items[0].Exists);
    }

    [Fact]
    public async Task Removing_a_script_saves()
    {
        _group.Add(Script("a.ps1"));
        _group.Add(Script("b.ps1"));

        Assert.Null(await _group.RemoveAsync(_group.Items[0]));

        Assert.Equal(["b.ps1"], _group.Items.Select(i => i.FileName));
        Assert.Equal(["b.ps1"], _model.Select(a => Path.GetFileName(a.Path)));
        Assert.Equal(3, _saves);
    }

    [Theory]
    [InlineData(0, 2, "b,c,a")]
    [InlineData(2, 0, "c,a,b")]
    [InlineData(1, 99, "a,c,b")]  // past the end: last
    [InlineData(1, -5, "b,a,c")]  // before the start: first
    [InlineData(1, 1, "a,b,c")]
    public void Moving_reorders_the_list_and_the_settings(int from, int to, string expected)
    {
        foreach (var name in new[] { "a", "b", "c" }) _group.Add(Script(name));
        _saves = 0;

        _group.Move(_group.Items[from], to);

        Assert.Equal(expected, string.Join(",", _group.Items.Select(i => i.FileName)));
        Assert.Equal(expected, string.Join(",", _model.Select(a => Path.GetFileName(a.Path))));
        Assert.Equal(from == Math.Clamp(to, 0, 2) ? 0 : 1, _saves);
    }

    [Fact]
    public void Moving_a_script_of_another_event_does_nothing()
    {
        _group.Add(Script("a"));
        var other = new ScriptItemViewModel(new ScriptAction { Path = "x" }, _elevated, () => { });
        _group.Move(other, 0);
        Assert.Single(_group.Items);
    }

    [Fact]
    public void Script_options_save_when_they_change()
    {
        _group.Add(Script("a.ps1"));
        var item = _group.Items[0];
        _saves = 0;
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.Enabled = true;
        item.Enabled = false;
        item.WaitForExit = false;
        item.WaitForExit = true;
        item.Arguments = "  --now  ";
        item.Arguments = "--now";

        Assert.Equal((false, true, "--now"), (item.Model.Enabled, item.Model.WaitForExit, item.Model.Arguments));
        Assert.Equal(["Enabled", "WaitForExit", "Arguments"], changed);
        Assert.Equal(3, _saves);
        Assert.Empty(_group.EnabledActions);
    }

    [Fact]
    public void A_script_set_to_run_as_administrator_without_its_task_offers_a_repair()
    {
        var item = new ScriptItemViewModel(new ScriptAction { Path = Script("a.ps1"), RunElevated = true }, _elevated, () => { });

        Assert.True(item.CanRepair);
        Assert.True(item.HasElevationNote);
        Assert.Equal("Can't run as administrator: its administrator task is missing.", item.ElevationNote);
        Assert.True(item.IsIdle);
    }

    [Fact]
    public void A_normal_script_has_no_note()
    {
        var item = new ScriptItemViewModel(new ScriptAction { Path = Script("a.ps1") }, _elevated, () => { });
        Assert.False(item.CanRepair);
        Assert.Null(item.ElevationNote);
        Assert.False(item.HasElevationNote);
    }

    [Fact]
    public async Task Repairing_a_normal_script_does_nothing()
    {
        var item = new ScriptItemViewModel(new ScriptAction { Path = Script("a.ps1") }, _elevated, () => { });
        await item.RepairAsync();
        Assert.False(TaskFolder.Exists());
    }

    [Fact]
    public async Task Setting_the_same_value_undoes_the_checkbox_without_work()
    {
        var item = new ScriptItemViewModel(new ScriptAction { Path = Script("a.ps1") }, _elevated, () => _saves++);
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.RunElevated = false;
        await Task.Delay(50);

        Assert.Equal(["RunElevated"], changed);
        Assert.Equal(0, _saves);
    }

    [AdminFact]
    public async Task Ticking_run_as_administrator_registers_the_task_and_unticking_deletes_it()
    {
        _group.Add(Script("admin.ps1"));
        var item = _group.Items[0];
        var idle = new List<bool>();
        item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(item.IsIdle)) idle.Add(item.IsIdle); };
        _saves = 0;

        item.RunElevated = true;
        Assert.False(item.IsIdle);
        item.RunElevated = false; // ignored while busy
        await Wait.Until(() => item.IsIdle, 30, "the task to be registered");

        Assert.True(item.RunElevated);
        Assert.Equal([ElevatedTasks.TaskName(item.Model)], TaskFolder.Tasks());
        Assert.Null(item.ElevationNote);
        Assert.Equal(1, _saves);
        Assert.Equal([false, true], idle);

        item.Arguments = "--changed"; // the task carries the arguments: registered again
        await Wait.Until(() => item.IsIdle && _elevated.Problem(item.Model) is null, 30, "the task to be updated");

        item.RunElevated = false;
        await Wait.Until(() => item.IsIdle && !item.RunElevated, 30, "the task to be deleted");
        Assert.Empty(TaskFolder.Tasks());
    }

    [AdminFact]
    public async Task Repair_registers_a_missing_task()
    {
        var item = new ScriptItemViewModel(new ScriptAction { Path = Script("a.ps1"), RunElevated = true }, _elevated, () => { });
        Assert.True(item.CanRepair);

        await item.RepairAsync();

        Assert.False(item.CanRepair);
        Assert.Null(item.ElevationNote);
        Assert.Single(TaskFolder.Tasks());
    }

    [AdminFact]
    public async Task Removing_a_script_that_runs_as_administrator_deletes_its_task()
    {
        _group.Add(Script("admin.ps1"));
        var item = _group.Items[0];
        item.RunElevated = true;
        await Wait.Until(() => item.IsIdle && item.RunElevated, 30);

        Assert.Null(await _group.RemoveAsync(item));

        Assert.Empty(_group.Items);
        Assert.Empty(TaskFolder.Tasks());
    }
}
