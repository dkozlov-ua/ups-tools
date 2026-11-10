using UpsTools.Core;
using UpsTools.Services;

namespace UpsTools.ViewModels;

/// <summary>The app-wide settings: the window, Windows startup, the poll period and updates.</summary>
public sealed class ConfigurationViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action _changed;
    private readonly AppLog _log;

    /// <param name="changed">Saves the settings.</param>
    /// <param name="periodChanged">Applies a changed poll period to the monitors.</param>
    /// <param name="updater">Updates from GitHub; the real one unless a test says otherwise.</param>
    public ConfigurationViewModel(AppSettings settings, AppLog log, Action changed, Action periodChanged, Updater? updater = null)
    {
        _settings = settings;
        _log = log;
        _changed = changed;

        UpdatePeriod = new NumberSetting(AppSettings.MinUpdatePeriodSeconds, AppSettings.MaxUpdatePeriodSeconds,
            () => settings.UpdatePeriodSeconds, value =>
            {
                settings.UpdatePeriodSeconds = value;
                changed();
                periodChanged();
            });
        StartWithWindows = StartupRegistration.IsEnabled;
        Update = new UpdateViewModel(updater ?? new Updater(), log);
    }

    public string Version { get; } = "Version " + Updater.CurrentVersionText;

    /// <summary>The update button.</summary>
    public UpdateViewModel Update { get; }

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

    /// <summary>How often each UPS is polled, in seconds.</summary>
    public NumberSetting UpdatePeriod { get; }
}

/// <summary>One UPS's own settings: its name, when its events fire and which of them show a notification.</summary>
public sealed class UpsConfigurationViewModel : ObservableObject
{
    private readonly UpsSettings _ups;
    private readonly Action _changed;
    private readonly Action _renamed;

    /// <param name="changed">Saves the settings.</param>
    /// <param name="rulesChanged">Applies changed event limits to the UPS's monitor.</param>
    /// <param name="renamed">The UPS's name changed.</param>
    public UpsConfigurationViewModel(UpsSettings ups, Action changed, Action rulesChanged, Action renamed)
    {
        _ups = ups;
        _changed = changed;
        _renamed = renamed;

        NumberSetting Rule(int max, Func<int> get, Action<int> set) => new(0, max, get, value =>
        {
            set(value);
            changed();
            rulesChanged();
        });

        BatteryLowPercent = Rule(AppSettings.MaxBatteryLowPercent, () => ups.BatteryLowPercent, v => ups.BatteryLowPercent = v);
        BatteryLowMinutes = Rule(AppSettings.MaxBatteryLowMinutes, () => ups.BatteryLowMinutes, v => ups.BatteryLowMinutes = v);
        GridLostDelay = Rule(AppSettings.MaxGridLostDelaySeconds, () => ups.GridLostDelaySeconds, v => ups.GridLostDelaySeconds = v);
        UnreachableOnBattery = Rule(AppSettings.MaxUnreachableOnBatteryMinutes,
            () => ups.UnreachableOnBatteryMinutes, v => ups.UnreachableOnBatteryMinutes = v);
        Notifications = [.. Enum.GetValues<UpsEventKind>().Select(kind => new NotificationOption(kind, ups, changed))];
    }

    public IReadOnlyList<NotificationOption> Notifications { get; }

    /// <summary>Its brand and model, which it is called unless it is given a <see cref="Label"/>.</summary>
    public string DefaultTitle => _ups.Device.Title;

    /// <summary>What it calls itself, its address and how UPS Tools reaches it.</summary>
    public string Detail => $"{_ups.Device.Name} · {_ups.Device.Address} · via Bluetooth LE";

    /// <summary>What the user calls it; empty: <see cref="DefaultTitle"/>.</summary>
    public string Label
    {
        get => _ups.Label ?? "";
        set
        {
            var label = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_ups.Label == label) return;
            _ups.Label = label;
            OnPropertyChanged();
            _changed();
            _renamed();
        }
    }

    /// <summary>"Battery low" at or below this charge; 0: off.</summary>
    public NumberSetting BatteryLowPercent { get; }

    /// <summary>"Battery low" at or below this many minutes left; 0: off.</summary>
    public NumberSetting BatteryLowMinutes { get; }

    /// <summary>Seconds the grid must be gone before "Grid lost"; 0: at once.</summary>
    public NumberSetting GridLostDelay { get; }

    /// <summary>Minutes unreachable on battery before "Battery low"; 0: never.</summary>
    public NumberSetting UnreachableOnBattery { get; }

    /// <summary>The device was paired again, e.g. with another user ID.</summary>
    public void RefreshDevice()
    {
        OnPropertyChanged(nameof(DefaultTitle));
        OnPropertyChanged(nameof(Detail));
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

/// <summary>Whether one event of a UPS shows a Windows notification.</summary>
public sealed class NotificationOption(UpsEventKind kind, UpsSettings settings, Action changed) : ObservableObject
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
