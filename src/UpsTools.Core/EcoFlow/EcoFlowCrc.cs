namespace UpsTools.Core.EcoFlow;

/// <summary>The checksums of EcoFlow frames: CRC-8 (poly 0x07, init 0) and CRC-16/ARC (poly 0x8005 reflected, init 0).</summary>
internal static class EcoFlowCrc
{
    private static readonly byte[] Crc8Table = BuildCrc8Table();
    private static readonly ushort[] Crc16Table = BuildCrc16Table();

    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (var b in data) crc = Crc8Table[crc ^ b];
        return crc;
    }

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data) crc = (ushort)(Crc16Table[(crc ^ b) & 0xFF] ^ (crc >> 8));
        return crc;
    }

    private static byte[] BuildCrc8Table()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
            table[i] = (byte)crc;
        }
        return table;
    }

    private static ushort[] BuildCrc16Table()
    {
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
            table[i] = (ushort)crc;
        }
        return table;
    }
}
