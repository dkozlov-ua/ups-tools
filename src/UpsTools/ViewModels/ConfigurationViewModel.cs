using UpsTools.Core;
using UpsTools.Services;

namespace UpsTools.ViewModels;

public sealed class ConfigurationViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action _changed;
    private readonly AppLog _log;

    /// <param name="changed">Saves the settings.</param>
    /// <param name="monitorChanged">Applies a changed setting of the monitor: its period or the events' limits.</param>
    /// <param name="updater">Updates from GitHub; the real one unless a test says otherwise.</param>
    public ConfigurationViewModel(AppSettings settings, AppLog log, Action changed, Action monitorChanged, Updater? updater = null)
    {
        _settings = settings;
        _log = log;
        _changed = changed;

        NumberSetting Monitor(int min, int max, Func<int> get, Action<int> set) => new(min, max, get, value =>
        {
            set(value);
            changed();
            monitorChanged();
        });

        UpdatePeriod = Monitor(AppSettings.MinUpdatePeriodSeconds, AppSettings.MaxUpdatePeriodSeconds,
            () => settings.UpdatePeriodSeconds, v => settings.UpdatePeriodSeconds = v);
        BatteryLowPercent = Monitor(0, AppSettings.MaxBatteryLowPercent, () => settings.BatteryLowPercent, v => settings.BatteryLowPercent = v);
        BatteryLowMinutes = Monitor(0, AppSettings.MaxBatteryLowMinutes, () => settings.BatteryLowMinutes, v => settings.BatteryLowMinutes = v);
        GridLostDelay = Monitor(0, AppSettings.MaxGridLostDelaySeconds, () => settings.GridLostDelaySeconds, v => settings.GridLostDelaySeconds = v);
        UnreachableOnBattery = Monitor(0, AppSettings.MaxUnreachableOnBatteryMinutes,
            () => settings.UnreachableOnBatteryMinutes, v => settings.UnreachableOnBatteryMinutes = v);
        StartWithWindows = StartupRegistration.IsEnabled;
        Notifications = [.. Enum.GetValues<UpsEventKind>().Select(kind => new NotificationOption(kind, settings, changed))];
        Update = new UpdateViewModel(updater ?? new Updater(), log);
        RefreshPairing();
    }

    public IReadOnlyList<NotificationOption> Notifications { get; }

    public string Version { get; } = "Version " + Updater.CurrentVersionText;

    /// <summary>The update button.</summary>
    public UpdateViewModel Update { get; }

    public string PairedTitle { get; private set => Set(ref field, value); } = "";
    public string PairedDetail { get; private set => Set(ref field, value); } = "";
    public bool IsPaired { get; private set => Set(ref field, value); }

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set
        {
            if (_settings.CloseToTray == value) return;
            _settings.CloseToTray = value;
            OnPropertyChanged();
            _changed();
        }
    }

    public bool StartWithWindows
    {
        get;
        set
        {
            if (field == value) return;
            try
            {
                StartupRegistration.SetEnabled(value);
                field = value;
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not change the Windows startup entry: {ex.Message}");
            }
            OnPropertyChanged();
        }
    }

    /// <summary>How often the UPS is polled, in seconds.</summary>
    public NumberSetting UpdatePeriod { get; }

    /// <summary>"Battery low" at or below this charge; 0: off.</summary>
    public NumberSetting BatteryLowPercent { get; }

    /// <summary>"Battery low" at or below this many minutes left; 0: off.</summary>
    public NumberSetting BatteryLowMinutes { get; }

    /// <summary>Seconds the grid must be gone before "Grid lost"; 0: at once.</summary>
    public NumberSetting GridLostDelay { get; }

    /// <summary>Minutes unreachable on battery before "Battery low"; 0: never.</summary>
    public NumberSetting UnreachableOnBattery { get; }

    public void RefreshPairing()
    {
        PairedUps? ups = _settings.Ups;
        IsPaired = ups is not null;
        PairedTitle = ups is null ? "No UPS paired" : ups.Title;
        PairedDetail = ups is null
            ? "Find your UPS to start monitoring it."
            : $"{ups.Name} · {ups.Address} · via Bluetooth LE";
    }
}

/// <summary>
/// A whole number typed into a text box: applied, clamped to its range, as soon as it parses; the box shows the
/// value in effect once it loses focus (<see cref="Normalize"/>).
/// </summary>
public sealed class NumberSetting : ObservableObject
{
    private readonly Func<int> _get;
    private readonly Action<int> _set;

    public NumberSetting(int min, int max, Func<int> get, Action<int> set)
    {
        Min = min;
        Max = max;
        _get = get;
        _set = set;
        Text = get().ToString();
    }

    public int Min { get; }
    public int Max { get; }
    public int Value => _get();

    public string Text
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            if (!int.TryParse(value, out var number)) return;
            number = Math.Clamp(number, Min, Max);
            if (number != _get()) _set(number);
        }
    } = "";

    /// <summary>Show the value actually in effect, e.g. after the user typed something out of range.</summary>
    public void Normalize() => Text = _get().ToString();
}

/// <summary>Whether one event shows a Windows notification.</summary>
public sealed class NotificationOption(UpsEventKind kind, AppSettings settings, Action changed) : ObservableObject
{
    public UpsEventKind Kind { get; } = kind;
    public string Title { get; } = kind.Title();

    public bool IsEnabled
    {
        get => settings.NotifyOn(Kind);
        set
        {
            if (settings.NotifyOn(Kind) == value) return;
            settings.Notifications[Kind] = value;
            OnPropertyChanged();
            changed();
        }
    }
}
