namespace UpsTools.Core.Ble;

/// <summary>
/// The platform's Bluetooth LE: scanning and opening links. Windows has its own built in
/// (<see cref="BlePlatform.Default"/>); another platform (Android) passes one to the drivers.
/// </summary>
public interface IBlePlatform
{
    /// <summary>Reports every advertisement heard (scan responses too) until the duration elapses or the token is cancelled.</summary>
    /// <exception cref="IOException">Bluetooth can't be used, e.g. it is off; the message says why.</exception>
    Task ScanAsync(Action<BleAdvertisement> onReceived, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Connects to the device and finds its data channel: the first of <paramref name="channels"/> it has.</summary>
    /// <param name="brand">Names the device in errors, e.g. "Bluetti".</param>
    /// <param name="trace">Protocol tracing, or null when it is off.</param>
    /// <exception cref="IOException">It can't; the message says why.</exception>
    Task<IBleLink> ConnectAsync(ulong address, IReadOnlyList<GattChannel> channels, string brand, Action<string>? trace,
        CancellationToken cancellationToken);
}

/// <summary>An advertisement (or scan response) a device sent.</summary>
/// <param name="LocalName">The name in it, if any.</param>
/// <param name="ManufacturerData">Its manufacturer data sections: the company ID and the data after it.</param>
public sealed record BleAdvertisement(ulong Address, string? LocalName, short Rssi,
    IReadOnlyList<(ushort CompanyId, byte[] Data)> ManufacturerData)
{
    public IEnumerable<byte[]> ManufacturerDataOf(ushort companyId) =>
        ManufacturerData.Where(m => m.CompanyId == companyId).Select(m => m.Data);
}

public static class BlePlatform
{
    /// <summary>
    /// What the drivers use when not given one: WinRT on Windows; elsewhere, one that says Bluetooth isn't supported
    /// (the app passes its own).
    /// </summary>
    public static IBlePlatform Default { get; } =
#if WINDOWS
        new WinRtBle();
#else
        new UnsupportedBle();
#endif

    private sealed class UnsupportedBle : IBlePlatform
    {
        public Task ScanAsync(Action<BleAdvertisement> onReceived, TimeSpan duration, CancellationToken cancellationToken) =>
            throw new PlatformNotSupportedException("Bluetooth LE isn't supported here");

        public Task<IBleLink> ConnectAsync(ulong address, IReadOnlyList<GattChannel> channels, string brand,
            Action<string>? trace, CancellationToken cancellationToken) =>
            throw new PlatformNotSupportedException("Bluetooth LE isn't supported here");
    }
}

public static class BluetoothAddressFormat
{
    public static string Format(ulong address) =>
        string.Join(':', Enumerable.Range(0, 6).Select(i => ((address >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    public static ulong Parse(string address) =>
        Convert.ToUInt64(address.Replace(":", "").Replace("-", ""), 16);
}
