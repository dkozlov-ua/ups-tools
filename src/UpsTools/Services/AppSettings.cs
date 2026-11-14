using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using UpsTools.Core;

namespace UpsTools.Services;

public sealed class AppSettings
{
    public const int MinUpdatePeriodSeconds = 2;
    public const int MaxUpdatePeriodSeconds = 600;
    public const int MaxBatteryLowPercent = 99;
    public const int MaxBatteryLowMinutes = 600;
    public const int MaxGridLostDelaySeconds = 600;
    public const int MaxUnreachableOnBatteryMinutes = 600;

    /// <summary>The paired UPSes, each with its own scripts, event limits and notifications.</summary>
    public List<UpsSettings> Upses { get; set; } = [];

    public bool CloseToTray { get; set; } = true;

    /// <summary>How often every UPS is polled.</summary>
    public int UpdatePeriodSeconds { get; set; } = 60;

    /// <summary>Whether other PCs can watch the UPSes this PC is connected to, and how.</summary>
    public SharingSettings Sharing { get; set; } = new();
}

/// <summary>Sharing this PC's UPSes with other PCs (<see cref="UpsTools.Core.Sharing.SharingServer"/>).</summary>
public sealed class SharingSettings
{
    public bool Enabled { get; set; }

    /// <summary>What other PCs need to watch them; made up when sharing is first turned on.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }

    /// <summary>The TCP port; only settings.json changes it.</summary>
    public int Port { get; set; } = UpsTools.Core.Sharing.SharingServer.DefaultPort;
}

/// <summary>One paired UPS and what happens on its events.</summary>
public sealed class UpsSettings
{
    /// <summary>Tells UPSes apart in notifications, whatever they are called.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public PairedUps Device { get; set; } = null!;

    /// <summary>What the user calls it; null: its brand and model (<see cref="PairedUps.Title"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    /// <summary>"Battery low" at or below this charge, on battery; 0: never.</summary>
    public int BatteryLowPercent { get; set; } = 20;

    /// <summary>"Battery low" at or below this many minutes left, on battery; 0: never.</summary>
    public int BatteryLowMinutes { get; set; } = 10;

    /// <summary>"Grid lost" only once the grid has been gone this long; 0: at once.</summary>
    public int GridLostDelaySeconds { get; set; }

    /// <summary>"Battery low" once the UPS has been unreachable this long on battery; 0: never.</summary>
    public int UnreachableOnBatteryMinutes { get; set; }

    public EventRules Rules() => new(BatteryLowPercent, BatteryLowMinutes, GridLostDelaySeconds, UnreachableOnBatteryMinutes);
    public Dictionary<UpsEventKind, List<ScriptAction>> Scripts { get; set; } = [];

    /// <summary>Which events show a Windows notification. Events not listed do.</summary>
    public Dictionary<UpsEventKind, bool> Notifications { get; set; } = [];

    public bool NotifyOn(UpsEventKind kind) => !Notifications.TryGetValue(kind, out var on) || on;

    public List<ScriptAction> ScriptsFor(UpsEventKind kind)
    {
        if (!Scripts.TryGetValue(kind, out var list)) Scripts[kind] = list = [];
        return list;
    }
}

public sealed class ScriptAction
{
    /// <summary>Names the script's administrator task (<see cref="ElevatedTasks"/>).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>A built-in action instead of a script: <see cref="Path"/> is empty then.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PowerAction? Power { get; set; }

    /// <summary>For a built-in action: how long the countdown before it lasts (<see cref="PowerRunner"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CountdownSeconds { get; set; }

    public string Path { get; set; } = "";

    /// <summary>What logs and notifications call it: the script's file name, or the built-in action.</summary>
    [JsonIgnore]
    public string Name => Power is { } power ? power.Title() : System.IO.Path.GetFileName(Path);
    public string Arguments { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>The next script starts only after this one exits with code 0; if it fails, the rest are skipped.</summary>
    public bool WaitForExit { get; set; }

    /// <summary>With <see cref="WaitForExit"/>: still running after this many minutes, the next script starts anyway; 0: no limit.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int WaitLimitMinutes { get; set; }

    public const int MaxWaitLimitMinutes = 1440;

    /// <summary>Runs as administrator, through a scheduled task registered when this was turned on.</summary>
    public bool RunElevated { get; set; }
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON in %APPDATA%\UpsTools.</summary>
/// <param name="folder">Where settings.json goes: <see cref="DefaultFolder"/> unless a test says otherwise.</param>
public sealed class SettingsStore(AppLog log, string? folder = null)
{
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>%APPDATA%\UpsTools, or %APPDATA%\UpsTools-name for a profile (<see cref="AppProfile"/>).</summary>
    public static string DefaultFolder { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UpsTools" + AppProfile.Suffix);

    public string Folder { get; } = folder ?? DefaultFolder;
    public string FilePath => System.IO.Path.Combine(Folder, FileName);

    /// <summary>What settings.json was renamed to when its single UPS moved into <see cref="AppSettings.Upses"/> (0.7.0).</summary>
    public const string SingleUpsBackupName = "settings.json.before-0.7.0";

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var text = File.ReadAllText(FilePath);
            var root = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("not a JSON object");
            var settings = root.Deserialize<AppSettings>(Json) ?? new AppSettings();
            if (!root.ContainsKey(nameof(AppSettings.Upses))) MoveSingleUps(root, settings, text);

            settings.Upses.RemoveAll(ups => ups.Device is null);
            settings.Sharing ??= new SharingSettings();
            settings.Sharing.Code = UpsTools.Core.Sharing.SharingCode.Format(settings.Sharing.Code);
            if (settings.Sharing.Port is < 1 or > 65535) settings.Sharing.Port = UpsTools.Core.Sharing.SharingServer.DefaultPort;
            settings.UpdatePeriodSeconds = Math.Clamp(settings.UpdatePeriodSeconds,
                AppSettings.MinUpdatePeriodSeconds, AppSettings.MaxUpdatePeriodSeconds);
            foreach (var ups in settings.Upses)
            {
                if (string.IsNullOrWhiteSpace(ups.Id) || settings.Upses.Count(u => u.Id == ups.Id) > 1) ups.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(ups.Label)) ups.Label = null;
                ups.BatteryLowPercent = Math.Clamp(ups.BatteryLowPercent, 0, AppSettings.MaxBatteryLowPercent);
                ups.BatteryLowMinutes = Math.Clamp(ups.BatteryLowMinutes, 0, AppSettings.MaxBatteryLowMinutes);
                ups.GridLostDelaySeconds = Math.Clamp(ups.GridLostDelaySeconds, 0, AppSettings.MaxGridLostDelaySeconds);
                ups.UnreachableOnBatteryMinutes = Math.Clamp(ups.UnreachableOnBatteryMinutes, 0, AppSettings.MaxUnreachableOnBatteryMinutes);
                foreach (var action in ups.Scripts.Values.SelectMany(list => list))
                    action.WaitLimitMinutes = Math.Clamp(action.WaitLimitMinutes, 0, ScriptAction.MaxWaitLimitMinutes);
            }
            return settings;
        }
        catch (Exception ex)
        {
            var backup = FilePath + ".broken";
            log.Warn($"Settings file is unreadable ({ex.Message}); starting with defaults. The old file was kept as {backup}");
            try { File.Copy(FilePath, backup, overwrite: true); } catch { /* best effort */ }
            return new AppSettings();
        }
    }

    /// <summary>
    /// Before 0.7.0, settings.json had one UPS ("Ups") and its scripts, limits and notifications at the top level:
    /// they become the first of <see cref="AppSettings.Upses"/>. The old file is kept as <see cref="SingleUpsBackupName"/>.
    /// </summary>
    private void MoveSingleUps(JsonObject root, AppSettings settings, string text)
    {
        var ups = root.Deserialize<UpsSettings>(Json) ?? new UpsSettings();
        ups.Device = root["Ups"]?.Deserialize<PairedUps>(Json)!;
        var hasScripts = ups.Scripts.Values.Any(list => list.Count > 0);
        if (ups.Device is null && !hasScripts) return; // nothing to move

        try { File.WriteAllText(System.IO.Path.Combine(Folder, SingleUpsBackupName), text); } catch { /* best effort */ }
        if (ups.Device is not null)
        {
            settings.Upses.Add(ups);
            Save(settings);
        }
        else
        {
            log.Warn($"The scripts set up without a paired UPS now belong to each UPS: pair one and set them up again. {SingleUpsBackupName} still has them.");
            Save(settings);
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            log.Warn($"Could not save settings: {ex.Message}");
        }
    }
}
