namespace UpsTools.Core.Bluetti;

/// <summary>
/// A connected link to a Bluetti device's data channel: notifications from the device and writes to it.
/// <see cref="GattLink"/> is the Bluetooth LE one; the tests use a simulated device.
/// </summary>
internal interface IBluettiLink : IAsyncDisposable
{
    /// <summary>A notification from the device. Raised on any thread.</summary>
    event Action<byte[]>? Received;

    /// <summary>The link dropped. Raised on any thread.</summary>
    event Action? Disconnected;

    /// <summary>Subscribes to (or unsubscribes from) the device's notifications.</summary>
    Task SetNotificationsAsync(bool on, CancellationToken ct);

    Task WriteAsync(byte[] data, CancellationToken ct);
}

/// <summary>Opens a new link to the device; throws when it can't.</summary>
internal delegate Task<IBluettiLink> BluettiLinkFactory(CancellationToken ct);

/// <summary>How long <see cref="BluettiConnection"/> waits for each step.</summary>
internal sealed record BluettiTimings(
    TimeSpan ChallengeWait,
    TimeSpan Handshake,
    TimeSpan Response,
    TimeSpan Write,
    TimeSpan Retry,
    TimeSpan Unsubscribe)
{
    public static BluettiTimings Default { get; } = new(
        // Encrypted devices send their challenge right after notifications are enabled.
        ChallengeWait: TimeSpan.FromSeconds(4),
        Handshake: TimeSpan.FromSeconds(5), // normally ~1.3 s
        Response: TimeSpan.FromSeconds(5),
        Write: TimeSpan.FromSeconds(5),
        Retry: TimeSpan.FromSeconds(1),
        Unsubscribe: TimeSpan.FromSeconds(2));
}
