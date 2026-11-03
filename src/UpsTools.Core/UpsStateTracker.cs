namespace UpsTools.Core;

/// <summary>
/// Turns a stream of poll results into <see cref="UpsEventKind"/> transitions.
/// The first observation only establishes a baseline: starting the app during an outage does not fire "Grid lost".
/// </summary>
public sealed class UpsStateTracker(int failuresBeforeUnreachable = UpsStateTracker.DefaultFailuresBeforeUnreachable)
{
    public const int DefaultFailuresBeforeUnreachable = 3;

    private bool? _gridPresent;
    private bool? _reachable;
    private int _consecutiveFailures;

    public bool? GridPresent => _gridPresent;
    public bool? Reachable => _reachable;

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

        return events;
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
