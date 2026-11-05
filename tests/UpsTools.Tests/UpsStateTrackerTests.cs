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

    // ---- waiting out a grid loss

    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static UpsStatus At(double seconds, bool? grid, int battery = 80) =>
        new() { Timestamp = T0.AddSeconds(seconds), GridPresent = grid, BatteryPercent = battery };

    private static UpsStateTracker WithDelay(int seconds) => new() { Rules = new EventRules(GridLostDelaySeconds: seconds) };

    [Fact]
    public void Grid_lost_waits_until_the_grid_has_been_gone_for_the_delay()
    {
        var tracker = WithDelay(30);
        tracker.OnSuccess(At(0, true));

        Assert.Empty(tracker.OnSuccess(At(2, false)));
        Assert.Equal(true, tracker.GridPresent);
        Assert.Equal(T0.AddSeconds(2), tracker.GridGoneSince);
        Assert.Equal(T0.AddSeconds(32), tracker.GridLostDue);

        Assert.Empty(tracker.OnSuccess(At(31, false)));
        Assert.Equal([UpsEventKind.GridLost], tracker.OnSuccess(At(32, false)));
        Assert.Equal(false, tracker.GridPresent);
        Assert.Equal(T0.AddSeconds(2), tracker.GridGoneSince); // when the outage started
        Assert.Null(tracker.GridLostDue);

        Assert.Empty(tracker.OnSuccess(At(40, false)));
        Assert.Equal([UpsEventKind.GridRestored], tracker.OnSuccess(At(50, true)));
        Assert.Null(tracker.GridGoneSince);
        Assert.Null(tracker.IgnoredGridLoss);
    }

    [Fact]
    public void A_shorter_blip_fires_nothing_and_says_how_long_it_was()
    {
        var tracker = WithDelay(30);
        tracker.OnSuccess(At(0, true));
        tracker.OnSuccess(At(2, false));
        tracker.OnSuccess(At(6, null)); // not known: still waiting

        Assert.Empty(tracker.OnSuccess(At(10, true)));
        Assert.Equal(TimeSpan.FromSeconds(8), tracker.IgnoredGridLoss);
        Assert.Null(tracker.GridGoneSince);
        Assert.Null(tracker.GridLostDue);

        Assert.Empty(tracker.OnSuccess(At(12, true)));
        Assert.Null(tracker.IgnoredGridLoss); // only the reading that ended it says so

        // The next loss is timed from its own start.
        Assert.Empty(tracker.OnSuccess(At(20, false)));
        Assert.Empty(tracker.OnSuccess(At(45, false)));
        Assert.Equal([UpsEventKind.GridLost], tracker.OnSuccess(At(50, false)));
    }

    [Fact]
    public void Battery_low_waits_for_grid_lost()
    {
        var tracker = WithDelay(30);
        tracker.OnSuccess(At(0, true, 10));
        Assert.Empty(tracker.OnSuccess(At(2, false, 10)));
        Assert.Equal([UpsEventKind.GridLost, UpsEventKind.BatteryLow], tracker.OnSuccess(At(40, false, 9)));
    }

    [Fact]
    public void A_shorter_delay_applies_to_a_loss_already_being_waited_out()
    {
        var tracker = WithDelay(300);
        tracker.OnSuccess(At(0, true));
        tracker.OnSuccess(At(2, false));
        Assert.Empty(tracker.OnSuccess(At(20, false)));

        tracker.Rules = new EventRules(GridLostDelaySeconds: 10);
        Assert.Equal(T0.AddSeconds(12), tracker.GridLostDue);
        Assert.Equal([UpsEventKind.GridLost], tracker.OnSuccess(At(22, false)));
    }

    [Fact]
    public void The_wait_goes_on_while_the_ups_does_not_answer()
    {
        var tracker = new UpsStateTracker(failuresBeforeUnreachable: 1) { Rules = new EventRules(GridLostDelaySeconds: 30) };
        tracker.OnSuccess(At(0, true));
        tracker.OnSuccess(At(2, false));
        Assert.Equal([UpsEventKind.UpsUnreachable], tracker.OnFailure());
        Assert.Equal([UpsEventKind.UpsReachable, UpsEventKind.GridLost], tracker.OnSuccess(At(60, false)));
    }

    [Fact]
    public void Starting_during_an_outage_is_still_a_baseline_with_a_delay()
    {
        var tracker = WithDelay(30);
        Assert.Empty(tracker.OnSuccess(At(0, false)));
        Assert.Empty(tracker.OnSuccess(At(60, false)));
        Assert.Null(tracker.GridLostDue);
        Assert.Equal([UpsEventKind.GridRestored], tracker.OnSuccess(At(70, true)));
    }

    [Fact]
    public void The_delay_is_off_by_default_and_never_negative()
    {
        Assert.Equal(TimeSpan.Zero, new EventRules().GridLostDelay);
        Assert.Equal(TimeSpan.Zero, new EventRules(GridLostDelaySeconds: -5).GridLostDelay);
        Assert.Equal(TimeSpan.FromSeconds(45), new EventRules(GridLostDelaySeconds: 45).GridLostDelay);
    }
}
