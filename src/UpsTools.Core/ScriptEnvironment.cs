using System.Globalization;

namespace UpsTools.Core;

/// <summary>
/// The environment variables a script gets: what happened and the UPS's state at that moment, so one script can
/// serve several events or pass the state on. A value the UPS doesn't report (or that doesn't apply) is removed
/// from the environment rather than left empty.
/// </summary>
public static class ScriptEnvironment
{
    /// <summary>The event: GridLost, BatteryLow, GridRestored, UpsUnreachable or UpsReachable.</summary>
    public const string Event = "UPS_EVENT";

    /// <summary>1 when started by Run now rather than by the event itself.</summary>
    public const string Manual = "UPS_MANUAL";

    /// <summary>1 while mains power is present at the UPS input, 0 on battery.</summary>
    public const string Grid = "UPS_GRID";

    /// <summary>1 while the UPS answers, 0 when it is unreachable (the readings are then its last ones).</summary>
    public const string Reachable = "UPS_REACHABLE";

    public const string BatteryPercent = "UPS_BATTERY_PERCENT";

    /// <summary>Estimated minutes left on battery at the current load.</summary>
    public const string RuntimeMinutes = "UPS_RUNTIME_MINUTES";

    /// <summary>Output power, in watts.</summary>
    public const string LoadWatts = "UPS_LOAD_W";

    /// <summary>For Grid restored, how long the outage lasted; on battery, how long it has lasted so far.</summary>
    public const string OutageSeconds = "UPS_OUTAGE_SECONDS";

    public const string Model = "UPS_MODEL";
    public const string Serial = "UPS_SERIAL";

    /// <summary>When the readings were taken, ISO 8601 with the UTC offset.</summary>
    public const string ReadingTime = "UPS_READING_TIME";

    public static IReadOnlyList<string> Names { get; } =
        [Event, Manual, Grid, Reachable, BatteryPercent, RuntimeMinutes, LoadWatts, OutageSeconds, Model, Serial, ReadingTime];

    /// <param name="kind">The event the scripts belong to.</param>
    /// <param name="status">The reading at the event, or the last one.</param>
    /// <param name="reachable">Whether the UPS answers; null when not known (nothing is monitored).</param>
    /// <param name="outage">How long the grid has been, or was, gone.</param>
    /// <param name="manual">Started by Run now.</param>
    /// <returns>Every name in <see cref="Names"/>: null removes it.</returns>
    public static IReadOnlyDictionary<string, string?> For(UpsEventKind? kind, UpsStatus? status, bool? reachable,
        TimeSpan? outage, bool manual)
    {
        static string? Number(double? value) => value is { } v ? Math.Floor(v).ToString("0", CultureInfo.InvariantCulture) : null;
        static string? Flag(bool? value) => value is { } v ? v ? "1" : "0" : null;

        return new Dictionary<string, string?>
        {
            [Event] = kind?.ToString(),
            [Manual] = manual ? "1" : null,
            [Grid] = Flag(status?.GridPresent),
            [Reachable] = Flag(reachable),
            [BatteryPercent] = status?.BatteryPercent?.ToString(CultureInfo.InvariantCulture),
            [RuntimeMinutes] = Number(status?.EstimatedRuntime?.TotalMinutes),
            [LoadWatts] = Number(status?.TotalOutputW is { } w ? Math.Round(w) : null),
            [OutageSeconds] = Number(outage is { } o && o >= TimeSpan.Zero ? o.TotalSeconds : null),
            [Model] = status?.Model,
            [Serial] = status?.Serial,
            [ReadingTime] = status?.Timestamp.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>No UPS state at all: every variable removed (e.g. a script started from elsewhere).</summary>
    public static IReadOnlyDictionary<string, string?> None { get; } = Names.ToDictionary(n => n, _ => (string?)null);
}
