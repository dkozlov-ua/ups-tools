using System.Buffers.Binary;
using System.Text;

namespace UpsTools.Core.Bluetti;

internal readonly record struct RegisterBlock(ushort Start, ushort Count)
{
    public bool Contains(ushort address, int count = 1) => address >= Start && address + count <= Start + Count;
}

/// <summary>Register map for the Bluetti "V2" layout and its conversion to <see cref="UpsStatus"/>.</summary>
internal static class BluettiStatusParser
{
    private const ushort SocReg = 102;
    private const ushort RuntimeReg = 104;   // in the model's RuntimeUnit; 99.9 h (5994 minutes) while on grid
    private const ushort ModelReg = 110;     // 6 registers, ASCII with bytes swapped per register
    private const ushort SerialReg = 116;    // 4 registers, least-significant word first
    private const ushort DcOutputReg = 140;
    private const ushort AcOutputReg = 142;
    private const ushort DcInputReg = 144;
    private const ushort AcInputReg = 146;
    private const ushort AcInputVoltageReg = 1314; // 0.1 V
    private const ushort AcOutputVoltageReg = 1511; // 0.1 V

    /// <summary>Mains is considered present above this input voltage.</summary>
    internal const double GridVoltageThreshold = 80;

    /// <summary>Runtime readings at or above this are placeholders ("99.9 h"), not estimates.</summary>
    private static readonly TimeSpan RuntimePlaceholder = TimeSpan.FromHours(99.9);

    public static IReadOnlyList<RegisterBlock> BlocksFor(BluettiModel model) => model.HasAcInputVoltage
        ? [new(SocReg, 18), new(DcOutputReg, 8), new(AcInputVoltageReg, 2), new(AcOutputVoltageReg, 1)]
        : [new(SocReg, 18), new(DcOutputReg, 8)];

    public static UpsStatus Parse(BluettiModel model, IReadOnlyDictionary<RegisterBlock, byte[]> blocks, DateTimeOffset timestamp)
    {
        ushort? Reg(ushort address)
        {
            foreach (var (block, data) in blocks)
            {
                if (block.Contains(address))
                    return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan((address - block.Start) * 2));
            }
            return null;
        }

        ReadOnlySpan<byte> Span(ushort address, int count)
        {
            foreach (var (block, data) in blocks)
            {
                if (block.Contains(address, count))
                    return data.AsSpan((address - block.Start) * 2, count * 2);
            }
            return [];
        }

        var acInputVoltage = model.HasAcInputVoltage ? Reg(AcInputVoltageReg) / 10.0 : null;
        var acInputW = (double?)Reg(AcInputReg);
        bool? grid = model.HasAcInputVoltage
            ? acInputVoltage is { } v ? v >= GridVoltageThreshold : null
            : acInputW is { } p ? p > 0 : null;

        TimeSpan? runtime = null;
        if (grid == false && Reg(RuntimeReg) is { } raw and > 0)
        {
            var value = model.RuntimeUnit * raw;
            if (value < RuntimePlaceholder) runtime = value;
        }

        var soc = Reg(SocReg);

        return new UpsStatus
        {
            Timestamp = timestamp,
            Model = ParseModel(Span(ModelReg, 6)) ?? model.Name,
            Serial = ParseSerial(Span(SerialReg, 4)),
            GridPresent = grid,
            BatteryPercent = soc is <= 100 ? soc : null,
            CapacityWh = model.CapacityWh,
            AcInputVoltage = acInputVoltage,
            AcOutputVoltage = model.HasAcInputVoltage ? Reg(AcOutputVoltageReg) / 10.0 : null,
            AcInputW = acInputW,
            DcInputW = Reg(DcInputReg),
            AcOutputW = Reg(AcOutputReg),
            DcOutputW = Reg(DcOutputReg),
            ReportedRuntime = runtime,
        };
    }

    private static string? ParseModel(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return null;
        var swapped = data.ToArray();
        for (var i = 0; i + 1 < swapped.Length; i += 2) (swapped[i], swapped[i + 1]) = (swapped[i + 1], swapped[i]);
        var text = Encoding.ASCII.GetString(swapped).TrimEnd('\0').Trim();
        return text.Length > 0 ? text : null;
    }

    private static string? ParseSerial(ReadOnlySpan<byte> data)
    {
        if (data.Length != 8) return null;
        ulong value = 0;
        for (var i = 0; i < 4; i++)
            value |= (ulong)BinaryPrimitives.ReadUInt16BigEndian(data[(i * 2)..]) << (16 * i);
        return value.ToString();
    }
}
