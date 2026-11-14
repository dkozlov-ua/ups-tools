namespace UpsTools.Core;

/// <summary>
/// Averages the output power over a rolling window, so the time left on battery doesn't jump with every short change
/// in load. Readings without an output power leave the window as it is.
/// </summary>
public sealed class LoadAverage(TimeSpan window)
{
    /// <summary>The window <see cref="UpsMonitor"/> uses.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    private readonly Queue<(DateTimeOffset At, double Watts)> _samples = new();

    public LoadAverage() : this(DefaultWindow)
    {
    }

    public TimeSpan Window { get; } = window;

    /// <summary>Adds the reading's output power and returns it with <see cref="UpsStatus.AverageOutputW"/> set.</summary>
    /// <remarks>
    /// A reading that already has an average (one shared by another PC, which worked it out over its own readings)
    /// keeps it, and still counts towards this window.
    /// </remarks>
    public UpsStatus Add(UpsStatus status)
    {
        if (status.TotalOutputW is not { } watts) return status;

        // The clock went back (e.g. it was corrected): older samples can't be placed any more.
        if (_samples.Count > 0 && _samples.Last().At > status.Timestamp) _samples.Clear();

        _samples.Enqueue((status.Timestamp, watts));
        while (_samples.Peek().At <= status.Timestamp - Window) _samples.Dequeue();

        return status.AverageOutputW is null ? status with { AverageOutputW = _samples.Average(s => s.Watts) } : status;
    }
}
