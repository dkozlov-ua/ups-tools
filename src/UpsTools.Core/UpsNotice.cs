using System.Globalization;

namespace UpsTools.Core;

public enum NoticeTone
{
    Neutral,
    Good,
    Warning,
    Problem,
}

/// <summary>What a desktop notification about a UPS event says.</summary>
public sealed record UpsNotice(UpsEventKind Kind, NoticeTone Tone, string Title, string Body, string? Footnote)
{
    /// <summary>
    /// Notifications with the same tag replace each other, so the notification list shows the current
    /// state: "Grid restored" replaces "Grid lost", "UPS reachable again" replaces "UPS unreachable".
    /// </summary>
    public string Tag => Kind is UpsEventKind.GridLost or UpsEventKind.GridRestored ? "grid" : "link";

    /// <summary>Stays on screen longer: something stopped working.</summary>
    public bool Important => Kind is UpsEventKind.GridLost or UpsEventKind.UpsUnreachable;

    /// <param name="kind">The event.</param>
    /// <param name="status">The reading the event was raised with; for "UPS unreachable", the last good reading.</param>
    /// <param name="outage">For "Grid restored": how long the UPS ran on battery, when known.</param>
    /// <param name="scripts">This event's scripts, as they stand when the notification goes out.</param>
    /// <param name="stopped">The opposite event's scripts that this event stopped, and the ones of them it skipped.</param>
    public static UpsNotice For(UpsEventKind kind, UpsStatus? status, TimeSpan? outage = null,
        ScriptChain? scripts = null, (UpsEventKind Kind, IReadOnlyList<string> Skipped)? stopped = null)
    {
        var footnote = ScriptReport.Footnote(scripts, stopped);
        var battery = status?.BatteryPercent is { } soc ? $"Battery {soc} %" : null;

        switch (kind)
        {
            case UpsEventKind.GridLost:
            {
                var body = Join(battery, Runtime(status)) ?? "The UPS switched to battery.";
                return new(kind, NoticeTone.Warning, "Grid lost — running on battery", body, footnote);
            }

            case UpsEventKind.GridRestored:
            {
                var after = outage switch
                {
                    null => "",
                    { TotalMinutes: < 1 } => " after less than a minute on battery",
                    { } t => $" after {UpsFormat.Duration(t)} on battery",
                };
                var body = $"Power is back{after}." + (battery is null ? "" : $" {battery}.");
                return new(kind, NoticeTone.Good, "Grid restored", body, footnote);
            }

            case UpsEventKind.UpsUnreachable:
            {
                var last = status is null
                    ? ""
                    : $" (last reading at {status.Timestamp.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)})";
                var body = $"The UPS stopped answering{last}. Power cuts can't be detected until it answers again.";
                return new(kind, NoticeTone.Problem, "UPS unreachable", body, footnote);
            }

            case UpsEventKind.UpsReachable:
            {
                var state = status?.GridPresent switch
                {
                    true => Join("Grid online", battery),
                    false => Join("On battery", battery, Runtime(status)),
                    null => battery,
                };
                var body = "The UPS is answering again." + (state is null ? "" : $" {state}.");
                var tone = status?.GridPresent == false ? NoticeTone.Warning : NoticeTone.Good;
                return new(kind, tone, "UPS reachable again", body, footnote);
            }

            default:
                return new(kind, NoticeTone.Neutral, kind.Title(), kind.Description(), footnote);
        }
    }

    private static string? Runtime(UpsStatus? status)
    {
        if (status?.EstimatedRuntime is not { } runtime) return null;
        var left = $"about {UpsFormat.Duration(runtime)} left";
        return status.TotalOutputW is { } load ? $"{left} at {UpsFormat.Watts(load)}" : left;
    }

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(p => p is not null).ToList();
        return present.Count == 0 ? null : string.Join(" · ", present);
    }
}
