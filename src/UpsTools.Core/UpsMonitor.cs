namespace UpsTools.Core;

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

    private readonly IUpsConnection _connection;
    private readonly TimeSpan _readTimeout;
    private readonly UpsStateTracker _tracker = new();
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource _wake = new();
    private Task? _loop;
    private TimeSpan _period;
    private string? _lastError; // repeated identical failures are logged once

    /// <param name="readTimeout">A read that takes longer counts as failed (30 s unless a test says otherwise).</param>
    public UpsMonitor(IUpsConnection connection, TimeSpan period, TimeSpan? readTimeout = null)
    {
        _connection = connection;
        _period = period;
        _readTimeout = readTimeout ?? DefaultReadTimeout;
    }

    public event Action<UpsStatus>? StatusUpdated;
    public event Action<UpsEventKind, UpsStatus?>? EventRaised;
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

            if (!_wake.TryReset()) _wake = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _wake.Token);
            try
            {
                await Task.Delay(_period, linked.Token);
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
            events = _tracker.OnSuccess(status);
            SetLinkState(UpsLinkState.Online);
            StatusUpdated?.Invoke(status);
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            var message = ex is OperationCanceledException ? "timed out" : ex.Message;
            if (message != _lastError) Log?.Invoke($"UPS read failed: {message}");
            _lastError = message;
            events = _tracker.OnFailure();
            SetLinkState(_tracker.Reachable == false ? UpsLinkState.Unreachable
                : LinkState == UpsLinkState.Connecting ? UpsLinkState.Connecting
                : UpsLinkState.Retrying);
        }
        catch (Exception)
        {
            return; // Shutting down.
        }

        foreach (var kind in events) EventRaised?.Invoke(kind, status);
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
