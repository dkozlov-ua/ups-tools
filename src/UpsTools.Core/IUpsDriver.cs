namespace UpsTools.Core;

/// <summary>A UPS family + transport (e.g. Bluetti over Bluetooth LE). Implement this to support another manufacturer.</summary>
public interface IUpsDriver
{
    /// <summary>Stable identifier stored in settings, e.g. "bluetti-ble".</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Report nearby devices as they are discovered until the duration elapses or the token is cancelled.</summary>
    Task DiscoverAsync(Action<DiscoveredUps> onFound, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Create a connection object. It connects lazily on the first read.</summary>
    IUpsConnection CreateConnection(PairedUps device);
}

public interface IUpsConnection : IAsyncDisposable
{
    /// <summary>Read a fresh status, (re)connecting if needed. Throws on failure.</summary>
    Task<UpsStatus> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Drop the link so the next read reconnects from scratch.</summary>
    Task ResetAsync();
}

public sealed record DiscoveredUps(string DriverId, string Address, string Name, string Model, short? Rssi);

public sealed record PairedUps(string DriverId, string Address, string Name, string Model);
