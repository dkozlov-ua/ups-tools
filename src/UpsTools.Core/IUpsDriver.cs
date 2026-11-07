using System.Text.Json.Serialization;

namespace UpsTools.Core;

/// <summary>A UPS family + transport (e.g. Bluetti over Bluetooth LE). Implement this to support another manufacturer.</summary>
public interface IUpsDriver
{
    /// <summary>Stable identifier stored in settings, e.g. "bluetti-ble".</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Report nearby devices as they are discovered until the duration elapses or the token is cancelled.</summary>
    Task DiscoverAsync(Action<DiscoveredUps> onFound, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>
    /// Its devices only talk to the account they're bound to: pairing asks for that account's user ID
    /// (<see cref="PairedUps.UserId"/>).
    /// </summary>
    bool RequiresUserId => false;

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

/// <param name="Name">What the device calls itself; its serial number for EcoFlow devices.</param>
public sealed record DiscoveredUps(string DriverId, string Address, string Name, string Model, short? Rssi)
{
    /// <summary>The brand and model, e.g. "Bluetti EL100V2".</summary>
    public string Title => UpsDrivers.Title(DriverId, Model);
}

/// <param name="Name">What the device calls itself; its serial number for EcoFlow devices.</param>
/// <param name="UserId">For a driver that <see cref="IUpsDriver.RequiresUserId"/>: the account the device is bound to.</param>
public sealed record PairedUps(
    string DriverId,
    string Address,
    string Name,
    string Model,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserId = null)
{
    /// <summary>The brand and model, e.g. "Bluetti EL100V2".</summary>
    [JsonIgnore]
    public string Title => UpsDrivers.Title(DriverId, Model);
}
