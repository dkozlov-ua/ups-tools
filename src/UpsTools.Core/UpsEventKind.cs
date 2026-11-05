namespace UpsTools.Core;

/// <summary>State transitions that scripts can be attached to.</summary>
public enum UpsEventKind
{
    GridLost,
    GridRestored,
    UpsUnreachable,
    UpsReachable,
}

public static class UpsEventKindExtensions
{
    public static string Title(this UpsEventKind kind) => kind switch
    {
        UpsEventKind.GridLost => "Grid lost",
        UpsEventKind.GridRestored => "Grid restored",
        UpsEventKind.UpsUnreachable => "UPS unreachable",
        UpsEventKind.UpsReachable => "UPS reachable again",
        _ => kind.ToString(),
    };

    /// <summary>The event that undoes this one: its scripts stop when this one fires.</summary>
    public static UpsEventKind Opposite(this UpsEventKind kind) => kind switch
    {
        UpsEventKind.GridLost => UpsEventKind.GridRestored,
        UpsEventKind.GridRestored => UpsEventKind.GridLost,
        UpsEventKind.UpsUnreachable => UpsEventKind.UpsReachable,
        UpsEventKind.UpsReachable => UpsEventKind.UpsUnreachable,
        _ => kind,
    };

    public static string Description(this UpsEventKind kind) => kind switch
    {
        UpsEventKind.GridLost => "Mains power at the UPS input disappeared; the UPS is running on battery.",
        UpsEventKind.GridRestored => "Mains power at the UPS input came back.",
        UpsEventKind.UpsUnreachable => $"The UPS did not answer {UpsStateTracker.DefaultFailuresBeforeUnreachable} polls in a row.",
        UpsEventKind.UpsReachable => "The UPS answered again after being unreachable.",
        _ => "",
    };
}
