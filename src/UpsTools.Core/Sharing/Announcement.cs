using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace UpsTools.Core.Sharing;

/// <summary>What a PC does with a UPS it has paired, as it tells the other PCs (<see cref="UpsCoordinator"/>).</summary>
public enum UpsRole
{
    /// <summary>No PC is connected to it: this one would connect if it is chosen.</summary>
    Wanting,

    /// <summary>Connected to it over Bluetooth: the others read it through this PC.</summary>
    Holding,

    /// <summary>Reads it through the PC holding it.</summary>
    Watching,

    /// <summary>Let go of it after it stopped answering, so that another PC can try; waits before wanting it again.</summary>
    Resting,
}

/// <summary>What a PC runs on, as it tells the other PCs: one on mains power takes over a UPS from one on battery.</summary>
public enum PowerSource
{
    /// <summary>It doesn't say: a version from before PCs told.</summary>
    Unknown,

    /// <summary>Plugged in, or a PC without a battery: holding a link costs it nothing that matters.</summary>
    Mains,

    /// <summary>On its own battery, which a link to a UPS drains.</summary>
    Battery,
}

/// <summary>A UPS in an <see cref="Announcement"/>.</summary>
/// <param name="Online">For <see cref="UpsRole.Holding"/>: its last read succeeded.</param>
/// <param name="Unreached">
/// This PC couldn't reach it lately when it was connected to it: on mains power or not, it doesn't take it over from a
/// PC on battery for a while.
/// </param>
public sealed record AnnouncedUps(string Key, string Title, PairedUps Device, UpsRole Role, bool Online = false, bool Unreached = false);

/// <summary>
/// What a PC tells the others about itself, every few seconds over UDP multicast: its TCP port and what it does with
/// each UPS it has paired. Readable only with the sharing code: sealed with AES-GCM under <see cref="SharingCode.DiscoveryKey"/>.
/// The PC's name and instance stay readable, with the code's <see cref="SharingCode.Fingerprint"/>, so that a PC with
/// another code can be told about.
/// </summary>
/// <param name="Instance">Tells the running copy of UPS Tools apart (<see cref="SharingHub.Instance"/>).</param>
/// <param name="Probe">Just started: the others answer with an announcement of their own at once.</param>
/// <param name="Bye">Stopping: the others forget it at once.</param>
/// <param name="Power">What it runs on; <see cref="PowerSource.Unknown"/> from a version that doesn't say.</param>
public sealed record Announcement(
    string Instance,
    string Pc,
    int Port,
    IReadOnlyList<AnnouncedUps> Upses,
    bool Probe = false,
    bool Bye = false,
    PowerSource Power = PowerSource.Unknown)
{
    private static readonly byte[] Magic = "UPSD\x01"u8.ToArray();
    private const int FingerprintLength = 8;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    /// <summary>An announcement older than this (by the sender's clock) is ignored: it was played back.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    internal sealed record Header(string Instance, string Pc);

    internal sealed record Body(int Port, long SentMs, IReadOnlyList<AnnouncedUps> Upses, bool Probe, bool Bye, PowerSource Power = PowerSource.Unknown);

    public byte[] Seal(byte[] key, byte[] fingerprint, DateTimeOffset now)
    {
        var header = SharingJson.Write(new Header(Instance, Pc));
        var body = SharingJson.Write(new Body(Port, now.ToUnixTimeMilliseconds(), Upses, Probe, Bye, Power));

        var clear = new byte[Magic.Length + FingerprintLength + 2 + header.Length];
        Magic.CopyTo(clear, 0);
        fingerprint.CopyTo(clear, Magic.Length);
        BinaryPrimitives.WriteUInt16BigEndian(clear.AsSpan(Magic.Length + FingerprintLength), (ushort)header.Length);
        header.CopyTo(clear, Magic.Length + FingerprintLength + 2);

        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var sealedBody = new byte[body.Length + TagLength];
        using (var aes = new AesGcm(key, TagLength))
            aes.Encrypt(nonce, body, sealedBody.AsSpan(0, body.Length), sealedBody.AsSpan(body.Length), clear);
        return [.. clear, .. nonce, .. sealedBody];
    }

    /// <summary>What a datagram says.</summary>
    /// <returns>
    /// The announcement; or, for one sealed with another code, null with <paramref name="foreign"/> set to who sent it;
    /// or null and null for anything else (not an announcement, changed, played back).
    /// </returns>
    public static Announcement? Open(byte[] datagram, byte[] key, byte[] fingerprint, DateTimeOffset now, out (string Instance, string Pc)? foreign)
    {
        foreign = null;
        try
        {
            var headerStart = Magic.Length + FingerprintLength + 2;
            if (datagram.Length < headerStart || !datagram.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
            var headerLength = BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(Magic.Length + FingerprintLength));
            var bodyStart = headerStart + headerLength + NonceLength;
            if (datagram.Length < bodyStart + TagLength) return null;
            var header = JsonSerializer.Deserialize(datagram.AsSpan(headerStart, headerLength), SharingJson.Info<Header>());
            if (header?.Instance is null || header.Pc is null) return null;

            if (!datagram.AsSpan(Magic.Length, FingerprintLength).SequenceEqual(fingerprint))
            {
                foreign = (header.Instance, header.Pc);
                return null;
            }

            var body = new byte[datagram.Length - bodyStart - TagLength];
            using (var aes = new AesGcm(key, TagLength))
                aes.Decrypt(datagram.AsSpan(headerStart + headerLength, NonceLength), datagram.AsSpan(bodyStart, body.Length),
                    datagram.AsSpan(datagram.Length - TagLength), body, datagram.AsSpan(0, headerStart + headerLength));
            var opened = JsonSerializer.Deserialize(body, SharingJson.Info<Body>());
            if (opened is null || (now - DateTimeOffset.FromUnixTimeMilliseconds(opened.SentMs)).Duration() > MaxAge) return null;
            return new Announcement(header.Instance, header.Pc, opened.Port, opened.Upses ?? [], opened.Probe, opened.Bye,
                Enum.IsDefined(opened.Power) ? opened.Power : PowerSource.Unknown);
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}
