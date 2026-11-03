namespace UpsTools.Core;

/// <summary>Vendor-neutral snapshot of a UPS. Values are null when the device doesn't report them.</summary>
public sealed record UpsStatus
{
    public required DateTimeOffset Timestamp { get; init; }

    public string? Model { get; init; }
    public string? Serial { get; init; }

    /// <summary>True while mains power is present at the UPS input.</summary>
    public bool? GridPresent { get; init; }

    public int? BatteryPercent { get; init; }
    public double? CapacityWh { get; init; }

    public double? AcInputVoltage { get; init; }
    public double? AcOutputVoltage { get; init; }

    public double? AcInputW { get; init; }
    public double? DcInputW { get; init; }
    public double? AcOutputW { get; init; }
    public double? DcOutputW { get; init; }

    /// <summary>Runtime as reported by the device itself, if it reports a plausible value.</summary>
    public TimeSpan? ReportedRuntime { get; init; }

    public double? TotalInputW => Sum(AcInputW, DcInputW);
    public double? TotalOutputW => Sum(AcOutputW, DcOutputW);

    /// <summary>
    /// Approximate power into (+) or out of (−) the battery: total input minus total output.
    /// Conversion losses are ignored, so small values near zero mean "idle".
    /// </summary>
    public double? BatteryFlowW => TotalInputW is { } input && TotalOutputW is { } output ? input - output : null;

    /// <summary>
    /// How long the battery would last at the current load: the device's own figure when it reports one,
    /// otherwise remaining energy divided by the current output power.
    /// </summary>
    public TimeSpan? EstimatedRuntime
    {
        get
        {
            if (ReportedRuntime is { } reported) return reported;
            if (BatteryPercent is not { } soc || CapacityWh is not { } capacity || TotalOutputW is not { } load || load < 1)
                return null;
            return TimeSpan.FromHours(capacity * soc / 100.0 / load);
        }
    }

    private static double? Sum(double? a, double? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);
}
