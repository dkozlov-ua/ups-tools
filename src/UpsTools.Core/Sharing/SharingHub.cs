using System.Collections.Concurrent;

namespace UpsTools.Core.Sharing;

/// <summary>
/// What this PC shares: the UPSes it is connected to itself, each with its latest reading (or why the latest read
/// failed). The monitors publish here, from their polling threads; <see cref="SharingServer"/> answers from here.
/// </summary>
public sealed class SharingHub
{
    private readonly ConcurrentDictionary<string, Feed> _feeds = new();

    /// <summary>Tells this running copy of UPS Tools apart, e.g. from the PC it is asked to watch.</summary>
    public string Instance { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The computer name other PCs see.</summary>
    public string Pc { get; init; } = Environment.MachineName;

    /// <summary>What names a UPS in requests: its driver and address, which stay the same when it is paired again.</summary>
    public static string KeyOf(PairedUps device) => $"{device.DriverId}/{device.Address}";

    /// <summary>Shares a UPS from now on (or updates how it is shared), without a reading yet.</summary>
    public void Share(PairedUps device, string title, TimeSpan period)
    {
        // The account it is bound to stays here.
        var shared = device with { UserId = null };
        var feed = _feeds.GetOrAdd(KeyOf(device), _ => new Feed(shared));
        feed.Title = title;
        feed.Period = period;
    }

    /// <summary>Stops sharing a UPS: PCs waiting for its next reading are told it is gone.</summary>
    public void Unshare(PairedUps device)
    {
        if (_feeds.TryRemove(KeyOf(device), out var feed)) feed.Close();
    }

    public void Rename(PairedUps device, string title)
    {
        if (_feeds.TryGetValue(KeyOf(device), out var feed)) feed.Title = title;
    }

    public void SetPeriod(TimeSpan period)
    {
        foreach (var feed in _feeds.Values) feed.Period = period;
    }

    public void Publish(PairedUps device, UpsStatus status)
    {
        if (_feeds.TryGetValue(KeyOf(device), out var feed)) feed.Publish(status, null);
    }

    public void PublishFailure(PairedUps device, string error)
    {
        if (_feeds.TryGetValue(KeyOf(device), out var feed)) feed.Publish(null, error);
    }

    public IReadOnlyList<SharedUps> List() =>
        [.. _feeds.OrderBy(f => f.Value.Created).Select(f => new SharedUps(f.Key, f.Value.Title, f.Value.Device))];

    internal Feed? Find(string? key) => key is not null && _feeds.TryGetValue(key, out var feed) ? feed : null;

    /// <summary>One shared UPS and its latest reading.</summary>
    internal sealed class Feed(PairedUps device)
    {
        private readonly Lock _lock = new();
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PairedUps Device { get; } = device;
        public long Created { get; } = DateTime.UtcNow.Ticks;

        /// <summary>Tells this feed's readings apart from those of the one before it (the UPS shared again counts from 1 again).</summary>
        public string Run { get; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = device.Title;
        public TimeSpan Period { get; set; }
        public bool Closed { get; private set; }

        /// <summary>The latest reading's number (0: none yet), the reading or why it failed, and a task that completes on the next one.</summary>
        public (long Seq, UpsStatus? Status, string? Error, Task Changed) Latest
        {
            get
            {
                lock (_lock) return (_seq, _status, _error, _changed.Task);
            }
        }

        private long _seq;
        private UpsStatus? _status;
        private string? _error;

        public void Publish(UpsStatus? status, string? error)
        {
            TaskCompletionSource changed;
            lock (_lock)
            {
                _seq++;
                (_status, _error) = (status, error);
                changed = _changed;
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            changed.TrySetResult();
        }

        public void Close()
        {
            Closed = true;
            Publish(null, null);
        }
    }
}
