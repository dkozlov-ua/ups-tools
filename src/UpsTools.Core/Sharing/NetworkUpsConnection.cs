using System.Net;

namespace UpsTools.Core.Sharing;

/// <summary>
/// A UPS another PC is connected to (<see cref="UpsCoordinator"/> chose it), read through that PC. Each read waits
/// for that PC's next reading of the UPS, with timeouts of its own (<see cref="IUpsConnection.WaitsForReadings"/>).
/// A failed read on that PC fails here too, so this PC's events follow the UPS as that PC sees it, with this PC's own
/// limits.
/// </summary>
public sealed class NetworkUpsConnection : IUpsConnection
{
    /// <summary>How long to wait for a newer reading in one request: well under the sharing PC's 30 s limit.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(25);

    /// <summary>Beyond the wait, how long an answer may take.</summary>
    private static readonly TimeSpan AnswerMargin = TimeSpan.FromSeconds(15);

    private readonly IPAddress _address;
    private readonly int _port;
    private readonly string _code;
    private readonly string _key;
    private readonly string _pc;
    private readonly TimeSpan _retryDelay;
    private readonly Func<DateTimeOffset> _clock;
    private SharingClient? _client;
    private string? _run;
    private long _seq;
    private DateTimeOffset? _failedAt;
    private DateTimeOffset _lastReadingAt;
    private TimeSpan _period = TimeSpan.FromMinutes(1);

    /// <param name="key">The UPS (<see cref="SharingHub.KeyOf"/>).</param>
    /// <param name="pc">What the PC is called in messages.</param>
    /// <param name="retryDelay">After the link failed, how long to wait before connecting again (10 s unless a test says otherwise).</param>
    public NetworkUpsConnection(IPAddress address, int port, string code, string key, string pc, TimeSpan? retryDelay = null,
        Func<DateTimeOffset>? clock = null)
    {
        _address = address;
        _port = port;
        _code = code;
        _key = key;
        _pc = pc;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(10);
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>The computer name this PC gives the sharing one, for its log.</summary>
    public string From { get; init; } = Environment.MachineName;

    public bool WaitsForReadings => true;

    /// <summary>
    /// A sharing PC that answers but has had no reading for this long (its own period plus its read timeout, with a
    /// margin) has stopped reading the UPS: that counts as a failed read.
    /// </summary>
    private TimeSpan Overdue => _period + TimeSpan.FromSeconds(90);

    public async Task<UpsStatus> ReadAsync(CancellationToken cancellationToken)
    {
        // Don't hammer a PC that just failed to answer.
        if (_failedAt is { } failed && _clock() - failed < _retryDelay)
            await Task.Delay(_retryDelay - (_clock() - failed), cancellationToken);

        try
        {
            if (_client is null)
            {
                _client = await SharingClient.ConnectAsync(_address, _port, _code, _pc, cancellationToken);
                _client.From = From;
                _lastReadingAt = _clock();
            }

            while (true)
            {
                var response = await _client.AskAsync(new SharingRequest
                {
                    Op = SharingRequest.Next, From = From, Ups = _key, Run = _run, Seq = _seq, WaitSeconds = (int)Wait.TotalSeconds,
                }, Wait + AnswerMargin, cancellationToken);

                if (response.Missing) throw new SharingException($"{_pc} isn't connected to this UPS any more");
                if (response.PeriodSeconds > 0) _period = TimeSpan.FromSeconds(response.PeriodSeconds);
                if (response.Seq == 0)
                {
                    // Nothing newer yet.
                    if (_clock() - _lastReadingAt > Overdue)
                        throw new SharingException($"{_pc} has had no reading of the UPS for {(int)(_clock() - _lastReadingAt).TotalSeconds} s");
                    continue;
                }

                (_run, _seq, _lastReadingAt, _failedAt) = (response.Run, response.Seq, _clock(), null);
                if (response.Status is { } status) return status;
                throw new SharedReadFailedException($"On {_pc}: {response.Error ?? "the read failed"}");
            }
        }
        catch (Exception ex) when (ex is not SharedReadFailedException)
        {
            // The link is in an unknown state (a request may be unanswered): start over on the next read.
            Drop();
            if (!cancellationToken.IsCancellationRequested) _failedAt = _clock();
            throw;
        }
    }

    public Task ResetAsync()
    {
        Drop();
        return Task.CompletedTask;
    }

    private void Drop()
    {
        _client?.Dispose();
        _client = null;
    }

    public ValueTask DisposeAsync()
    {
        Drop();
        return ValueTask.CompletedTask;
    }

    /// <summary>The sharing PC read the UPS and failed; the link to it is fine.</summary>
    private sealed class SharedReadFailedException(string message) : SharingException(message);
}
