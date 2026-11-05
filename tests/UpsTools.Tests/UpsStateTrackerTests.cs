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

    [Fact]
    public void Reset_failures_forgives_the_streak_but_not_an_outage()
    {
        var tracker = new UpsStateTracker(failuresBeforeUnreachable: 3);
        tracker.OnSuccess(Status(true));
        tracker.OnFailure();
        tracker.OnFailure();
        tracker.ResetFailures();
        Assert.Empty(tracker.OnFailure());
        Assert.Empty(tracker.OnFailure());
        Assert.Equal([UpsEventKind.UpsUnreachable], tracker.OnFailure());

        tracker.ResetFailures();
        Assert.Empty(tracker.OnFailure()); // still unreachable: no second event
        Assert.Equal(false, tracker.Reachable);
        Assert.Equal([UpsEventKind.UpsReachable], tracker.OnSuccess(Status(true)));
    }

    [Fact]
    public void Starting_without_a_grid_reading_waits_for_one_as_the_baseline()
    {
        var tracker = new UpsStateTracker();
        Assert.Empty(tracker.OnSuccess(Status(null)));
        Assert.Null(tracker.GridPresent);
        Assert.Equal(true, tracker.Reachable);
        Assert.Empty(tracker.OnSuccess(Status(false)));
        Assert.Equal([UpsEventKind.GridRestored], tracker.OnSuccess(Status(true)));
    }

    [Fact]
    public void The_default_is_three_failures()
    {
        var tracker = new UpsStateTracker();
        Assert.Empty(tracker.OnFailure());
        Assert.Empty(tracker.OnFailure());
        Assert.Equal([UpsEventKind.UpsUnreachable], tracker.OnFailure());
    }

    // ---- Battery low

    private static UpsStatus Reading(bool? grid, int? battery = 80, double? minutesLeft = null) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch,
        GridPresent = grid,
        BatteryPercent = battery,
        ReportedRuntime = minutesLeft is { } m ? TimeSpan.FromMinutes(m) : null,
    };

    [Fact]
    public void Battery_low_fires_once_per_outage_at_the_charge_limit()
    {
        var tracker = new UpsStateTracker { Rules = new EventRules(BatteryLowPercent: 20, BatteryLowMinutes: 0) };
        tracker.OnSuccess(Reading(true, 25));

        Assert.Equal([UpsEventKind.GridLost], tracker.OnSuccess(Reading(false, 21)));
        Assert.Equal([UpsEventKind.BatteryLow], tracker.OnSuccess(Reading(false, 20)));
        Assert.Empty(tracker.OnSuccess(Reading(false, 19)));
        Assert.Empty(tracker.OnSuccess(Reading(false, 22))); // charging up a little doesn't re-arm it
        Assert.Empty(tracker.OnSuccess(Reading(false, 15)));

        Assert.Equal([UpsEventKind.GridRestored], tracker.OnSuccess(Reading(true, 15)));
        Assert.Equal([UpsEventKind.GridLost, UpsEventKind.BatteryLow], tracker.OnSuccess(Reading(false, 15))); // the next outage
    }

    [Fact]
    public void Battery_low_never_fires_on_grid_power()
    {
        var tracker = new UpsStateTracker();
        tracker.OnSuccess(Reading(true, 5, minutesLeft: 1));
        Assert.Empty(tracker.OnSuccess(Reading(true, 1, minutesLeft: 1)));
        Assert.Empty(tracker.OnSuccess(Reading(null, 1, minutesLeft: 1))); // still on grid as far as we know
    }

    [Fact]
    public void Battery_low_fires_on_the_first_reading_when_the_app_starts_during_an_outage_on_a_low_battery()
    {
        var tracker = new UpsStateTracker();
        Assert.Equal([UpsEventKind.BatteryLow], tracker.OnSuccess(Reading(false, 10)));
        Assert.Empty(tracker.OnSuccess(Reading(false, 9)));
    }

    [Fact]
    public void A_reading_without_the_grid_state_keeps_the_outage_going()
    {
        var tracker = new UpsStateTracker();
        tracker.OnSuccess(Reading(true));
        tracker.OnSuccess(Reading(false, 50));
        Assert.Equal([UpsEventKind.BatteryLow], tracker.OnSuccess(Reading(null, 20)));
    }

    [Fact]
    public void The_time_left_must_be_low_in_two_readings_in_a_row()
    {
        var tracker = new UpsStateTracker { Rules = new EventRules(BatteryLowPercent: 0, BatteryLowMinutes: 10) };
        tracker.OnSuccess(Reading(true));
        tracker.OnSuccess(Reading(false, 50, minutesLeft: 60));

        Assert.Empty(tracker.OnSuccess(Reading(false, 50, minutesLeft: 8))); // a load spike
        Assert.Empty(tracker.OnSuccess(Reading(false, 50, minutesLeft: 40)));
        Assert.Empty(tracker.OnSuccess(Reading(false, 50, minutesLeft: 10)));
        Assert.Empty(tracker.OnSuccess(Reading(false, 50, minutesLeft: null))); // unknown breaks the streak too
        Assert.Empty(tracker.OnSuccess(Reading(false, 50, minutesLeft: 9)));
        Assert.Equal([UpsEventKind.BatteryLow], tracker.OnSuccess(Reading(false, 50, minutesLeft: 9.5)));
    }

    [Fact]
    public void The_time_left_streak_starts_over_after_an_outage()
    {
        var tracker = new UpsStateTracker { Rules = new EventRules(BatteryLowPercent: 0, BatteryLowMinutes: 10) };
        tracker.OnSuccess(Reading(true));
        tracker.OnSuccess(Reading(false, 50, minutesLeft: 5));
        tracker.OnSuccess(Reading(true));
        Assert.Equal([UpsEventKind.GridLost], tracker.OnSuccess(Reading(false, 50, minutesLeft: 5)));
        Assert.Equal([UpsEventKind.BatteryLow], tracker.OnSuccess(Reading(false, 50, minutesLeft: 5)));
    }

    [Fact]
    public void A_limit_of_zero_is_off()
    {
        var tracker = new UpsStateTracker { Rules = new EventRules(0, 0) };
        tracker.OnSuccess(Reading(true));
        tracker.OnSuccess(Reading(false, 1, minutesLeft: 0.5));
        Assert.Empty(tracker.OnSuccess(Reading(false, 0, minutesLeft: 0.1)));
    }

    [Fact]
    public void New_limits_apply_from_the_next_reading()
    {
        var tracker = new UpsStateTracker { Rules = new EventRules(10, 0) };
        tracker.OnSuccess(Reading(true));
        Assert.Equal([UpsEventKind.GridLost], tracker.OnSuccess(Reading(false, 30)));

        tracker.Rules = new EventRules(30, 0);
        Assert.Equal([UpsEventKind.BatteryLow], tracker.OnSuccess(Reading(false, 30)));
    }

    [Fact]
    public void The_defaults_are_20_percent_or_10_minutes()
    {
        Assert.Equal(new EventRules(20, 10), new UpsStateTracker().Rules);
        Assert.Equal(2, EventRules.LowRuntimeReadings);
    }

    [Fact]
    public void Battery_low_comes_after_reachable_again_and_grid_lost_from_the_same_reading()
    {
        var tracker = new UpsStateTracker(failuresBeforeUnreachable: 1);
        tracker.OnSuccess(Reading(true));
        tracker.OnFailure();
        Assert.Equal([UpsEventKind.UpsReachable, UpsEventKind.GridLost, UpsEventKind.BatteryLow], tracker.OnSuccess(Reading(false, 5)));
    }
}
