using UpsTools.Core.Bluetti;

namespace UpsTools.Core;

/// <summary>Registry of supported UPS families. Add new drivers here.</summary>
public sealed class UpsDrivers(Action<string>? log = null)
{
    public IReadOnlyList<IUpsDriver> All { get; } = [new BluettiBleDriver(log)];

    public IUpsDriver? Find(string id) => All.FirstOrDefault(d => d.Id == id);
}
