namespace UpsTools.Core.Bluetti;

/// <param name="Name">Model prefix of the BLE advertised name, e.g. "EL100V2" in "EL100V22527008220279".</param>
/// <param name="CapacityWh">Nominal battery capacity, used to estimate runtime when the device doesn't report it.</param>
/// <param name="HasAcInputVoltage">Whether register 1314 (AC input voltage) exists; otherwise grid is inferred from AC input power.</param>
/// <param name="Tested">Confirmed against real hardware by this project.</param>
public sealed record BluettiModel(string Name, double? CapacityWh, bool HasAcInputVoltage, bool Tested);

/// <summary>
/// Models sharing the "V2" register layout (SOC @102, runtime @104, powers @140–146, AC input voltage @1314),
/// per the device maps in bluetti-bt-lib. Older V1 models (AC200M, AC300, EB3A, EP500…) use a different layout.
/// </summary>
public static class BluettiModels
{
    public static readonly IReadOnlyList<BluettiModel> All =
    [
        new("EL100V2", 1024, HasAcInputVoltage: true, Tested: true),
        new("EL30V2", null, true, false),
        new("EL10", null, true, false),
        new("AC180T", null, true, false),
        new("AC180P", null, true, false),
        new("AC180", null, true, false),
        new("AC70P", null, true, false),
        new("AC70", null, true, false),
        new("AC60P", null, true, false),
        new("AC60", null, true, false),
        new("AC2A", null, false, false),
        new("AC2P", null, false, false),
        new("AP300", null, true, false),
        new("PR100V2", null, true, false),
        new("PR30V2", null, true, false),
        new("HANDSFREE2", null, true, false),
        new("HANDSFREE1", null, true, false),
    ];

    /// <summary>Match an advertised BLE name to a model. Longest prefix wins ("EL100V2…" is not "EL10").</summary>
    public static BluettiModel? FromAdvertisedName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return All.Where(m => name.StartsWith(m.Name, StringComparison.OrdinalIgnoreCase))
                  .MaxBy(m => m.Name.Length);
    }

    public static BluettiModel? FromName(string? model) =>
        All.FirstOrDefault(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase));
}
