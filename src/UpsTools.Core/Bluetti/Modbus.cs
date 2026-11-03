using System.Buffers.Binary;

namespace UpsTools.Core.Bluetti;

/// <summary>The Modbus-RTU subset Bluetti uses over BLE: "read holding registers" (0x03) to unit 1.</summary>
internal static class Modbus
{
    private const byte UnitId = 1;
    private const byte ReadHoldingRegisters = 0x03;

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    public static byte[] BuildReadRequest(ushort start, ushort count)
    {
        var frame = new byte[8];
        frame[0] = UnitId;
        frame[1] = ReadHoldingRegisters;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), start);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), count);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), Crc16(frame.AsSpan(0, 6)));
        return frame;
    }

    /// <summary>Total frame length once enough bytes have arrived to tell, otherwise null.</summary>
    public static int? ExpectedResponseLength(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 2) return null;
        if ((buffer[1] & 0x80) != 0) return 5; // unit, function|0x80, exception code, crc16
        if (buffer.Length < 3) return null;
        return 3 + buffer[2] + 2;
    }

    /// <summary>Validate a read response and return its register bytes (big-endian, 2 per register).</summary>
    public static byte[] ParseReadResponse(ReadOnlySpan<byte> frame, ushort expectedCount)
    {
        if (frame.Length < 5) throw new ModbusException($"Response too short ({frame.Length} bytes)");

        var crc = BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]);
        if (crc != Crc16(frame[..^2])) throw new ModbusException("Response CRC mismatch");

        if (frame[1] == (ReadHoldingRegisters | 0x80))
            throw new ModbusException($"Device returned Modbus exception {frame[2]}");
        if (frame[1] != ReadHoldingRegisters)
            throw new ModbusException($"Unexpected function code 0x{frame[1]:X2}");

        var byteCount = frame[2];
        if (byteCount != expectedCount * 2 || frame.Length != 5 + byteCount)
            throw new ModbusException($"Unexpected payload size {byteCount} for {expectedCount} registers");

        return frame.Slice(3, byteCount).ToArray();
    }
}

public sealed class ModbusException(string message) : Exception(message);
