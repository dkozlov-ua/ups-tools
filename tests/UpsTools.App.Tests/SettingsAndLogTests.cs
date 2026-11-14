using System.Text.Json;
using UpsTools.AppTests.Support;
using UpsTools.Core;
using UpsTools.Services;

namespace UpsTools.AppTests;

public sealed class SettingsAndLogTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly AppLog _log;
    private readonly SettingsStore _store;

    public SettingsAndLogTests()
    {
        _log = new AppLog(_folder.Path);
        _store = new SettingsStore(_log, _folder.Path);
    }

    public void Dispose() => _folder.Dispose();

    // ---- profile

    [Theory]
    [InlineData(new string[0], null, null)]
    [InlineData(new string[0], "e2e", "e2e")]
    [InlineData(new[] { "UpsTools.exe", "--profile", "cli" }, "env", "cli")]
    [InlineData(new[] { "UpsTools.exe", "--PROFILE", "Cli-2_x" }, null, "Cli-2_x")]
    [InlineData(new[] { "UpsTools.exe", "--profile" }, "env", "env")]
    [InlineData(new string[0], "bad name", null)]
    [InlineData(new string[0], "..\\escape", null)]
    [InlineData(new string[0], "", null)]
    public void The_profile_comes_from_the_command_line_then_the_environment(string[] args, string? environment, string? expected)
    {
        Assert.Equal(expected, AppProfile.Parse(args, environment));
    }

    [Fact]
    public void This_test_run_is_its_own_profile()
    {
        Assert.Equal(TestProfile.Name, AppProfile.Name);
        Assert.Equal("-" + TestProfile.Name, AppProfile.Suffix);
        Assert.Equal(" --profile " + TestProfile.Name, AppProfile.CommandLine);
        Assert.EndsWith(@"\UpsTools-" + TestProfile.Name, SettingsStore.DefaultFolder);
        Assert.EndsWith(@"\UpsTools-" + TestProfile.Name, Notifier.CacheFolder);
        Assert.Equal("DKozlov.UpsTools-" + TestProfile.Name, Notifier.AppUserModelId);
        Assert.Equal(@"\UpsTools-" + TestProfile.Name, ElevatedTasks.FolderPath);
        Assert.Equal("UpsTools-" + TestProfile.Name, StartupRegistration.ValueName);
    }

    // ---- settings

    private static readonly PairedUps Bluetti = new("bluetti-ble", "0C:4E:A0:E0:81:E6", "EL100V22527008220279", "EL100V2");

    /// <summary>Settings with one UPS, set up by <paramref name="edit"/>.</summary>
    private static AppSettings With(Action<UpsSettings> edit)
    {
        var ups = new UpsSettings { Device = Bluetti };
        edit(ups);
        return new AppSettings { Upses = [ups] };
    }

    [Fact]
    public void Defaults_when_there_is_no_file()
    {
        var settings = _store.Load();
        Assert.Empty(settings.Upses);
        Assert.True(settings.CloseToTray);
        Assert.Equal(60, settings.UpdatePeriodSeconds);
        Assert.Empty(_log.Recent);
    }

    [Fact]
    public void A_new_ups_has_the_default_limits_and_every_notification()
    {
        var ups = new UpsSettings { Device = Bluetti };
        Assert.Equal(new EventRules(20, 10, 0, 0), ups.Rules());
        Assert.Empty(ups.Scripts);
        Assert.Null(ups.Label);
        Assert.Equal(32, ups.Id.Length);
        Assert.All(Enum.GetValues<UpsEventKind>(), kind => Assert.True(ups.NotifyOn(kind)));
    }

    [Fact]
    public void Settings_round_trip_with_enums_as_names()
    {
        var settings = new AppSettings
        {
            CloseToTray = false,
            UpdatePeriodSeconds = 30,
            Upses =
            [
                new UpsSettings
                {
                    Device = Bluetti,
                    Label = "Office",
                    BatteryLowPercent = 35,
                    BatteryLowMinutes = 0,
                    GridLostDelaySeconds = 15,
                    UnreachableOnBatteryMinutes = 4,
                    Notifications = { [UpsEventKind.UpsReachable] = false },
                },
                new UpsSettings { Device = new PairedUps("ecoflow-ble", "AA:BB:CC:DD:EE:FF", "R651ZEB4XF2E0123", "River 3 Plus", "1234567890") },
            ],
        };
        settings.Upses[0].ScriptsFor(UpsEventKind.GridLost).Add(new ScriptAction
        {
            Path = @"C:\scripts\pause.ahk", Arguments = "--now", Enabled = false, WaitForExit = true, RunElevated = true,
        });

        _store.Save(settings);
        var json = File.ReadAllText(_store.FilePath);
        var loaded = _store.Load();

        Assert.Contains("\"GridLost\"", json);
        Assert.Contains("\"UpsReachable\": false", json);
        Assert.False(loaded.CloseToTray);
        Assert.Equal(30, loaded.UpdatePeriodSeconds);
        Assert.Equal(2, loaded.Upses.Count);
        var (office, river) = (loaded.Upses[0], loaded.Upses[1]);
        Assert.Equal((settings.Upses[0].Id, Bluetti, "Office"), (office.Id, office.Device, office.Label));
        Assert.Equal(new EventRules(35, 0, 15, 4), office.Rules());
        Assert.False(office.NotifyOn(UpsEventKind.UpsReachable));
        Assert.True(office.NotifyOn(UpsEventKind.GridLost));
        var script = Assert.Single(office.ScriptsFor(UpsEventKind.GridLost));
        Assert.Equal(settings.Upses[0].Scripts[UpsEventKind.GridLost][0].Id, script.Id);
        Assert.Equal((@"C:\scripts\pause.ahk", "--now", false, true, true),
            (script.Path, script.Arguments, script.Enabled, script.WaitForExit, script.RunElevated));
        Assert.Equal((settings.Upses[1].Device, (string?)null, "1234567890"), (river.Device, river.Label, river.Device.UserId));
        Assert.Empty(river.ScriptsFor(UpsEventKind.GridLost));
        Assert.False(File.Exists(_store.FilePath + ".tmp"));
        Assert.DoesNotContain("\"WaitLimitMinutes\"", json); // no limit: not written
        Assert.DoesNotContain("\"Power\"", json); // only built-in actions have these
        Assert.DoesNotContain("\"CountdownSeconds\"", json);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(-2, 0)]
    [InlineData(5000, 1440)]
    public void A_scripts_time_limit_round_trips_clamped(int saved, int loaded)
    {
        _store.Save(With(ups => ups.ScriptsFor(UpsEventKind.GridLost).Add(new ScriptAction { Path = "a.ps1", WaitForExit = true, WaitLimitMinutes = saved })));
        Assert.Equal(loaded, _store.Load().Upses[0].ScriptsFor(UpsEventKind.GridLost)[0].WaitLimitMinutes);
    }

    [Fact]
    public void Built_in_actions_round_trip_by_name()
    {
        _store.Save(With(ups => ups.ScriptsFor(UpsEventKind.BatteryLow).Add(new ScriptAction { Power = PowerAction.ShutDown, CountdownSeconds = 90 })));

        Assert.Contains("\"Power\": \"ShutDown\"", File.ReadAllText(_store.FilePath));
        var action = Assert.Single(_store.Load().Upses[0].ScriptsFor(UpsEventKind.BatteryLow));
        Assert.Equal((PowerAction.ShutDown, 90, "Shut down"), (action.Power, action.CountdownSeconds, action.Name));
        Assert.Equal("a.ps1", new ScriptAction { Path = @"C:\x\a.ps1" }.Name);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(-5, 2)]
    [InlineData(601, 600)]
    [InlineData(45, 45)]
    public void The_update_period_is_clamped_on_load(int saved, int loaded)
    {
        _store.Save(new AppSettings { UpdatePeriodSeconds = saved });
        Assert.Equal(loaded, _store.Load().UpdatePeriodSeconds);
    }

    [Theory]
    [InlineData(-1, 0, 601, 600)]
    [InlineData(100, 99, -3, 0)]
    [InlineData(50, 50, 30, 30)]
    public void The_battery_low_limits_are_clamped_on_load(int percent, int loadedPercent, int minutes, int loadedMinutes)
    {
        _store.Save(With(ups => (ups.BatteryLowPercent, ups.BatteryLowMinutes) = (percent, minutes)));
        Assert.Equal(new EventRules(loadedPercent, loadedMinutes), _store.Load().Upses[0].Rules());
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(601, 600)]
    [InlineData(20, 20)]
    public void The_grid_lost_delay_is_clamped_on_load(int saved, int loaded)
    {
        _store.Save(With(ups => (ups.GridLostDelaySeconds, ups.UnreachableOnBatteryMinutes) = (saved, saved)));
        var ups = _store.Load().Upses[0];
        Assert.Equal((loaded, loaded), (ups.GridLostDelaySeconds, ups.UnreachableOnBatteryMinutes));
    }

    [Fact]
    public void A_ups_without_a_device_is_dropped_and_ids_are_made_unique()
    {
        Directory.CreateDirectory(_store.Folder);
        File.WriteAllText(_store.FilePath, """
            { "Upses": [
                { "Id": "same", "Device": { "DriverId": "bluetti-ble", "Address": "A", "Name": "A", "Model": "EL100V2" }, "Label": "  " },
                { "Id": "gone" },
                { "Id": "same", "Device": { "DriverId": "bluetti-ble", "Address": "B", "Name": "B", "Model": "EL100V2" } }
            ] }
            """);
        var upses = _store.Load().Upses;
        Assert.Equal(["A", "B"], upses.Select(u => u.Device.Address));
        Assert.NotEqual(upses[0].Id, upses[1].Id);
        Assert.Null(upses[0].Label);
    }

    // ---- settings of 0.6.0 and earlier: one UPS at the top level

    [Fact]
    public void A_single_ups_of_an_older_version_becomes_the_first_of_the_list()
    {
        Directory.CreateDirectory(_store.Folder);
        const string old = """
            {
              "Ups": { "DriverId": "bluetti-ble", "Address": "0C:4E:A0:E0:81:E6", "Name": "EL100V22527008220279", "Model": "EL100V2" },
              "CloseToTray": false,
              "UpdatePeriodSeconds": 15,
              "BatteryLowPercent": 30,
              "BatteryLowMinutes": 0,
              "GridLostDelaySeconds": 180,
              "UnreachableOnBatteryMinutes": 5,
              "Scripts": { "GridLost": [ { "Id": "abc", "Path": "C:\\scripts\\save.ps1", "Arguments": "", "Enabled": true, "WaitForExit": true, "RunElevated": false } ], "GridRestored": [] },
              "Notifications": { "UpsReachable": false }
            }
            """;
        File.WriteAllText(_store.FilePath, old);

        var settings = _store.Load();

        Assert.Equal((false, 15), (settings.CloseToTray, settings.UpdatePeriodSeconds));
        var ups = Assert.Single(settings.Upses);
        Assert.Equal(Bluetti, ups.Device);
        Assert.Equal(new EventRules(30, 0, 180, 5), ups.Rules());
        var script = Assert.Single(ups.ScriptsFor(UpsEventKind.GridLost));
        Assert.Equal(("abc", @"C:\scripts\save.ps1", true), (script.Id, script.Path, script.WaitForExit));
        Assert.False(ups.NotifyOn(UpsEventKind.UpsReachable));

        // Saved in the new form at once, with the old file kept.
        Assert.Equal(old, File.ReadAllText(Path.Combine(_store.Folder, SettingsStore.SingleUpsBackupName)));
        using var doc = JsonDocument.Parse(File.ReadAllText(_store.FilePath));
        Assert.Equal(["Upses", "CloseToTray", "UpdatePeriodSeconds"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(ups.Id, _store.Load().Upses.Single().Id);
        Assert.Empty(_log.Recent);
    }

    [Fact]
    public void Settings_of_an_older_version_without_a_ups_have_nothing_to_move()
    {
        Directory.CreateDirectory(_store.Folder);
        File.WriteAllText(_store.FilePath, """{ "Ups": null, "CloseToTray": false, "UpdatePeriodSeconds": 30, "Scripts": { "GridLost": [] } }""");
        var settings = _store.Load();
        Assert.Equal((false, 30), (settings.CloseToTray, settings.UpdatePeriodSeconds));
        Assert.Empty(settings.Upses);
        Assert.False(File.Exists(Path.Combine(_store.Folder, SettingsStore.SingleUpsBackupName)));
        Assert.Empty(_log.Recent);
    }

    [Fact]
    public void Scripts_of_an_older_version_without_a_ups_are_kept_in_the_old_file()
    {
        Directory.CreateDirectory(_store.Folder);
        File.WriteAllText(_store.FilePath, """{ "Ups": null, "Scripts": { "GridLost": [ { "Path": "C:\\a.ps1" } ] } }""");
        Assert.Empty(_store.Load().Upses);
        Assert.True(File.Exists(Path.Combine(_store.Folder, SettingsStore.SingleUpsBackupName)));
        Assert.StartsWith("The scripts set up without a paired UPS", Assert.Single(_log.Recent).Message);
        Assert.Contains("\"Upses\": []", File.ReadAllText(_store.FilePath)); // said once
    }

    [Fact]
    public void A_broken_file_is_kept_aside_and_defaults_are_used()
    {
        Directory.CreateDirectory(_store.Folder);
        File.WriteAllText(_store.FilePath, "{ not json");

        var settings = _store.Load();

        Assert.Empty(settings.Upses);
        Assert.Equal("{ not json", File.ReadAllText(_store.FilePath + ".broken"));
        var warning = Assert.Single(_log.Recent);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.StartsWith("Settings file is unreadable (", warning.Message);
    }

    [Fact]
    public void A_null_file_gives_defaults()
    {
        File.WriteAllText(_store.FilePath, "null");
        Assert.Equal(60, _store.Load().UpdatePeriodSeconds);
    }

    [Fact]
    public void A_failed_save_is_logged_not_thrown()
    {
        var blocked = new SettingsStore(_log, _folder.File("a-file", "not a folder"));
        blocked.Save(new AppSettings());
        Assert.StartsWith("Could not save settings: ", Assert.Single(_log.Recent, e => e.Level == LogLevel.Warning).Message);
    }

    [Fact]
    public void ScriptsFor_creates_the_list_once()
    {
        var settings = new UpsSettings();
        var list = settings.ScriptsFor(UpsEventKind.GridRestored);
        Assert.Same(list, settings.ScriptsFor(UpsEventKind.GridRestored));
        Assert.Same(list, settings.Scripts[UpsEventKind.GridRestored]);
    }

    [Fact]
    public void New_scripts_get_an_id_and_are_enabled()
    {
        var a = new ScriptAction();
        Assert.Equal(32, a.Id.Length);
        Assert.NotEqual(a.Id, new ScriptAction().Id);
        Assert.True(a.Enabled);
        Assert.False(a.WaitForExit);
        Assert.False(a.RunElevated);
        Assert.Equal("", a.Arguments);
    }

    [Fact]
    public void Default_folders_are_the_profiles()
    {
        Assert.Equal(SettingsStore.DefaultFolder, new SettingsStore(_log).Folder);
        Assert.Equal(SettingsStore.DefaultFolder, new AppLog().Folder);
        Assert.Equal(Path.Combine(SettingsStore.DefaultFolder, "settings.json"), new SettingsStore(_log).FilePath);
    }

    // ---- log

    [Fact]
    public void Entries_are_kept_raised_and_written()
    {
        var raised = new List<LogEntry>();
        _log.EntryAdded += raised.Add;

        _log.Info("one");
        _log.Event("Grid lost");
        _log.Warn("three");

        Assert.Equal([(LogLevel.Info, "one"), (LogLevel.Event, "Grid lost"), (LogLevel.Warning, "three")],
            _log.Recent.Select(e => (e.Level, e.Message)));
        Assert.Equal(_log.Recent, raised);
        var lines = File.ReadAllLines(_log.FilePath);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d \[Event\] Grid lost$", lines[1]);
        Assert.Matches(@"^\d\d:\d\d:\d\d$", raised[0].TimeText);
    }

    [Fact]
    public void Only_the_latest_entries_are_kept_in_memory()
    {
        for (var i = 0; i < AppLog.MaxEntries + 5; i++) _log.Info($"entry {i}");
        Assert.Equal(AppLog.MaxEntries, _log.Recent.Count);
        Assert.Equal("entry 5", _log.Recent[0].Message);
    }

    [Fact]
    public void A_full_log_file_is_rotated()
    {
        Directory.CreateDirectory(_log.Folder);
        File.WriteAllText(_log.FilePath, new string('x', (int)AppLog.MaxFileBytes + 1));

        _log.Info("fresh");

        Assert.Equal(AppLog.MaxFileBytes + 1, new FileInfo(_log.FilePath + ".1").Length);
        Assert.EndsWith("[Info] fresh", File.ReadAllText(_log.FilePath).TrimEnd());
    }

    [Fact]
    public void Logging_survives_an_unwritable_file()
    {
        var log = new AppLog(_folder.File("a-file", "not a folder"));
        log.Warn("still here");
        Assert.Equal("still here", Assert.Single(log.Recent).Message);
    }

    [Fact]
    public async Task Logging_from_many_threads_loses_nothing()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 20; i++) _log.Info($"{t}/{i}");
        })));
        Assert.Equal(160, _log.Recent.Count);
        Assert.Equal(160, File.ReadAllLines(_log.FilePath).Length);
    }

    [Fact]
    public void Settings_json_is_human_readable()
    {
        _store.Save(With(_ => { }));
        using var doc = JsonDocument.Parse(File.ReadAllText(_store.FilePath));
        Assert.Equal(["Upses", "CloseToTray", "UpdatePeriodSeconds"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Contains("\n  \"CloseToTray\"", File.ReadAllText(_store.FilePath));
        Assert.Equal(["Id", "Device", "BatteryLowPercent", "BatteryLowMinutes", "GridLostDelaySeconds", "UnreachableOnBatteryMinutes", "Scripts", "Notifications"],
            doc.RootElement.GetProperty("Upses")[0].EnumerateObject().Select(p => p.Name));
    }
}
