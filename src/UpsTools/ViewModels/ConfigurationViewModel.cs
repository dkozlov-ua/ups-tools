using System.Reflection;
using UpsTools.Core;
using UpsTools.Services;

namespace UpsTools.ViewModels;

public sealed class ConfigurationViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action _changed;
    private readonly Action<TimeSpan> _periodChanged;
    private readonly AppLog _log;

    public ConfigurationViewModel(AppSettings settings, AppLog log, Action changed, Action<TimeSpan> periodChanged)
    {
        _settings = settings;
        _log = log;
        _changed = changed;
        _periodChanged = periodChanged;
        UpdatePeriodText = settings.UpdatePeriodSeconds.ToString();
        StartWithWindows = StartupRegistration.IsEnabled;
        RefreshPairing();
    }

    public string Version { get; } = "Version " + (Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?");

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

    /// <summary>Text box contents; applied (and clamped) when it parses.</summary>
    public string UpdatePeriodText
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            if (!int.TryParse(value, out var seconds)) return;
            seconds = Math.Clamp(seconds, AppSettings.MinUpdatePeriodSeconds, AppSettings.MaxUpdatePeriodSeconds);
            if (seconds == _settings.UpdatePeriodSeconds) return;
            _settings.UpdatePeriodSeconds = seconds;
            _changed();
            _periodChanged(TimeSpan.FromSeconds(seconds));
        }
    } = "";

    /// <summary>Show the value actually in effect, e.g. after the user typed something out of range.</summary>
    public void NormalizeUpdatePeriod() => UpdatePeriodText = _settings.UpdatePeriodSeconds.ToString();

    public void RefreshPairing()
    {
        PairedUps? ups = _settings.Ups;
        IsPaired = ups is not null;
        PairedTitle = ups is null ? "No UPS paired" : $"Bluetti {ups.Model}";
        PairedDetail = ups is null
            ? "Find your UPS to start monitoring it."
            : $"{ups.Name} · {ups.Address} · via Bluetooth LE";
    }
}
