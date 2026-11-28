using System.Text.Json;
using System.Text.Json.Serialization;
using UpsTools.Core;
using UpsTools.Core.Sharing;

namespace UpsTools.Services;

/// <summary>
/// The phone's settings: the Windows app's (<c>settings.json</c>) without the scripts and the tray, with what only a
/// phone has. Kept in the app's own storage.
/// </summary>
public sealed class AppSettings
{
    public const int MinUpdatePeriodSeconds = 2;
    public const int MaxUpdatePeriodSeconds = 600;
    public const int MaxBatteryLowPercent = 99;
    public const int MaxBatteryLowMinutes = 600;
    public const int MaxGridLostDelaySeconds = 600;
    public const int MaxUnreachableOnBatteryMinutes = 600;

    /// <summary>The paired UPSes, each with its own event limits and notifications.</summary>
    public List<UpsSettings> Upses { get; set; } = [];

    /// <summary>How often every UPS is polled.</summary>
    public int UpdatePeriodSeconds { get; set; } = 60;

    /// <summary>Monitoring starts again when the phone starts.</summary>
    public bool StartAtBoot { get; set; } = true;

    /// <summary>
    /// Keeps the processor awake while monitoring, so that polls and events go on with the screen off. Without it,
    /// Android may suspend the app between polls for minutes.
    /// </summary>
    public bool KeepAwake { get; set; } = true;

    /// <summary>Whether other devices can watch the UPSes this one is connected to, and how.</summary>
    public SharingSettings Sharing { get; set; } = new();
}

/// <summary>Sharing this phone's UPSes with the PCs and phones on the network (<see cref="SharingServer"/>).</summary>
public sealed class SharingSettings
{
    public bool Enabled { get; set; }

    /// <summary>What the others need to watch them; made up when sharing is first turned on.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }

    /// <summary>The TCP and UDP port; only settings.json changes it.</summary>
    public int Port { get; set; } = SharingServer.DefaultPort;
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

    /// <summary>Which events show a notification. Events not listed do.</summary>
    public Dictionary<UpsEventKind, bool> Notifications { get; set; } = [];

    public bool NotifyOn(UpsEventKind kind) => !Notifications.TryGetValue(kind, out var on) || on;

    /// <summary>Whether the status notification (the one that stays while monitoring) has a line about it.</summary>
    public bool ShowInStatus { get; set; } = true;

    /// <summary>
    /// Which events change the status notification's colour while their state lasts (<see cref="StatusEventKinds"/>).
    /// Events not listed do.
    /// </summary>
    public Dictionary<UpsEventKind, bool> StatusEvents { get; set; } = [];

    /// <summary>
    /// The events that change the status notification's colour while their state lasts: on battery it turns
    /// yellow; with the battery low (until the grid is back) and unreachable, red.
    /// </summary>
    public static IReadOnlyList<UpsEventKind> StatusEventKinds { get; } =
        [UpsEventKind.GridLost, UpsEventKind.BatteryLow, UpsEventKind.UpsUnreachable];

    public bool StatusOn(UpsEventKind kind) => !StatusEvents.TryGetValue(kind, out var on) || on;

    /// <summary>The URLs its events call, as scripts are on Windows (<see cref="UpsTools.Droid.WebhookSender"/>).</summary>
    public List<WebhookSettings> Webhooks { get; set; } = [];
}

/// <summary>What a webhook's request carries.</summary>
public enum WebhookFormat
{
    /// <summary>A JSON object: the event and the UPS's state, as the script variables on Windows (Home Assistant, n8n…).</summary>
    Json,

    /// <summary>The notification's text, its title in a Title header (ntfy, and anything that takes plain text).</summary>
    Text,
}

/// <summary>A URL that a UPS's events call with a POST.</summary>
public sealed class WebhookSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = "";
    public WebhookFormat Format { get; set; } = WebhookFormat.Json;
    public bool Enabled { get; set; } = true;

    /// <summary>The events that call it; all of them for a new one.</summary>
    public List<UpsEventKind> Events { get; set; } = [.. Enum.GetValues<UpsEventKind>()];

    /// <summary>An http(s) URL with a host, the only kind it calls.</summary>
    public static bool IsValidUrl(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0;
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON (settings.json) in the app's storage.</summary>
public sealed class SettingsStore(AppLog log, string folder)
{
    public const string FileName = "settings.json";

    /// <summary>The app's storage; the app sets it as it starts (<see cref="AppLog"/>'s default folder).</summary>
    public static string DefaultFolder { get; set; } = Path.GetTempPath();


    public string Folder { get; } = folder;
    public string FilePath => Path.Combine(Folder, FileName);

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var settings = JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            settings.Upses ??= [];
            settings.Upses.RemoveAll(ups => ups?.Device is null);
            settings.Sharing ??= new SharingSettings();
            settings.Sharing.Code = SharingCode.Format(settings.Sharing.Code);
            if (settings.Sharing.Port is < 1 or > 65535) settings.Sharing.Port = SharingServer.DefaultPort;
            settings.UpdatePeriodSeconds = Math.Clamp(settings.UpdatePeriodSeconds,
                AppSettings.MinUpdatePeriodSeconds, AppSettings.MaxUpdatePeriodSeconds);
            foreach (var ups in settings.Upses)
            {
                if (string.IsNullOrWhiteSpace(ups.Id) || settings.Upses.Count(u => u.Id == ups.Id) > 1) ups.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(ups.Label)) ups.Label = null;
                ups.Notifications ??= [];
                ups.StatusEvents ??= [];
                ups.Webhooks ??= [];
                ups.Webhooks.RemoveAll(w => w is null);
                foreach (var hook in ups.Webhooks) hook.Events ??= [];
                ups.BatteryLowPercent = Math.Clamp(ups.BatteryLowPercent, 0, AppSettings.MaxBatteryLowPercent);
                ups.BatteryLowMinutes = Math.Clamp(ups.BatteryLowMinutes, 0, AppSettings.MaxBatteryLowMinutes);
                ups.GridLostDelaySeconds = Math.Clamp(ups.GridLostDelaySeconds, 0, AppSettings.MaxGridLostDelaySeconds);
                ups.UnreachableOnBatteryMinutes = Math.Clamp(ups.UnreachableOnBatteryMinutes, 0, AppSettings.MaxUnreachableOnBatteryMinutes);
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

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            log.Warn($"Could not save settings: {ex.Message}");
        }
    }
}

/// <summary>settings.json, through a serializer generated at build time: the app is trimmed.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>The colour a UPS (or all of them) gives the status notification, as the tray icon on Windows.</summary>
public enum TrayState
{
    Neutral,
    GridOnline,
    OnBattery,
    Problem,
}
