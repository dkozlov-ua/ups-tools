using System.Collections.ObjectModel;
using UpsTools.Core;
using UpsTools.Services;
using UpsTools.ViewModels;

namespace UpsTools;

/// <summary>
/// One paired UPS as the app has it: its settings, its monitor, what the dashboard shows of it, its scripts, and the
/// scripts of its events that are still running. <see cref="AppController"/> drives it, on the UI thread.
/// </summary>
public sealed class UpsUnit : ObservableObject
{
    /// <param name="save">Saves the settings.</param>
    /// <param name="rulesChanged">Applies changed event limits to its monitor.</param>
    /// <param name="renamed">The user renamed it.</param>
    internal UpsUnit(UpsSettings settings, ElevatedTasks elevated, Action save, Action rulesChanged, Action renamed)
    {
        Settings = settings;
        EventScripts = new ObservableCollection<EventScriptsViewModel>(Enum.GetValues<UpsEventKind>()
            .Select(kind => new EventScriptsViewModel(kind, settings.ScriptsFor(kind), elevated, save)));
        Configuration = new UpsConfigurationViewModel(settings, save, rulesChanged, renamed);
        Title = settings.Label ?? settings.Device.Title;
    }

    public UpsSettings Settings { get; }
    public PairedUps Device => Settings.Device;

    /// <summary>
    /// What it is called: the name the user gave it, or its brand and model, with what it calls itself when another
    /// UPS has the same brand and model.
    /// </summary>
    public string Title { get; internal set => Set(ref field, value); }

    public DashboardViewModel Dashboard { get; } = new();
    public ObservableCollection<EventScriptsViewModel> EventScripts { get; }

    /// <summary>Its own settings: name, event limits and notifications.</summary>
    public UpsConfigurationViewModel Configuration { get; }

    /// <summary>Polls it; null while it isn't monitored.</summary>
    public UpsMonitor? Monitor { get; internal set; }

    /// <summary>Tells its notifications apart from another UPS's (<see cref="Notifier.TagOf"/>).</summary>
    internal string Source => Settings.Id.Length > 8 ? Settings.Id[..8] : Settings.Id;

    /// <summary>Starts what the log says about it: "Title: " while several UPSes are paired, otherwise nothing.</summary>
    internal string LogPrefix { get; set; } = "";

    /// <summary>When its grid went, during an outage.</summary>
    internal DateTimeOffset? GridLostAt { get; set; }

    /// <summary>The scripts of its events still running, by event.</summary>
    internal Dictionary<UpsEventKind, (ScriptChain Chain, CancellationTokenSource Cancel)> Chains { get; } = [];

    /// <summary>Which of its event groups holds <paramref name="action"/>, if any.</summary>
    internal EventScriptsViewModel? GroupOf(ScriptAction action) =>
        EventScripts.FirstOrDefault(g => g.Items.Any(i => i.Model == action));

    public override string ToString() => Title;
}
