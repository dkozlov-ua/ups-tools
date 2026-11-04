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
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON in %APPDATA%\UpsTools.</summary>
public sealed class SettingsStore(AppLog log)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Folder { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UpsTools");

    public static string FilePath { get; } = System.IO.Path.Combine(Folder, "settings.json");

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
