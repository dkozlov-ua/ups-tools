using System.Text.Json;
using System.Text.Json.Serialization;

namespace UpsTools.Core.Sharing;

/// <summary>A UPS another PC shares: what that PC calls it, and the device.</summary>
/// <param name="Key">Names it in requests (<see cref="SharingHub.KeyOf"/>).</param>
public sealed record SharedUps(string Key, string Title, PairedUps Device);

/// <summary>What a PC that shares its UPSes answers to "list".</summary>
/// <param name="Pc">Its computer name.</param>
/// <param name="Instance">Tells the running copy of UPS Tools apart from any other (<see cref="SharingHub.Instance"/>).</param>
public sealed record SharedPc(string Pc, string Instance, IReadOnlyList<SharedUps> Upses);

/// <summary>A request from the PC that watches; JSON in a <see cref="SecureChannel"/> message.</summary>
internal sealed record SharingRequest
{
    public const string List = "list";
    public const string Next = "next";

    public required string Op { get; init; }

    /// <summary>The computer name of the PC that asks, for the log of the one that answers.</summary>
    public string? From { get; init; }

    /// <summary>For <see cref="Next"/>: which UPS (<see cref="SharedUps.Key"/>).</summary>
    public string? Ups { get; init; }

    /// <summary>For <see cref="Next"/>: the reading the asking PC has, which it wants one newer than.</summary>
    public string? Run { get; init; }
    public long Seq { get; init; }

    /// <summary>For <see cref="Next"/>: how long to wait for a newer reading before answering that there is none.</summary>
    public int WaitSeconds { get; init; }
}

/// <summary>The answer to a <see cref="SharingRequest"/>.</summary>
internal sealed record SharingResponse
{
    // "list"
    public SharedPc? Pc { get; init; }

    // "next"

    /// <summary>The UPS isn't shared (any more), or isn't monitored right now.</summary>
    public bool Missing { get; init; }

    /// <summary>The reading's place: <see cref="Seq"/> counts the readings of one run of the sharing app; 0: none newer yet.</summary>
    public string? Run { get; init; }
    public long Seq { get; init; }

    /// <summary>The reading, or why it failed.</summary>
    public UpsStatus? Status { get; init; }
    public string? Error { get; init; }

    /// <summary>How often the sharing PC reads the UPS.</summary>
    public int PeriodSeconds { get; init; }
}

internal static class SharingJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        IgnoreReadOnlyProperties = true,
    };

    public static byte[] Write<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Read<T>(byte[] json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new SharingException("Unexpected data on the link");
}
