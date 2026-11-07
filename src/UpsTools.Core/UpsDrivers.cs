using UpsTools.Core.Bluetti;
using UpsTools.Core.EcoFlow;

namespace UpsTools.Core;

/// <summary>Registry of supported UPS families. Add new drivers here.</summary>
public sealed class UpsDrivers
{
    /// <summary>The real drivers, or only the simulator when <see cref="SimulatedUpsDriver.EnvironmentVariable"/> is set.</summary>
    public UpsDrivers(Action<string>? log = null)
        : this(SimulatedUpsDriver.FromEnvironment() is { } simulator ? [simulator] : [new BluettiBleDriver(log), new EcoFlowBleDriver(log)])
    {
    }

    public UpsDrivers(IReadOnlyList<IUpsDriver> all) => All = all;

    public IReadOnlyList<IUpsDriver> All { get; }

    public IUpsDriver? Find(string id) => All.FirstOrDefault(d => d.Id == id);

    /// <summary>The brand and model of a device of the given driver, e.g. "EcoFlow River 3 Plus".</summary>
    public static string Title(string driverId, string model) =>
        $"{(driverId == EcoFlowBleDriver.DriverId ? "EcoFlow" : "Bluetti")} {model}";
}
