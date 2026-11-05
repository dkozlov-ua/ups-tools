namespace UpsTools.Core;

/// <summary>When the events that depend on limits fire (<see cref="UpsStateTracker"/>).</summary>
/// <param name="BatteryLowPercent">"Battery low" once the charge is at or below this, on battery; 0: never.</param>
/// <param name="BatteryLowMinutes">"Battery low" once the time left is at or below this, on battery; 0: never.</param>
public sealed record EventRules(int BatteryLowPercent = 20, int BatteryLowMinutes = 10)
{
    /// <summary>Readings in a row with the time left at or below the limit before it counts: a short load spike doesn't.</summary>
    public const int LowRuntimeReadings = 2;
}

/// <summary>
/// Turns a stream of poll results into <see cref="UpsEventKind"/> transitions.
/// The first observation only establishes a baseline: starting the app during an outage does not fire "Grid lost".
/// "Battery low" is a state rather than a transition, so it does fire on the first reading.
/// </summary>
public sealed class UpsStateTracker(int failuresBeforeUnreachable = UpsStateTracker.DefaultFailuresBeforeUnreachable)
{
    public const int DefaultFailuresBeforeUnreachable = 3;

    private bool? _gridPresent;
    private bool? _reachable;
    private int _consecutiveFailures;
    private bool _batteryLowFired;
    private int _lowRuntimeReadings;

    public bool? GridPresent => _gridPresent;
    public bool? Reachable => _reachable;

    /// <summary>The limits; can change at any time.</summary>
    public EventRules Rules { get; set; } = new();

    public IReadOnlyList<UpsEventKind> OnSuccess(UpsStatus status)
    {
        var events = new List<UpsEventKind>(2);
        _consecutiveFailures = 0;

        if (_reachable == false) events.Add(UpsEventKind.UpsReachable);
        _reachable = true;

        if (status.GridPresent is { } grid)
        {
            if (_gridPresent is { } previous && previous != grid)
                events.Add(grid ? UpsEventKind.GridRestored : UpsEventKind.GridLost);
            _gridPresent = grid;
        }

        if (_gridPresent == true)
        {
            _batteryLowFired = false; // the next outage can fire it again
            _lowRuntimeReadings = 0;
        }
        else if (_gridPresent == false && !_batteryLowFired && IsBatteryLow(status))
        {
            _batteryLowFired = true;
            events.Add(UpsEventKind.BatteryLow);
        }

        return events;
    }

    private bool IsBatteryLow(UpsStatus status)
    {
        var rules = Rules;
        var lowRuntime = rules.BatteryLowMinutes > 0 && status.EstimatedRuntime is { } left && left.TotalMinutes <= rules.BatteryLowMinutes;
        _lowRuntimeReadings = lowRuntime ? _lowRuntimeReadings + 1 : 0;

        return rules.BatteryLowPercent > 0 && status.BatteryPercent is { } soc && soc <= rules.BatteryLowPercent
               || _lowRuntimeReadings >= EventRules.LowRuntimeReadings;
    }

    public IReadOnlyList<UpsEventKind> OnFailure()
    {
        _consecutiveFailures++;
        if (_consecutiveFailures < failuresBeforeUnreachable || _reachable == false) return [];
        _reachable = false;
        return [UpsEventKind.UpsUnreachable];
    }

    /// <summary>Forget the failure streak, e.g. after the PC resumed from sleep and the link is expected to be down briefly.</summary>
    public void ResetFailures() => _consecutiveFailures = 0;
}
