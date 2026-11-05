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

    [Fact]
    public void Defaults_when_there_is_no_file()
    {
        var settings = _store.Load();
        Assert.Null(settings.Ups);
        Assert.True(settings.CloseToTray);
        Assert.Equal(10, settings.UpdatePeriodSeconds);
        Assert.Equal(new EventRules(20, 10), settings.Rules());
        Assert.Empty(settings.Scripts);
        Assert.All(Enum.GetValues<UpsEventKind>(), kind => Assert.True(settings.NotifyOn(kind)));
        Assert.Empty(_log.Recent);
    }

    [Fact]
    public void Settings_round_trip_with_enums_as_names()
    {
        var settings = new AppSettings
        {
            Ups = new PairedUps("bluetti-ble", "0C:4E:A0:E0:81:E6", "EL100V22527008220279", "EL100V2"),
            CloseToTray = false,
            UpdatePeriodSeconds = 30,
            BatteryLowPercent = 35,
            BatteryLowMinutes = 0,
            Notifications = { [UpsEventKind.UpsReachable] = false },
        };
        settings.ScriptsFor(UpsEventKind.GridLost).Add(new ScriptAction
        {
            Path = @"C:\scripts\pause.ahk", Arguments = "--now", Enabled = false, WaitForExit = true, RunElevated = true,
        });

        _store.Save(settings);
        var json = File.ReadAllText(_store.FilePath);
        var loaded = _store.Load();

        Assert.Contains("\"GridLost\"", json);
        Assert.Contains("\"UpsReachable\": false", json);
        Assert.Equal(settings.Ups, loaded.Ups);
        Assert.False(loaded.CloseToTray);
        Assert.Equal(30, loaded.UpdatePeriodSeconds);
        Assert.Equal(new EventRules(35, 0), loaded.Rules());
        Assert.False(loaded.NotifyOn(UpsEventKind.UpsReachable));
        Assert.True(loaded.NotifyOn(UpsEventKind.GridLost));
        var script = Assert.Single(loaded.ScriptsFor(UpsEventKind.GridLost));
        Assert.Equal(settings.Scripts[UpsEventKind.GridLost][0].Id, script.Id);
        Assert.Equal((@"C:\scripts\pause.ahk", "--now", false, true, true),
            (script.Path, script.Arguments, script.Enabled, script.WaitForExit, script.RunElevated));
        Assert.False(File.Exists(_store.FilePath + ".tmp"));
        Assert.DoesNotContain("\"Power\"", json); // only built-in actions have these
        Assert.DoesNotContain("\"CountdownSeconds\"", json);
    }

    [Fact]
    public void Built_in_actions_round_trip_by_name()
    {
        var settings = new AppSettings();
        settings.ScriptsFor(UpsEventKind.BatteryLow).Add(new ScriptAction { Power = PowerAction.ShutDown, CountdownSeconds = 90 });
        _store.Save(settings);

        Assert.Contains("\"Power\": \"ShutDown\"", File.ReadAllText(_store.FilePath));
        var action = Assert.Single(_store.Load().ScriptsFor(UpsEventKind.BatteryLow));
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
        _store.Save(new AppSettings { BatteryLowPercent = percent, BatteryLowMinutes = minutes });
        Assert.Equal(new EventRules(loadedPercent, loadedMinutes), _store.Load().Rules());
    }

    [Fact]
    public void Settings_from_an_older_version_get_the_default_battery_low_limits()
    {
        Directory.CreateDirectory(_store.Folder);
        File.WriteAllText(_store.FilePath, """{ "CloseToTray": false, "UpdatePeriodSeconds": 30 }""");
        var settings = _store.Load();
        Assert.Equal((false, 20, 10), (settings.CloseToTray, settings.BatteryLowPercent, settings.BatteryLowMinutes));
    }

    [Fact]
    public void A_broken_file_is_kept_aside_and_defaults_are_used()
    {
        Directory.CreateDirectory(_store.Folder);
        File.WriteAllText(_store.FilePath, "{ not json");

        var settings = _store.Load();

        Assert.Null(settings.Ups);
        Assert.Equal("{ not json", File.ReadAllText(_store.FilePath + ".broken"));
        var warning = Assert.Single(_log.Recent);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.StartsWith("Settings file is unreadable (", warning.Message);
    }

    [Fact]
    public void A_null_file_gives_defaults()
    {
        File.WriteAllText(_store.FilePath, "null");
        Assert.Equal(10, _store.Load().UpdatePeriodSeconds);
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
        var settings = new AppSettings();
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
        _store.Save(new AppSettings());
        using var doc = JsonDocument.Parse(File.ReadAllText(_store.FilePath));
        Assert.Equal(["Ups", "CloseToTray", "UpdatePeriodSeconds", "BatteryLowPercent", "BatteryLowMinutes", "Scripts", "Notifications"],
            doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Contains("\n  \"CloseToTray\"", File.ReadAllText(_store.FilePath));
    }
}
