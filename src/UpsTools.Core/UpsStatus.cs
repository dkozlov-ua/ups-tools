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

    /// <summary>
    /// <see cref="TotalOutputW"/> averaged over the last few minutes (<see cref="LoadAverage"/>); null on a reading that
    /// hasn't been through one.
    /// </summary>
    public double? AverageOutputW { get; init; }

    public double? TotalInputW => Sum(AcInputW, DcInputW);
    public double? TotalOutputW => Sum(AcOutputW, DcOutputW);

    /// <summary>
    /// Approximate power into (+) or out of (−) the battery: total input minus total output.
    /// Conversion losses are ignored, so small values near zero mean "idle".
    /// </summary>
    public double? BatteryFlowW => TotalInputW is { } input && TotalOutputW is { } output ? input - output : null;

    /// <summary>The load <see cref="EstimatedRuntime"/> is worked out for: the average one when known, else the current one.</summary>
    public double? RuntimeLoadW => AverageOutputW ?? TotalOutputW;

    /// <summary>
    /// How long the battery would last: remaining energy divided by <see cref="RuntimeLoadW"/>, when the capacity and a
    /// real load are known. The UPS's own estimate isn't used: the Elite 100 V2's runs far longer than its battery can.
    /// </summary>
    public TimeSpan? EstimatedRuntime =>
        BatteryPercent is { } soc && CapacityWh is { } capacity && RuntimeLoadW is { } load && load >= 1
            ? TimeSpan.FromHours(capacity * soc / 100.0 / load)
            : null;

    private static double? Sum(double? a, double? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);
}
