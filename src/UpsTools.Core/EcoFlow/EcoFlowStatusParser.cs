using System.Text;

namespace UpsTools.Core.EcoFlow;

/// <summary>The fields of the River 3 "DisplayPropertyUpload" message (pr705.proto in ha-ef-ble) and their <see cref="UpsStatus"/>.</summary>
internal static class EcoFlowStatusParser
{
    // Battery
    internal const int CmsBattSoc = 262;        // float %, all batteries
    internal const int BmsBattSoc = 242;        // float %, the built-in battery
    internal const int CmsBattFullEnergy = 462; // uint32 Wh, all batteries; not sent by a River 3 Plus

    // Extra batteries (a River 3 Plus takes one): plugged in, and serial number
    internal const int ExtraBatteryFlag = 426;
    internal const int ExtraBatterySerial = 433;
    internal const int ExtraBattery2Flag = 83;
    internal const int ExtraBattery2Serial = 88;

    // Power, in W; outputs are negative
    internal const int PowInSum = 3;
    internal const int PowOutSum = 4;
    internal const int PowGetQcUsb1 = 9;
    internal const int PowGetQcUsb2 = 10;
    internal const int PowGetTypeC1 = 11;
    internal const int PowGetTypeC2 = 12;
    internal const int PowGet12V = 37;
    internal const int PowGetAcIn = 54;
    internal const int PowGetPv = 361;
    internal const int PowGetAcOut = 368;

    // Inputs
    internal const int AcInFlag = 61;
    internal const int AcOutVoltage = 67;        // float V
    internal const int AcInVoltage = 68;         // float V
    internal const int AcChargerFlag = 202;      // bool: AC input plugged in

    /// <summary>Mains is considered present above this input voltage.</summary>
    private const double GridVoltageThreshold = 80;

    private static readonly int[] DcOutputs = [PowGet12V, PowGetTypeC1, PowGetTypeC2, PowGetQcUsb1, PowGetQcUsb2];

    /// <summary>The fields received so far include the battery charge: enough for a reading.</summary>
    public static bool IsComplete(ProtoFields fields) => fields[CmsBattSoc] is not null || fields[BmsBattSoc] is not null;

    public static UpsStatus Parse(EcoFlowModel model, string serial, ProtoFields fields, DateTimeOffset timestamp)
    {
        var soc = fields.Double(CmsBattSoc) ?? fields.Double(BmsBattSoc);
        var acInVoltage = fields.Double(AcInVoltage);
        var acInW = Abs(fields.Double(PowGetAcIn));

        // Mains is at the input when it has a voltage; else when the AC input is plugged in.
        bool? grid = acInVoltage is { } v
            ? v >= GridVoltageThreshold
            : fields.Integer(AcChargerFlag) is { } plugged
                ? plugged != 0
                : acInW is { } w ? w > 0 : null;

        var dcOut = DcOutputs.Select(fields.Double).Where(x => x is not null).Select(x => Math.Abs(x!.Value)).ToList();

        return new UpsStatus
        {
            Timestamp = timestamp,
            Model = model.Name,
            Serial = serial,
            GridPresent = grid,
            BatteryPercent = soc is { } s and >= 0 and <= 100 ? (int)Math.Round(s) : null,
            CapacityWh = fields.Integer(CmsBattFullEnergy) is { } wh and > 0 ? wh : CapacityWh(model, fields),
            AcInputVoltage = acInVoltage,
            AcOutputVoltage = fields.Double(AcOutVoltage),
            AcInputW = acInW,
            DcInputW = Abs(fields.Double(PowGetPv)),
            AcOutputW = Abs(fields.Double(PowGetAcOut)),
            DcOutputW = dcOut.Count > 0 ? dcOut.Sum() : null,
        };
    }

    /// <summary>The device's own capacity and that of each extra battery plugged into it; the charge covers them all.</summary>
    private static double? CapacityWh(EcoFlowModel model, ProtoFields fields)
    {
        var total = model.CapacityWh;
        foreach (var (flag, serial) in new[] { (ExtraBatteryFlag, ExtraBatterySerial), (ExtraBattery2Flag, ExtraBattery2Serial) })
        {
            if (fields.Integer(flag) is not (null or 0))
                total += EcoFlowModels.ExtraBatteryCapacityWh(fields.Bytes(serial) is { } sn ? Encoding.ASCII.GetString(sn) : null, model);
        }
        return total;
    }

    private static double? Abs(double? value) => value is { } v ? Math.Round(Math.Abs(v), 1) : null;
}
