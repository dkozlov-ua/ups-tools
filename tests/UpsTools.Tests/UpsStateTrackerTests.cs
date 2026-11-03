using UpsTools.Core;

namespace UpsTools.Tests;

public class UpsStateTrackerTests
{
    private static UpsStatus Status(bool? grid) => new() { Timestamp = DateTimeOffset.UnixEpoch, GridPresent = grid };

    [Fact]
    public void First_reading_is_a_baseline_without_events()
    {
        var tracker = new UpsStateTracker();
        Assert.Empty(tracker.OnSuccess(Status(false)));
        Assert.Equal(false, tracker.GridPresent);
    }

    [Fact]
    public void Grid_transitions_fire_once_each()
    {
        var tracker = new UpsStateTracker();
        tracker.OnSuccess(Status(true));

        Assert.Equal([UpsEventKind.GridLost], tracker.OnSuccess(Status(false)));
        Assert.Empty(tracker.OnSuccess(Status(false)));
        Assert.Equal([UpsEventKind.GridRestored], tracker.OnSuccess(Status(true)));
        Assert.Empty(tracker.OnSuccess(Status(true)));
    }

    [Fact]
    public void Unknown_grid_reading_keeps_previous_state()
    {
        var tracker = new UpsStateTracker();
        tracker.OnSuccess(Status(true));
        Assert.Empty(tracker.OnSuccess(Status(null)));
        Assert.Equal([UpsEventKind.GridLost], tracker.OnSuccess(Status(false)));
    }

    [Fact]
    public void Unreachable_fires_after_consecutive_failures_and_only_once()
    {
        var tracker = new UpsStateTracker(failuresBeforeUnreachable: 3);
        tracker.OnSuccess(Status(true));

        Assert.Empty(tracker.OnFailure());
        Assert.Empty(tracker.OnFailure());
        Assert.Equal([UpsEventKind.UpsUnreachable], tracker.OnFailure());
        Assert.Empty(tracker.OnFailure());
    }

    [Fact]
    public void A_success_resets_the_failure_streak()
    {
        var tracker = new UpsStateTracker(failuresBeforeUnreachable: 3);
        tracker.OnFailure();
        tracker.OnFailure();
        tracker.OnSuccess(Status(true));
        tracker.OnFailure();
        Assert.Empty(tracker.OnFailure());
    }

    [Fact]
    public void Never_reached_ups_still_becomes_unreachable()
    {
        var tracker = new UpsStateTracker(failuresBeforeUnreachable: 2);
        tracker.OnFailure();
        Assert.Equal([UpsEventKind.UpsUnreachable], tracker.OnFailure());
    }

    [Fact]
    public void Recovery_reports_reachable_then_grid_change_seen_during_the_gap()
    {
        var tracker = new UpsStateTracker(failuresBeforeUnreachable: 1);
        tracker.OnSuccess(Status(true));
        tracker.OnFailure();

        Assert.Equal([UpsEventKind.UpsReachable, UpsEventKind.GridLost], tracker.OnSuccess(Status(false)));
    }
}
