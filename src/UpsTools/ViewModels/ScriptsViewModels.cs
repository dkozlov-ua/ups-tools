using System.Collections.ObjectModel;
using System.IO;
using UpsTools.Core;
using UpsTools.Services;

namespace UpsTools.ViewModels;

/// <summary>Scripts attached to one UPS event. Edits write straight through to <see cref="AppSettings"/>.</summary>
public sealed class EventScriptsViewModel : ObservableObject
{
    private readonly List<ScriptAction> _model;
    private readonly ElevatedTasks _elevated;
    private readonly Action _changed;

    public EventScriptsViewModel(UpsEventKind kind, List<ScriptAction> model, ElevatedTasks elevated, Action changed)
    {
        Kind = kind;
        _model = model;
        _elevated = elevated;
        _changed = changed;
        foreach (var action in model) Items.Add(new ScriptItemViewModel(action, elevated, changed));
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public UpsEventKind Kind { get; }
    public string Title => Kind.Title();
    public string Description => Kind.Description();
    public ObservableCollection<ScriptItemViewModel> Items { get; } = [];
    public bool IsEmpty => Items.Count == 0;
    public IEnumerable<ScriptAction> EnabledActions => _model.Where(a => a.Enabled);

    public void Add(string path)
    {
        var action = new ScriptAction { Path = path };
        _model.Add(action);
        Items.Add(new ScriptItemViewModel(action, _elevated, _changed));
        _changed();
    }

    /// <returns>Null when removed; otherwise why the script's administrator task couldn't be deleted (it stays then).</returns>
    public async Task<string?> RemoveAsync(ScriptItemViewModel item)
    {
        if (item.Model.RunElevated && await _elevated.UnregisterAsync(item.Model) is { } problem) return problem;
        _model.Remove(item.Model);
        Items.Remove(item);
        _changed();
        return null;
    }

    /// <summary>Moves <paramref name="item"/> to position <paramref name="to"/>: scripts run in the listed order.</summary>
    public void Move(ScriptItemViewModel item, int to)
    {
        var from = Items.IndexOf(item);
        if (from < 0) return;
        to = Math.Clamp(to, 0, Items.Count - 1);
        if (from == to) return;

        Items.Move(from, to);
        _model.RemoveAt(from);
        _model.Insert(to, item.Model);
        _changed();
    }
}

public sealed class ScriptItemViewModel : ObservableObject
{
    private readonly ElevatedTasks _elevated;
    private readonly Action _changed;
    private bool _busy;
    private string? _elevationNote;
    private bool _canRepair;

    public ScriptItemViewModel(ScriptAction model, ElevatedTasks elevated, Action changed)
    {
        Model = model;
        _elevated = elevated;
        _changed = changed;
        CheckElevation();
    }

    public ScriptAction Model { get; }

    public string FileName => Path.GetFileName(Model.Path);
    public string FullPath => Model.Path;
    public bool Exists => File.Exists(Model.Path);

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled == value) return;
            Model.Enabled = value;
            OnPropertyChanged();
            _changed();
        }
    }

    public bool WaitForExit
    {
        get => Model.WaitForExit;
        set
        {
            if (Model.WaitForExit == value) return;
            Model.WaitForExit = value;
            OnPropertyChanged();
            _changed();
        }
    }

    public string Arguments
    {
        get => Model.Arguments;
        set
        {
            value = value.Trim();
            if (Model.Arguments == value) return;
            Model.Arguments = value;
            OnPropertyChanged();
            _changed();
            // The arguments are part of the administrator task.
            if (Model.RunElevated) _ = RepairAsync();
        }
    }

    /// <summary>
    /// Turning it on or off registers or deletes the script's administrator task, which asks for administrator
    /// rights; the checkbox goes back if that fails.
    /// </summary>
    public bool RunElevated
    {
        get => Model.RunElevated;
        set => _ = SetElevatedAsync(value);
    }

    /// <summary>No administrator task is being registered or deleted right now.</summary>
    public bool IsIdle => !_busy;

    /// <summary>Why the script can't run as administrator, or why the last change to that failed.</summary>
    public string? ElevationNote
    {
        get => _elevationNote;
        private set
        {
            if (Set(ref _elevationNote, value)) OnPropertyChanged(nameof(HasElevationNote));
        }
    }

    public bool HasElevationNote => _elevationNote is not null;

    /// <summary>Set to run as administrator, but its task is missing or out of date.</summary>
    public bool CanRepair
    {
        get => _canRepair;
        private set => Set(ref _canRepair, value);
    }

    /// <summary>Registers the administrator task again.</summary>
    public async Task RepairAsync()
    {
        if (!Model.RunElevated || !BeginBusy()) return;
        try
        {
            await _elevated.RegisterAsync(Model);
        }
        finally
        {
            EndBusy();
            CheckElevation();
        }
    }

    private async Task SetElevatedAsync(bool value)
    {
        if (value == Model.RunElevated || !BeginBusy())
        {
            OnPropertyChanged(nameof(RunElevated)); // undo the checkbox change
            return;
        }

        string? problem;
        try
        {
            problem = value ? await _elevated.RegisterAsync(Model) : await _elevated.UnregisterAsync(Model);
            if (problem is null)
            {
                Model.RunElevated = value;
                _changed();
            }
        }
        finally
        {
            EndBusy();
            OnPropertyChanged(nameof(RunElevated));
        }

        CheckElevation();
        if (problem is not null)
            ElevationNote = value
                ? $"Not set to run as administrator: {problem}."
                : $"Still runs as administrator: {problem}.";
    }

    private void CheckElevation()
    {
        var problem = Model.RunElevated ? _elevated.Problem(Model) : null;
        CanRepair = problem is not null;
        ElevationNote = problem is null ? null : $"Can't run as administrator: {problem}.";
    }

    private bool BeginBusy()
    {
        if (_busy) return false;
        _busy = true;
        OnPropertyChanged(nameof(IsIdle));
        return true;
    }

    private void EndBusy()
    {
        _busy = false;
        OnPropertyChanged(nameof(IsIdle));
    }
}
