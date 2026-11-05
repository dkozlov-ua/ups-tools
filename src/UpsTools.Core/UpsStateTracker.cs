namespace UpsTools.Core;

/// <summary>When the events that depend on limits fire (<see cref="UpsStateTracker"/>).</summary>
/// <param name="BatteryLowPercent">"Battery low" once the charge is at or below this, on battery; 0: never.</param>
/// <param name="BatteryLowMinutes">"Battery low" once the time left is at or below this, on battery; 0: never.</param>
/// <param name="GridLostDelaySeconds">
/// "Grid lost" only once the grid has been gone this long: a shorter blip fires nothing; 0: at the first reading
/// without it.
/// </param>
public sealed record EventRules(int BatteryLowPercent = 20, int BatteryLowMinutes = 10, int GridLostDelaySeconds = 0)
{
    public TimeSpan GridLostDelay => TimeSpan.FromSeconds(Math.Max(0, GridLostDelaySeconds));

    /// <summary>Readings in a row with the time left at or below the limit before it counts: a short load spike doesn't.</summary>
    public const int LowRuntimeReadings = 2;
}

/// <summary>
/// Turns a stream of poll results into <see cref="UpsEventKind"/> transitions.
/// The first observation only establishes a baseline: starting the app during an outage does not fire "Grid lost".
/// "Battery low" is a state rather than a transition, so it does fire on the first reading.
/// </summary>
/// <remarks>
/// Time is the readings' <see cref="UpsStatus.Timestamp"/>: with <see cref="EventRules.GridLostDelaySeconds"/>,
/// "Grid lost" fires with the first reading at least that long after the first one without the grid.
/// </remarks>
public sealed class UpsStateTracker(int failuresBeforeUnreachable = UpsStateTracker.DefaultFailuresBeforeUnreachable)
{
    public const int DefaultFailuresBeforeUnreachable = 3;

    private bool? _gridPresent;
    private bool? _reachable;
    private int _consecutiveFailures;
    private bool _batteryLowFired;
    private int _lowRuntimeReadings;
    private DateTimeOffset? _gridGoneSince;

    /// <summary>The grid state the events have reported: during a <see cref="EventRules.GridLostDelay"/>, still true.</summary>
    public bool? GridPresent => _gridPresent;
    public bool? Reachable => _reachable;

    /// <summary>When the first reading without the grid came, while it is waited out or once "Grid lost" fired.</summary>
    public DateTimeOffset? GridGoneSince => _gridGoneSince;

    /// <summary>While waiting out a grid loss: when "Grid lost" fires if the grid stays gone.</summary>
    public DateTimeOffset? GridLostDue => _gridPresent == true && _gridGoneSince is { } since ? since + Rules.GridLostDelay : null;

    /// <summary>Set by the last reading when it ended a grid loss shorter than the delay: how long it lasted.</summary>
    public TimeSpan? IgnoredGridLoss { get; private set; }

    /// <summary>The limits; can change at any time.</summary>
    public EventRules Rules { get; set; } = new();

    public IReadOnlyList<UpsEventKind> OnSuccess(UpsStatus status)
    {
        var events = new List<UpsEventKind>(2);
        _consecutiveFailures = 0;

        if (_reachable == false) events.Add(UpsEventKind.UpsReachable);
        _reachable = true;

        IgnoredGridLoss = null;
        if (status.GridPresent is { } grid)
        {
            if (_gridPresent is not { } previous)
            {
                _gridPresent = grid; // the baseline
                if (!grid) _gridGoneSince = status.Timestamp;
            }
            else if (!grid)
            {
                _gridGoneSince ??= status.Timestamp;
                if (previous && status.Timestamp - _gridGoneSince >= Rules.GridLostDelay)
                {
                    events.Add(UpsEventKind.GridLost);
                    _gridPresent = false;
                }
            }
            else
            {
                if (!previous) events.Add(UpsEventKind.GridRestored);
                else if (_gridGoneSince is { } since) IgnoredGridLoss = status.Timestamp - since;
                _gridPresent = true;
                _gridGoneSince = null;
            }
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
