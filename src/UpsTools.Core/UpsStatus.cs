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

    /// <summary>Remaining energy divided by the current output power, when the capacity and a real load are known.</summary>
    public TimeSpan? CalculatedRuntime =>
        BatteryPercent is { } soc && CapacityWh is { } capacity && TotalOutputW is { } load && load >= 1
            ? TimeSpan.FromHours(capacity * soc / 100.0 / load)
            : null;

    /// <summary>
    /// How long the battery would last at the current load: the lower of the device's own figure and
    /// <see cref="CalculatedRuntime"/>, so an optimistic device estimate can't delay "Battery low".
    /// </summary>
    public TimeSpan? EstimatedRuntime => UsesReportedRuntime ? ReportedRuntime : CalculatedRuntime;

    /// <summary>Whether <see cref="EstimatedRuntime"/> is the device's own figure.</summary>
    public bool UsesReportedRuntime =>
        ReportedRuntime is { } reported && (CalculatedRuntime is not { } calculated || reported <= calculated);

    private static double? Sum(double? a, double? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);
}
