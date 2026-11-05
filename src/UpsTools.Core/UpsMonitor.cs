namespace UpsTools.Core;

/// <summary>An event as the monitor raises it.</summary>
/// <param name="Status">The reading that raised it; null for "UPS unreachable".</param>
/// <param name="GridGoneSince">For "Grid lost": when the grid went, which is earlier than the reading with a delay.</param>
public sealed record UpsEvent(UpsEventKind Kind, UpsStatus? Status, DateTimeOffset? GridGoneSince = null);

public enum UpsLinkState
{
    Connecting,
    Online,
    Retrying,
    Unreachable,
}

/// <summary>
/// Polls one UPS connection on a background loop and reports status updates and state transitions.
/// Events are raised on the polling thread.
/// </summary>
public sealed class UpsMonitor : IAsyncDisposable
{
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DueMargin = TimeSpan.FromMilliseconds(50);

    private readonly IUpsConnection _connection;
    private readonly TimeSpan _readTimeout;
    private readonly UpsStateTracker _tracker = new();
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource _wake = new();
    private Task? _loop;
    private TimeSpan _period;
    private string? _lastError; // repeated identical failures are logged once

    /// <param name="readTimeout">A read that takes longer counts as failed (30 s unless a test says otherwise).</param>
    /// <param name="clock">The time of failed reads: the real one unless a test says otherwise.</param>
    public UpsMonitor(IUpsConnection connection, TimeSpan period, TimeSpan? readTimeout = null, Func<DateTimeOffset>? clock = null)
    {
        _connection = connection;
        _period = period;
        _readTimeout = readTimeout ?? DefaultReadTimeout;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    private readonly Func<DateTimeOffset> _clock;

    public event Action<UpsStatus>? StatusUpdated;
    public event Action<UpsEvent>? EventRaised;
    public event Action<UpsLinkState>? LinkStateChanged;
    public event Action<string>? Log;

    public UpsLinkState LinkState { get; private set; } = UpsLinkState.Connecting;
    public UpsStatus? LastStatus { get; private set; }

    public TimeSpan Period
    {
        get => _period;
        set
        {
            _period = value;
            PollNow();
        }
    }

    /// <summary>The limits of the events that depend on them, e.g. "Battery low"; apply from the next reading.</summary>
    public EventRules Rules
    {
        get => _tracker.Rules;
        set => _tracker.Rules = value;
    }

    public void Start() => _loop ??= Task.Run(RunAsync);

    /// <summary>Skip the remaining wait and poll immediately.</summary>
    public void PollNow() => _wake.Cancel();

    /// <summary>Call after the PC resumed from sleep: the BLE link is gone, so reconnect without counting it as an outage.</summary>
    public void NotifyResumed()
    {
        _tracker.ResetFailures();
        _ = _connection.ResetAsync();
        PollNow();
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            await PollOnceAsync();

            // Waiting out a grid loss: look again when "Grid lost" would be due (a little after: timers can fire early).
            var wait = _period;
            if (_tracker.GridLostDue is { } due && LastStatus is { } last)
            {
                var left = due - last.Timestamp + DueMargin;
                if (left < wait) wait = left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }

            // Unreachable on battery: try again when "Battery low" would be due.
            if (_tracker.BatteryLowDue is { } lowDue)
            {
                var left = lowDue - _clock() + DueMargin;
                if (left < wait) wait = left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }

            if (!_wake.TryReset()) _wake = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _wake.Token);
            try
            {
                await Task.Delay(wait, linked.Token);
            }
            catch (OperationCanceledException)
            {
                // Woken early or stopping.
            }
        }
    }

    private async Task PollOnceAsync()
    {
        IReadOnlyList<UpsEventKind> events;
        UpsStatus? status = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(_readTimeout);
            status = await _connection.ReadAsync(timeout.Token);
            LastStatus = status;
            _lastError = null;
            var waiting = _tracker.GridLostDue is not null;
            events = _tracker.OnSuccess(status);
            if (!waiting && _tracker.GridLostDue is not null)
                Log?.Invoke($"The grid is gone; Grid lost fires if it stays gone for {_tracker.Rules.GridLostDelaySeconds} s");
            if (_tracker.IgnoredGridLoss is { } blip)
                Log?.Invoke($"The grid came back after {Math.Round(blip.TotalSeconds)} s, before Grid lost was due");
            SetLinkState(UpsLinkState.Online);
            StatusUpdated?.Invoke(status);
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            var message = ex is OperationCanceledException ? "timed out" : ex.Message;
            if (message != _lastError) Log?.Invoke($"UPS read failed: {message}");
            _lastError = message;
            var unreachable = _tracker.Reachable == false;
            events = _tracker.OnFailure(_clock());
            if (!unreachable && _tracker.BatteryLowDue is not null)
                Log?.Invoke($"The UPS is unreachable on battery; Battery low fires if it stays unreachable for {_tracker.Rules.UnreachableOnBatteryMinutes} min");
            SetLinkState(_tracker.Reachable == false ? UpsLinkState.Unreachable
                : LinkState == UpsLinkState.Connecting ? UpsLinkState.Connecting
                : UpsLinkState.Retrying);
        }
        catch (Exception)
        {
            return; // Shutting down.
        }

        foreach (var kind in events)
            EventRaised?.Invoke(new UpsEvent(kind, status, kind == UpsEventKind.GridLost ? _tracker.GridGoneSince : null));
    }

    private void SetLinkState(UpsLinkState state)
    {
        if (LinkState == state) return;
        LinkState = state;
        LinkStateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _wake.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch { /* already logged */ }
        }
        await _connection.DisposeAsync();
        _stop.Dispose();
        _wake.Dispose();
    }
}
