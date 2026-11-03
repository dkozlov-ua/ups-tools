using System.Collections.ObjectModel;
using System.IO;
using UpsTools.Core;
using UpsTools.Services;

namespace UpsTools.ViewModels;

/// <summary>Scripts attached to one UPS event. Edits write straight through to <see cref="AppSettings"/>.</summary>
public sealed class EventScriptsViewModel : ObservableObject
{
    private readonly List<ScriptAction> _model;
    private readonly Action _changed;

    public EventScriptsViewModel(UpsEventKind kind, List<ScriptAction> model, Action changed)
    {
        Kind = kind;
        _model = model;
        _changed = changed;
        foreach (var action in model) Items.Add(new ScriptItemViewModel(action, changed));
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
        Items.Add(new ScriptItemViewModel(action, _changed));
        _changed();
    }

    public void Remove(ScriptItemViewModel item)
    {
        _model.Remove(item.Model);
        Items.Remove(item);
        _changed();
    }
}

public sealed class ScriptItemViewModel(ScriptAction model, Action changed) : ObservableObject
{
    public ScriptAction Model { get; } = model;

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
            changed();
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
            changed();
        }
    }
}
