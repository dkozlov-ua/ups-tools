namespace UpsTools.Core;

/// <summary>State transitions that scripts can be attached to.</summary>
public enum UpsEventKind
{
    GridLost,
    BatteryLow,
    GridRestored,
    UpsUnreachable,
    UpsReachable,
}

public static class UpsEventKindExtensions
{
    public static string Title(this UpsEventKind kind) => kind switch
    {
        UpsEventKind.GridLost => "Grid lost",
        UpsEventKind.BatteryLow => "Battery low",
        UpsEventKind.GridRestored => "Grid restored",
        UpsEventKind.UpsUnreachable => "UPS unreachable",
        UpsEventKind.UpsReachable => "UPS reachable again",
        _ => kind.ToString(),
    };

    /// <summary>The events that this one undoes: their scripts that haven't started yet don't run when it fires.</summary>
    public static IReadOnlyList<UpsEventKind> Stops(this UpsEventKind kind) => kind switch
    {
        UpsEventKind.GridLost => [UpsEventKind.GridRestored],
        UpsEventKind.GridRestored => [UpsEventKind.GridLost, UpsEventKind.BatteryLow],
        UpsEventKind.UpsUnreachable => [UpsEventKind.UpsReachable],
        UpsEventKind.UpsReachable => [UpsEventKind.UpsUnreachable],
        _ => [],
    };

    public static string Description(this UpsEventKind kind) => kind switch
    {
        UpsEventKind.GridLost => "Mains power at the UPS input disappeared; the UPS is running on battery.",
        UpsEventKind.BatteryLow => "On battery, the charge or the time left reached its limit, or the UPS stopped answering (set in Configuration). Once per outage.",
        UpsEventKind.GridRestored => "Mains power at the UPS input came back.",
        UpsEventKind.UpsUnreachable => $"The UPS did not answer {UpsStateTracker.DefaultFailuresBeforeUnreachable} polls in a row.",
        UpsEventKind.UpsReachable => "The UPS answered again after being unreachable.",
        _ => "",
    };
}
