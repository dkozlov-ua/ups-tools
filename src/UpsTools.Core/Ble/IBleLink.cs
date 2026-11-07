namespace UpsTools.Core.Ble;

/// <summary>
/// A connected link to a device's data channel over Bluetooth LE: notifications from the device and writes to it.
/// <see cref="GattLink"/> is the real one; the tests use simulated devices.
/// </summary>
internal interface IBleLink : IAsyncDisposable
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
internal delegate Task<IBleLink> BleLinkFactory(CancellationToken ct);

/// <summary>A device's data channel: the characteristic it notifies on and the one it is written to.</summary>
internal sealed record GattChannel(Guid Notify, Guid Write)
{
    public static GattChannel FromShortIds(uint notify, uint write) => new(ShortUuid(notify), ShortUuid(write));

    /// <summary>A 16- or 32-bit Bluetooth UUID in the base UUID 0000xxxx-0000-1000-8000-00805f9b34fb.</summary>
    private static Guid ShortUuid(uint id) => Guid.Parse($"{id:x8}-0000-1000-8000-00805f9b34fb");
}

/// <summary>Verbose protocol tracing for diagnostics; enable with the environment variable UPSTOOLS_TRACE=1.</summary>
internal static class BleTrace
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("UPSTOOLS_TRACE") == "1";
}
