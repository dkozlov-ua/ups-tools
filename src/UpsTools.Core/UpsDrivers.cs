using UpsTools.Core.Bluetti;

namespace UpsTools.Core;

/// <summary>Registry of supported UPS families. Add new drivers here.</summary>
public sealed class UpsDrivers
{
    /// <summary>The real drivers, or only the simulator when <see cref="SimulatedUpsDriver.EnvironmentVariable"/> is set.</summary>
    public UpsDrivers(Action<string>? log = null)
        : this(SimulatedUpsDriver.FromEnvironment() is { } simulator ? [simulator] : [new BluettiBleDriver(log)])
    {
    }

    public UpsDrivers(IReadOnlyList<IUpsDriver> all) => All = all;

    public IReadOnlyList<IUpsDriver> All { get; }

    public IUpsDriver? Find(string id) => All.FirstOrDefault(d => d.Id == id);
}
