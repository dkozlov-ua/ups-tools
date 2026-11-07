namespace UpsTools.Core.EcoFlow;

/// <summary>One scalar field of a protobuf message: its wire type and raw value (a varint, or the bits of a fixed32/64).</summary>
internal readonly record struct ProtoValue(int WireType, ulong Raw)
{
    public const int Varint = 0;
    public const int Fixed64 = 1;
    public const int Fixed32 = 5;

    public double AsDouble() => WireType switch
    {
        Fixed32 => BitConverter.UInt32BitsToSingle((uint)Raw),
        Fixed64 => BitConverter.UInt64BitsToDouble(Raw),
        _ => (long)Raw,
    };

    public long AsInteger() => WireType switch
    {
        Fixed32 or Fixed64 => (long)Math.Round(AsDouble()),
        _ => (long)Raw,
    };
}

/// <summary>
/// The latest value of each scalar field of a protobuf message that arrives in parts: EcoFlow devices send a few fields
/// at a time, so each part updates the fields it has and leaves the rest. Strings and nested messages are kept as bytes.
/// </summary>
internal sealed class ProtoFields
{
    private readonly Dictionary<int, ProtoValue> _fields = [];
    private readonly Dictionary<int, byte[]> _bytes = [];

    public int Count => _fields.Count;

    public ProtoValue? this[int field] => _fields.TryGetValue(field, out var value) ? value : null;

    public double? Double(int field) => this[field]?.AsDouble();

    public long? Integer(int field) => this[field]?.AsInteger();

    /// <summary>A string or nested message.</summary>
    public byte[]? Bytes(int field) => _bytes.GetValueOrDefault(field);

    public void Clear()
    {
        _fields.Clear();
        _bytes.Clear();
    }

    /// <summary>Applies one part; throws <see cref="FormatException"/> on bytes that aren't a protobuf message.</summary>
    public void Merge(ReadOnlySpan<byte> message)
    {
        var i = 0;
        while (i < message.Length)
        {
            var key = ReadVarint(message, ref i);
            var field = (int)(key >> 3);
            var wireType = (int)(key & 7);
            if (field == 0) throw new FormatException("Protobuf field number 0");
            switch (wireType)
            {
                case ProtoValue.Varint:
                    _fields[field] = new(wireType, ReadVarint(message, ref i));
                    break;
                case ProtoValue.Fixed64:
                    _fields[field] = new(wireType, BitConverter.ToUInt64(Take(message, ref i, 8)));
                    break;
                case ProtoValue.Fixed32:
                    _fields[field] = new(wireType, BitConverter.ToUInt32(Take(message, ref i, 4)));
                    break;
                case 2: // length-delimited: strings and nested messages
                    _bytes[field] = Take(message, ref i, checked((int)ReadVarint(message, ref i))).ToArray();
                    break;
                default:
                    throw new FormatException($"Unsupported protobuf wire type {wireType}");
            }
        }
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int i)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (i >= data.Length) throw new FormatException("Protobuf message ends inside a varint");
            var b = data[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new FormatException("Protobuf varint too long");
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> data, ref int i, int count)
    {
        if (count < 0 || i + count > data.Length) throw new FormatException("Protobuf message ends inside a field");
        var slice = data.Slice(i, count);
        i += count;
        return slice;
    }

    /// <summary>Encodes one varint field, for the few messages we send.</summary>
    public static byte[] EncodeVarint(int field, ulong value)
    {
        var bytes = new List<byte>();
        foreach (var v in new[] { (ulong)field << 3, value })
        {
            var rest = v;
            do
            {
                var b = (byte)(rest & 0x7F);
                rest >>= 7;
                bytes.Add(rest != 0 ? (byte)(b | 0x80) : b);
            } while (rest != 0);
        }
        return [.. bytes];
    }
}
