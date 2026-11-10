using System.Collections.ObjectModel;
using UpsTools.Core;
using UpsTools.Services;

namespace UpsTools.ViewModels;

/// <summary>Drives the colour of an indicator; mapped to theme brushes in XAML.</summary>
public enum Tone
{
    Neutral,
    Good,
    Warning,
    Bad,
}

public enum LinkStatus
{
    NotPaired,
    Connecting,
    Online,
    Retrying,
    Unreachable,
}

/// <summary>What the log says, newest first, as the dashboard shows it: the last 100 entries.</summary>
public sealed class ActivityFeed : ObservableCollection<LogEntry>
{
    private const int MaxEntries = 100;

    public void Post(LogEntry entry)
    {
        Insert(0, entry);
        while (Count > MaxEntries) RemoveAt(Count - 1);
    }
}

/// <summary>One UPS's readings, as the dashboard shows them.</summary>
public sealed class DashboardViewModel : ObservableObject
{
    private const double IdleThresholdW = 5;
    private bool? _grid;

    public string DeviceTitle { get; private set => Set(ref field, value); } = "No UPS paired";
    public string DeviceSubtitle { get; private set => Set(ref field, value); } = "Open Configuration → Add UPS to pair one.";
    public bool IsPaired { get; private set => Set(ref field, value); }

    public LinkStatus Link { get; private set => Set(ref field, value); } = LinkStatus.NotPaired;
    public string LinkText { get; private set => Set(ref field, value); } = "Not paired";
    public bool IsStale { get; private set => Set(ref field, value); } = true;
    public string LastUpdated { get; private set => Set(ref field, value); } = "";

    public Tone GridTone { get; private set => Set(ref field, value); }
    public string GridValue { get; private set => Set(ref field, value); } = "—";
    public string GridDetail { get; private set => Set(ref field, value); } = "";

    public Tone BatteryTone { get; private set => Set(ref field, value); }
    public double BatteryPercent { get; private set => Set(ref field, value); }
    public string BatteryValue { get; private set => Set(ref field, value); } = "—";
    public string BatteryDetail { get; private set => Set(ref field, value); } = "";

    public Tone FlowTone { get; private set => Set(ref field, value); }
    public string FlowValue { get; private set => Set(ref field, value); } = "—";
    public string FlowDetail { get; private set => Set(ref field, value); } = "";

    public string RuntimeValue { get; private set => Set(ref field, value); } = "—";
    public string RuntimeDetail { get; private set => Set(ref field, value); } = "";

    public string InputValue { get; private set => Set(ref field, value); } = "—";
    public string InputDetail { get; private set => Set(ref field, value); } = "";

    public string OutputValue { get; private set => Set(ref field, value); } = "—";
    public string OutputDetail { get; private set => Set(ref field, value); } = "";

    /// <summary>The tray's colour for this UPS (<see cref="Summary"/> says why).</summary>
    public TrayState State { get; private set => Set(ref field, value); } = TrayState.Neutral;

    /// <summary>One line about this UPS for the tray, e.g. "Grid online · 80 % · 120 W load".</summary>
    public string Summary { get; private set => Set(ref field, value); } = "No UPS paired";

    /// <param name="title">What the UPS is called in UPS Tools, if not its brand and model.</param>
    public void SetDevice(PairedUps? device, string? serial = null, string? title = null)
    {
        IsPaired = device is not null;
        if (device is null)
        {
            DeviceTitle = "No UPS paired";
            DeviceSubtitle = "Open Configuration → Add UPS to pair one.";
            SetLink(LinkStatus.NotPaired);
            ClearReadings();
            return;
        }

        if (serial is not null) _serial = serial;
        DeviceTitle = title ?? device.Title;
        DeviceSubtitle = string.Join(" · ", new[]
        {
            DeviceTitle == device.Title ? null : device.Title,
            _serial is null ? null : $"Serial {_serial}",
            device.Address,
        }.OfType<string>());
    }

    private string? _serial;

    public void SetLink(LinkStatus link)
    {
        Link = link;
        LinkText = link switch
        {
            LinkStatus.NotPaired => "Not paired",
            LinkStatus.Connecting => "Connecting…",
            LinkStatus.Online => "Connected",
            LinkStatus.Retrying => "Reconnecting…",
            LinkStatus.Unreachable => "Unreachable",
            _ => "",
        };
        IsStale = link != LinkStatus.Online;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        (State, Summary) = Link switch
        {
            LinkStatus.NotPaired => (TrayState.Neutral, "No UPS paired"),
            LinkStatus.Unreachable => (TrayState.Problem, "UPS unreachable"),
            LinkStatus.Connecting => (TrayState.Neutral, "Connecting to the UPS…"),
            _ => _grid switch
            {
                false => (TrayState.OnBattery, $"On battery · {BatteryValue} · {RuntimeValue} left"),
                true => (TrayState.GridOnline, $"Grid online · {BatteryValue} · {OutputValue} load"),
                null => (TrayState.Neutral, LinkText),
            },
        };
    }

    public void Apply(UpsStatus s)
    {
        LastUpdated = $"Updated {s.Timestamp.LocalDateTime:HH:mm:ss}";
        _grid = s.GridPresent;

        (GridTone, GridValue, GridDetail) = s.GridPresent switch
        {
            true => (Tone.Good, "Online", s.AcInputVoltage is { } v ? $"{v:0.0} V at the UPS input" : "Mains power present"),
            false => (Tone.Bad, "Lost", "Running on battery"),
            null => (Tone.Neutral, "—", "Unknown"),
        };

        BatteryPercent = s.BatteryPercent ?? 0;
        BatteryValue = s.BatteryPercent is { } soc ? $"{soc} %" : "—";
        BatteryTone = s.BatteryPercent switch
        {
            null => Tone.Neutral,
            < 20 => Tone.Bad,
            < 50 => Tone.Warning,
            _ => Tone.Good,
        };
        BatteryDetail = s.BatteryPercent is { } p && s.CapacityWh is { } cap
            ? $"≈ {cap * p / 100 / 1000:0.00} kWh of {cap / 1000:0.00} kWh stored"
            : "";

        (FlowTone, FlowValue, FlowDetail) = s.BatteryFlowW switch
        {
            null => (Tone.Neutral, "—", ""),
            > IdleThresholdW and var w => (Tone.Good, $"+{Watts(w)}", "Charging"),
            < -IdleThresholdW and var w => (Tone.Warning, $"−{Watts(-w)}", "Discharging"),
            _ => (Tone.Neutral, "0 W", "Idle (battery neither charging nor discharging)"),
        };

        if (s.EstimatedRuntime is { } runtime)
        {
            RuntimeValue = Duration(runtime);
            RuntimeDetail = s.AverageOutputW is { } average
                ? $"At {Watts(average)}, the average load of the last {LoadAverage.DefaultWindow.TotalMinutes:0} minutes"
                : $"Estimated at the current {Watts(s.TotalOutputW ?? 0)} load";
        }
        else
        {
            RuntimeValue = "—";
            RuntimeDetail = s.TotalOutputW is < 1 ? "No load" : "Not enough data";
        }

        InputValue = s.TotalInputW is { } input ? Watts(input) : "—";
        InputDetail = $"AC {Watts(s.AcInputW)} · DC {Watts(s.DcInputW)}";

        OutputValue = s.TotalOutputW is { } output ? Watts(output) : "—";
        OutputDetail = $"AC {Watts(s.AcOutputW)} · DC {Watts(s.DcOutputW)}"
                       + (s.AcOutputVoltage is { } ov ? $" · {ov:0.0} V" : "");
        UpdateSummary();
    }

    private void ClearReadings()
    {
        LastUpdated = "";
        _grid = null;
        _serial = null;
        (GridTone, GridValue, GridDetail) = (Tone.Neutral, "—", "");
        (BatteryTone, BatteryPercent, BatteryValue, BatteryDetail) = (Tone.Neutral, 0, "—", "");
        (FlowTone, FlowValue, FlowDetail) = (Tone.Neutral, "—", "");
        (RuntimeValue, RuntimeDetail) = ("—", "");
        (InputValue, InputDetail) = ("—", "");
        (OutputValue, OutputDetail) = ("—", "");
    }

    internal static string Watts(double? w) => UpsFormat.Watts(w);

    internal static string Duration(TimeSpan t) => UpsFormat.Duration(t);
}
