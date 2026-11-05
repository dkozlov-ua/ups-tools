using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UpsTools.Core;

namespace UpsTools.Services;

public sealed class AppSettings
{
    public const int MinUpdatePeriodSeconds = 2;
    public const int MaxUpdatePeriodSeconds = 600;

    public PairedUps? Ups { get; set; }
    public bool CloseToTray { get; set; } = true;
    public int UpdatePeriodSeconds { get; set; } = 10;
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

    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>The next script starts only after this one exits with code 0; if it fails, the rest are skipped.</summary>
    public bool WaitForExit { get; set; }

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

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
            settings.UpdatePeriodSeconds = Math.Clamp(settings.UpdatePeriodSeconds,
                AppSettings.MinUpdatePeriodSeconds, AppSettings.MaxUpdatePeriodSeconds);
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
