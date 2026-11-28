using System.Text;
using UpsTools.Core.Ble;

namespace UpsTools.Droid;

/// <summary>
/// Reads an advertisement as Android hands it over (the raw bytes of the advertisement and scan response): its name
/// and manufacturer data, as Windows does for the drivers.
/// </summary>
public static class AdvertisementParser
{
    private const byte ShortenedName = 0x08;
    private const byte CompleteName = 0x09;
    private const byte ManufacturerSpecific = 0xFF;

    /// <param name="raw">The AD structures: a length byte, then the type and the data; a length of 0 ends them.</param>
    public static BleAdvertisement Parse(ulong address, short rssi, ReadOnlySpan<byte> raw)
    {
        string? complete = null, shortened = null;
        var manufacturer = new List<(ushort, byte[])>();
        var i = 0;
        while (i < raw.Length)
        {
            var length = raw[i];
            if (length == 0 || i + 1 + length > raw.Length) break;
            var type = raw[i + 1];
            var data = raw.Slice(i + 2, length - 1);
            switch (type)
            {
                case CompleteName:
                    complete ??= Name(data);
                    break;
                case ShortenedName:
                    shortened ??= Name(data);
                    break;
                case ManufacturerSpecific when data.Length >= 2:
                    manufacturer.Add(((ushort)(data[0] | data[1] << 8), data[2..].ToArray()));
                    break;
            }
            i += 1 + length;
        }
        return new BleAdvertisement(address, complete ?? shortened, rssi, manufacturer);
    }

    private static string? Name(ReadOnlySpan<byte> data)
    {
        var name = Encoding.UTF8.GetString(data).TrimEnd('\0');
        return name.Length > 0 ? name : null;
    }
}
