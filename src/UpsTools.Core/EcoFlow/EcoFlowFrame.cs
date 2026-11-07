using System.Buffers.Binary;

namespace UpsTools.Core.EcoFlow;

/// <summary>
/// The outer frame on the Bluetooth link: 0x5A 0x5A, the frame type, 0x01, the length, the payload and a CRC-16.
/// Handshake messages travel as plain <see cref="Command"/> frames; packets as encrypted <see cref="Protocol"/> frames.
/// </summary>
internal readonly record struct EcoFlowFrame(byte Type, byte[] Payload)
{
    public const byte Command = 0x0;
    public const byte Protocol = 0x1;

    public byte[] ToBytes()
    {
        var data = new byte[8 + Payload.Length];
        data[0] = data[1] = 0x5A;
        data[2] = (byte)(Type << 4);
        data[3] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)(Payload.Length + 2));
        Payload.CopyTo(data, 6);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6 + Payload.Length), EcoFlowCrc.Crc16(data.AsSpan(0, 6 + Payload.Length)));
        return data;
    }
}

/// <summary>
/// Collects notifications into <see cref="EcoFlowFrame"/>s. A frame may span notifications; bytes that aren't a frame
/// with a good CRC are skipped.
/// </summary>
internal sealed class EcoFlowFrameReader
{
    private static readonly byte[] Prefix = [0x5A, 0x5A];

    /// <summary>Lengths beyond this are corrupt, not a frame to wait for.</summary>
    private const int MaxLength = 10_000;

    private byte[] _buffer = [];

    public void Reset() => _buffer = [];

    public List<EcoFlowFrame> Add(ReadOnlySpan<byte> notification)
    {
        var data = (ReadOnlySpan<byte>)[.. _buffer, .. notification];
        var frames = new List<EcoFlowFrame>();
        while (true)
        {
            var start = data.IndexOf(Prefix);
            if (start < 0)
            {
                // Keep a trailing 0x5A: it may be the first half of the next prefix.
                data = data is [.., 0x5A] ? data[^1..] : [];
                break;
            }
            data = data[start..];

            var (state, end) = Candidate(data);
            if (state == State.Incomplete)
            {
                // Wait for the rest, unless a whole frame already follows: then this was a 0x5A 0x5A inside other data.
                var next = NextCompleteFrame(data);
                if (next < 0) break;
                data = data[next..];
                continue;
            }
            if (state == State.Invalid)
            {
                data = data[1..];
                continue;
            }

            frames.Add(new EcoFlowFrame((byte)(data[2] >> 4), data[6..(end - 2)].ToArray()));
            data = data[end..];
        }
        _buffer = data.ToArray();
        return frames;
    }

    private enum State
    {
        Complete,
        Incomplete,
        Invalid,
    }

    /// <summary>Whether the data starts with a whole frame (and where it ends), the start of one, or no frame.</summary>
    private static (State State, int End) Candidate(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6) return (State.Incomplete, 0);
        // The frame type byte has a zero low nibble: not so for a stray 0x5A before the prefix.
        var length = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if ((data[2] & 0x0F) != 0 || length is < 2 or > MaxLength) return (State.Invalid, 0);

        var end = 6 + length;
        if (end > data.Length) return (State.Incomplete, end);
        var crc = BinaryPrimitives.ReadUInt16LittleEndian(data[(end - 2)..]);
        return EcoFlowCrc.Crc16(data[..(end - 2)]) == crc ? (State.Complete, end) : (State.Invalid, 0);
    }

    private static int NextCompleteFrame(ReadOnlySpan<byte> data)
    {
        for (var at = 1; at < data.Length - 1; at++)
        {
            if (data[at] == 0x5A && data[at + 1] == 0x5A && Candidate(data[at..]).State == State.Complete) return at;
        }
        return -1;
    }
}
