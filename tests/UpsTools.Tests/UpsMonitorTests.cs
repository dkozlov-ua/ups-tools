using System.Collections.Concurrent;
using System.Threading.Channels;
using UpsTools.Core;
using UpsTools.Tests.Support;

namespace UpsTools.Tests;

public class UpsMonitorTests
{
    /// <summary>Each read takes the next step the test queues, and waits for one if there is none yet.</summary>
    private sealed class ScriptedConnection : IUpsConnection
    {
        private readonly Channel<Func<CancellationToken, Task<UpsStatus>>> _steps = Channel.CreateUnbounded<Func<CancellationToken, Task<UpsStatus>>>();

        public int Reads;
        public int Resets;
        public bool Disposed;

        public void Answer(bool? grid, int battery = 80) =>
            _steps.Writer.TryWrite(_ => Task.FromResult(Status(grid, battery)));

        public void Fail(string message = "boom") =>
            _steps.Writer.TryWrite(_ => Task.FromException<UpsStatus>(new IOException(message)));

        /// <summary>A read that never answers, until it is cancelled.</summary>
        public void Hang() =>
            _steps.Writer.TryWrite(async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException();
            });

        public async Task<UpsStatus> ReadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Reads);
            var step = await _steps.Reader.ReadAsync(cancellationToken);
            return await step(cancellationToken);
        }

        public Task ResetAsync()
        {
            Interlocked.Increment(ref Resets);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static UpsStatus Status(bool? grid, int battery = 80) =>
        new() { Timestamp = DateTimeOffset.Now, GridPresent = grid, BatteryPercent = battery };

    private readonly ScriptedConnection _ups = new();
    private readonly ConcurrentQueue<UpsStatus> _statuses = new();
    private readonly ConcurrentQueue<(UpsEventKind Kind, UpsStatus? Status)> _events = new();
    private readonly ConcurrentQueue<UpsLinkState> _states = new();
    private readonly ConcurrentQueue<UpsEvent> _raised = new();
    private readonly ConcurrentQueue<string> _log = new();

    private UpsMonitor Start(TimeSpan? period = null, TimeSpan? readTimeout = null)
    {
        var monitor = new UpsMonitor(_ups, period ?? TimeSpan.Zero, readTimeout);
        monitor.StatusUpdated += _statuses.Enqueue;
        monitor.EventRaised += e => _events.Enqueue((e.Kind, e.Status));
        monitor.EventRaised += _raised.Enqueue;
        monitor.LinkStateChanged += _states.Enqueue;
        monitor.Log += _log.Enqueue;
        monitor.Start();
        return monitor;
    }

    [Fact]
    public async Task Readings_are_reported_and_the_link_goes_online()
    {
        await using var monitor = Start();
        Assert.Equal(UpsLinkState.Connecting, monitor.LinkState);

        _ups.Answer(true, 90);
        await Wait.Until(() => _statuses.Count == 1);

        Assert.Equal(UpsLinkState.Online, monitor.LinkState);
        Assert.Equal([UpsLinkState.Online], _states);
        Assert.Equal(90, monitor.LastStatus!.BatteryPercent);
        Assert.Empty(_events); // the first reading is a baseline
    }

    [Fact]
    public async Task Grid_transitions_are_raised_with_their_reading()
    {
        await using var monitor = Start();
        _ups.Answer(true);
        _ups.Answer(false, 70);
        _ups.Answer(false);
        _ups.Answer(true, 71);
        await Wait.Until(() => _statuses.Count == 4);

        Assert.Equal([UpsEventKind.GridLost, UpsEventKind.GridRestored], _events.Select(e => e.Kind));
        Assert.Equal([70, 71], _events.Select(e => e.Status!.BatteryPercent));
    }

    [Fact]
    public async Task Battery_low_follows_the_rules_it_is_given()
    {
        await using var monitor = Start();
        Assert.Equal(new EventRules(), monitor.Rules);
        monitor.Rules = new EventRules(BatteryLowPercent: 50, BatteryLowMinutes: 0);
        _ups.Answer(true, 60);
        _ups.Answer(false, 55);
        _ups.Answer(false, 50);
        await Wait.Until(() => _statuses.Count == 3);

        Assert.Equal([UpsEventKind.GridLost, UpsEventKind.BatteryLow], _events.Select(e => e.Kind));
        Assert.Equal(50, _events.Last().Status!.BatteryPercent);
    }

    [Fact]
    public async Task Waiting_out_a_grid_loss_it_polls_again_when_grid_lost_is_due()
    {
        await using var monitor = Start(TimeSpan.FromMinutes(10));
        monitor.Rules = new EventRules(GridLostDelaySeconds: 1);
        _ups.Answer(true);
        await Wait.Until(() => _statuses.Count == 1);
        monitor.PollNow();
        _ups.Answer(false);
        await Wait.Until(() => _statuses.Count == 2);
        Assert.Empty(_events);
        Assert.Contains("The grid is gone; Grid lost fires if it stays gone for 1 s", _log);

        _ups.Answer(false); // read about a second later, not in 10 minutes
        await Wait.Until(() => !_events.IsEmpty, seconds: 5);

        var lost = Assert.Single(_raised);
        Assert.Equal(UpsEventKind.GridLost, lost.Kind);
        Assert.Equal(_statuses.ElementAt(1).Timestamp, lost.GridGoneSince);
        Assert.True(lost.Status!.Timestamp - lost.GridGoneSince >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_blip_shorter_than_the_delay_is_only_logged()
    {
        await using var monitor = Start();
        monitor.Rules = new EventRules(GridLostDelaySeconds: 600);
        _ups.Answer(true);
        _ups.Answer(false);
        _ups.Answer(true);
        await Wait.Until(() => _statuses.Count == 3);

        Assert.Empty(_events);
        Assert.Matches(@"^The grid came back after \d+ s, before Grid lost was due$", _log.Last());
    }

    [Fact]
    public async Task Without_a_delay_other_events_carry_no_start_time()
    {
        await using var monitor = Start();
        _ups.Answer(true);
        _ups.Answer(false);
        _ups.Answer(true);
        await Wait.Until(() => _statuses.Count == 3);
        Assert.Equal([UpsEventKind.GridLost, UpsEventKind.GridRestored], _raised.Select(e => e.Kind));
        Assert.Equal(_statuses.ElementAt(1).Timestamp, _raised.First().GridGoneSince);
        Assert.Null(_raised.Last().GridGoneSince);
        Assert.Empty(_log);
    }

    [Fact]
    public async Task Failures_before_the_first_reading_keep_connecting_then_unreachable()
    {
        await using var monitor = Start();
        _ups.Fail();
        _ups.Fail();
        await Wait.Until(() => _ups.Reads >= 3);
        Assert.Equal(UpsLinkState.Connecting, monitor.LinkState);
        Assert.Empty(_states);

        _ups.Fail();
        await Wait.Until(() => !_events.IsEmpty);

        Assert.Equal(UpsLinkState.Unreachable, monitor.LinkState);
        Assert.Equal((UpsEventKind.UpsUnreachable, (UpsStatus?)null), Assert.Single(_events));
        Assert.Equal(["UPS read failed: boom"], _log); // the same error is logged once
    }

    [Fact]
    public async Task Failures_after_a_reading_retry_then_recover()
    {
        await using var monitor = Start();
        _ups.Answer(true);
        _ups.Fail("first");
        _ups.Fail("second");
        await Wait.Until(() => _log.Count == 2);
        Assert.Equal(UpsLinkState.Retrying, monitor.LinkState);

        _ups.Fail("second");
        _ups.Answer(false);
        await Wait.Until(() => _statuses.Count == 2);

        Assert.Equal([UpsLinkState.Online, UpsLinkState.Retrying, UpsLinkState.Unreachable, UpsLinkState.Online], _states);
        Assert.Equal([UpsEventKind.UpsUnreachable, UpsEventKind.UpsReachable, UpsEventKind.GridLost], _events.Select(e => e.Kind));
        Assert.Equal(["UPS read failed: first", "UPS read failed: second"], _log);
        Assert.False(monitor.LastStatus!.GridPresent);
    }

    [Fact]
    public async Task A_read_that_takes_too_long_counts_as_failed()
    {
        await using var monitor = Start(readTimeout: TimeSpan.FromMilliseconds(50));
        _ups.Hang();
        await Wait.Until(() => !_log.IsEmpty);
        Assert.Equal("UPS read failed: timed out", Assert.Single(_log));
    }

    [Fact]
    public async Task It_polls_once_per_period_and_PollNow_skips_the_wait()
    {
        await using var monitor = Start(TimeSpan.FromMinutes(10));
        _ups.Answer(true);
        _ups.Answer(true);
        await Wait.Until(() => _statuses.Count == 1);
        await Task.Delay(200);
        Assert.Single(_statuses);

        monitor.PollNow();
        await Wait.Until(() => _statuses.Count == 2);
    }

    [Fact]
    public async Task Changing_the_period_polls_at_once()
    {
        await using var monitor = Start(TimeSpan.FromMinutes(10));
        _ups.Answer(true);
        await Wait.Until(() => _statuses.Count == 1);
        await Task.Delay(200); // waiting for the next poll

        _ups.Answer(true);
        monitor.Period = TimeSpan.FromMinutes(5);

        await Wait.Until(() => _statuses.Count == 2);
        Assert.Equal(TimeSpan.FromMinutes(5), monitor.Period);
    }

    [Fact]
    public async Task Resuming_from_sleep_resets_the_link_and_forgives_earlier_failures()
    {
        await using var monitor = Start(TimeSpan.FromMilliseconds(20));
        _ups.Answer(true);
        _ups.Fail("a");
        _ups.Fail("b");
        await Wait.Until(() => _ups.Reads == 4); // both failures counted

        monitor.NotifyResumed();
        _ups.Fail("c");
        await Wait.Until(() => _ups.Reads == 5);

        Assert.Equal(1, _ups.Resets);
        Assert.Empty(_events); // 2 failures, then a fresh streak of 1
        Assert.Equal(UpsLinkState.Retrying, monitor.LinkState);
    }

    [Fact]
    public async Task Dispose_stops_polling_and_closes_the_connection()
    {
        var monitor = Start();
        _ups.Answer(true);
        await Wait.Until(() => _statuses.Count == 1);
        _ups.Hang();
        await Wait.Until(() => _ups.Reads == 2);

        await monitor.DisposeAsync();

        Assert.True(_ups.Disposed);
        Assert.Empty(_log); // a read cancelled by shutting down is not a failure
        _ups.Answer(true);
        await Task.Delay(100);
        Assert.Equal(2, _ups.Reads);
    }

    [Fact]
    public async Task Dispose_without_start_closes_the_connection()
    {
        var monitor = new UpsMonitor(_ups, TimeSpan.Zero);
        await monitor.DisposeAsync();
        Assert.True(_ups.Disposed);
    }

    [Fact]
    public async Task Starting_twice_runs_one_loop()
    {
        await using var monitor = Start(TimeSpan.FromMinutes(10));
        monitor.Start();
        _ups.Answer(true);
        await Wait.Until(() => _statuses.Count == 1);
        await Task.Delay(100);
        Assert.Equal(1, _ups.Reads);
    }
}
