namespace UpsTools.Core.EcoFlow;

/// <param name="Name">Shown in the app, e.g. "River 3 Plus".</param>
/// <param name="SerialPrefixes">The first four characters of the serial numbers of this model.</param>
/// <param name="CapacityWh">Nominal battery capacity, for when the device doesn't report its own.</param>
/// <param name="Tested">Confirmed against real hardware by this project.</param>
public sealed record EcoFlowModel(string Name, IReadOnlyList<string> SerialPrefixes, double? CapacityWh, bool Tested);

/// <summary>
/// EcoFlow models that report their state as the River 3 "DisplayPropertyUpload" message (pr705), told apart by the
/// start of their serial number, per the device classes in ha-ef-ble. Other EcoFlow series use other messages.
/// </summary>
public static class EcoFlowModels
{
    public static readonly IReadOnlyList<EcoFlowModel> All =
    [
        new("River 3 Plus", ["R631"], 286, Tested: true),
        new("River 3 Plus Wireless", ["R635"], 286, false),
        new("River 3 Plus (270)", ["R634"], null, false),
        new("River 3", ["R651"], 245, false),
        new("River 3 (230Wh)", ["R653"], 230, false),
        new("River 3 UPS", ["R655"], 245, false),
        new("River 3 UPS (230Wh)", ["R654"], 230, false),
    ];

    /// <summary>Extra batteries, which plug into a River 3 Plus and report only their serial number and charge.</summary>
    public static readonly IReadOnlyList<EcoFlowModel> ExtraBatteries =
    [
        new("River 3 Plus Smart Extra Battery", ["R633"], 286, Tested: true),
    ];

    /// <summary>
    /// The capacity of an extra battery: by its serial number, or, for one not listed, that of the device it is
    /// plugged into (EcoFlow's extra batteries match their device).
    /// </summary>
    public static double? ExtraBatteryCapacityWh(string? serial, EcoFlowModel device) =>
        Find(ExtraBatteries, serial)?.CapacityWh ?? device.CapacityWh;

    public static EcoFlowModel? FromSerial(string? serial) => Find(All, serial);

    private static EcoFlowModel? Find(IReadOnlyList<EcoFlowModel> models, string? serial) =>
        serial is { Length: >= 4 }
            ? models.FirstOrDefault(m => m.SerialPrefixes.Contains(serial[..4], StringComparer.OrdinalIgnoreCase))
            : null;
}
