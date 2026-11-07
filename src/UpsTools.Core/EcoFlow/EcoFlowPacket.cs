using System.Buffers.Binary;

namespace UpsTools.Core.EcoFlow;

/// <summary>
/// An EcoFlow command packet (prefix 0xAA, versions 2 and 3): who it is from and to, the command, and its payload.
/// Version 0x13 is version 3 whose payload ends in 0xBB 0xBB in place of a trustworthy CRC.
/// </summary>
internal sealed record EcoFlowPacket(byte Src, byte Dst, byte CmdSet, byte CmdId, byte[] Payload, byte Version = 3)
{
    public const byte Prefix = 0xAA;

    public byte Dsrc { get; init; } = 1;
    public byte Ddst { get; init; } = 1;

    /// <summary>Its first byte is also the key a device XORs the payload with, when not zero.</summary>
    public uint Seq { get; init; }

    public byte[] ToBytes()
    {
        var v3 = (Version & 0x0F) >= 3;
        var data = new List<byte>(20 + Payload.Length) { Prefix, Version, (byte)Payload.Length, (byte)(Payload.Length >> 8) };
        data.Add(EcoFlowCrc.Crc8([.. data]));
        data.Add(0x0D); // product byte
        data.AddRange([(byte)Seq, (byte)(Seq >> 8), (byte)(Seq >> 16), (byte)(Seq >> 24)]);
        data.AddRange([0, 0, Src, Dst]);
        if (v3) data.AddRange([Dsrc, Ddst]);
        data.AddRange([CmdSet, CmdId]);
        data.AddRange(Payload);
        var crc = EcoFlowCrc.Crc16([.. data]);
        data.AddRange([(byte)crc, (byte)(crc >> 8)]);
        return [.. data];
    }

    /// <summary>Parses one packet, undoing the payload XOR; throws <see cref="FormatException"/> when it isn't one.</summary>
    public static EcoFlowPacket Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5 || data[0] != Prefix) throw new FormatException($"Not an EcoFlow packet: {Hex(data)}");
        var versionByte = data[1];
        var version = versionByte & 0x0F;
        var sentinel = (versionByte & 0x10) != 0;
        if (version is not (2 or 3)) throw new FormatException($"Unsupported EcoFlow packet version 0x{versionByte:X2}");
        if (data.Length < (version == 2 ? 18 : 20)) throw new FormatException($"EcoFlow packet too short: {Hex(data)}");
        if (EcoFlowCrc.Crc8(data[..4]) != data[4]) throw new FormatException($"EcoFlow packet header CRC is wrong: {Hex(data)}");

        var endsInSentinel = data[^2] == 0xBB && data[^1] == 0xBB;
        if (!sentinel && !endsInSentinel &&
            EcoFlowCrc.Crc16(data[..^2]) != BinaryPrimitives.ReadUInt16LittleEndian(data[^2..]))
            throw new FormatException($"EcoFlow packet CRC is wrong: {Hex(data)}");

        var length = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        var seq = data.Slice(6, 4);
        var start = version == 2 ? 16 : 18;
        if (start + length > data.Length) throw new FormatException($"EcoFlow packet shorter than its length: {Hex(data)}");

        var payload = data.Slice(start, length).ToArray();
        if (seq[0] != 0)
        {
            for (var i = 0; i < payload.Length; i++) payload[i] ^= seq[0];
        }
        if (sentinel && payload is [.., 0xBB, 0xBB]) payload = payload[..^2];

        return new EcoFlowPacket(data[12], data[13], data[start - 2], data[start - 1], payload, versionByte)
        {
            Dsrc = version == 2 ? (byte)0 : data[14],
            Ddst = version == 2 ? (byte)0 : data[15],
            Seq = BinaryPrimitives.ReadUInt32LittleEndian(seq),
        };
    }

    private static string Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(data[..Math.Min(data.Length, 32)]);

    public override string ToString() =>
        $"packet {Src:X2}→{Dst:X2} {CmdSet:X2}/{CmdId:X2} v{Version:X2}, {Payload.Length} B";
}
