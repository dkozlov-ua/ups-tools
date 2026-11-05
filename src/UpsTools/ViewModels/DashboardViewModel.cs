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

public sealed class DashboardViewModel : ObservableObject
{
    private const int MaxActivity = 100;
    private const double IdleThresholdW = 5;

    public ObservableCollection<LogEntry> Activity { get; } = [];

    public string DeviceTitle { get; private set => Set(ref field, value); } = "No UPS paired";
    public string DeviceSubtitle { get; private set => Set(ref field, value); } = "Open Configuration → Find UPS to pair one.";
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

    public void SetDevice(PairedUps? device, string? serial = null)
    {
        IsPaired = device is not null;
        if (device is null)
        {
            DeviceTitle = "No UPS paired";
            DeviceSubtitle = "Open Configuration → Find UPS to pair one.";
            SetLink(LinkStatus.NotPaired);
            ClearReadings();
            return;
        }

        DeviceTitle = $"Bluetti {device.Model}";
        DeviceSubtitle = serial is null ? device.Address : $"Serial {serial} · {device.Address}";
    }

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
    }

    public void Apply(UpsStatus s)
    {
        LastUpdated = $"Updated {s.Timestamp.LocalDateTime:HH:mm:ss}";

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
    }

    public void AddActivity(LogEntry entry)
    {
        Activity.Insert(0, entry);
        while (Activity.Count > MaxActivity) Activity.RemoveAt(Activity.Count - 1);
    }

    private void ClearReadings()
    {
        LastUpdated = "";
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
