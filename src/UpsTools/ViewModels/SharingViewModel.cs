using System.Collections.ObjectModel;
using UpsTools.Core.Sharing;
using UpsTools.Services;

namespace UpsTools.ViewModels;

/// <summary>Sharing with the other PCs on the network: on or off, the code they share, and who is out there.</summary>
public sealed class SharingViewModel : ObservableObject
{
    private readonly SharingSettings _settings;
    private readonly Action _changed;
    private bool _sharing;

    /// <param name="changed">Saves the settings and starts or stops sharing as they say.</param>
    public SharingViewModel(SharingSettings settings, Action changed)
    {
        _settings = settings;
        _changed = changed;
    }

    public bool Enabled
    {
        get => _settings.Enabled;
        set
        {
            if (_settings.Enabled == value) return;
            _settings.Enabled = value;
            OnPropertyChanged();
            _changed();
        }
    }

    /// <summary>
    /// The code every PC that shares with this one has. Typing another PC's code in joins that PC; a text that
    /// isn't a code is shown as such and changes nothing.
    /// </summary>
    public string Code
    {
        get => _settings.Code ?? "";
        set
        {
            var code = SharingCode.Format(value);
            if (code is null)
            {
                Status = "That isn't a sharing code: it has 16 letters and digits, e.g. 7KQ2-M9XD-4FTB-R1WE.";
                HasProblem = true;
                OnPropertyChanged();
                return;
            }
            if (code == _settings.Code)
            {
                OnPropertyChanged(); // shown as it is kept
                return;
            }
            _settings.Code = code;
            OnPropertyChanged();
            _changed();
        }
    }

    /// <summary>This PC's name, as the other PCs list it.</summary>
    public string PcName { get; } = Environment.MachineName;

    /// <summary>Whether sharing works, or why not; empty while it is off.</summary>
    public string Status { get; private set => Set(ref field, value); } = "";

    public bool HasProblem { get; private set => Set(ref field, value); }

    /// <summary>The other PCs heard on the network, one line each.</summary>
    public ObservableCollection<string> Neighbours { get; } = [];

    public bool HasNeighbours => Neighbours.Count > 0;

    /// <summary>A new code: the other PCs have to be given it to keep sharing with this one.</summary>
    public void NewCode()
    {
        _settings.Code = SharingCode.New();
        OnPropertyChanged(nameof(Code));
        _changed();
    }

    /// <summary>Shows how sharing went: on <paramref name="port"/>, off (neither), or failing with <paramref name="problem"/>.</summary>
    internal void Show(int? port, string? problem = null)
    {
        _sharing = port is not null;
        HasProblem = problem is not null;
        Status = problem is not null ? $"Not sharing: {problem}" : StatusText();
        OnPropertyChanged(nameof(Code));
    }

    internal void ShowNeighbours(IReadOnlyList<NeighbourPc> neighbours)
    {
        Neighbours.Clear();
        foreach (var pc in neighbours)
            Neighbours.Add(!pc.SameCode ? $"{pc.Pc}: shares with another code"
                : pc.Holding.Count == 0 ? $"{pc.Pc}: not connected to a UPS"
                : $"{pc.Pc}: connected to {string.Join(", ", pc.Holding)}");
        OnPropertyChanged(nameof(HasNeighbours));
        if (!HasProblem) Status = StatusText();
    }

    private string StatusText() =>
        !_sharing ? ""
        : Neighbours.Count == 0 ? "Sharing. No other PC found yet. If Windows asks whether UPS Tools may use the network, allow it on private networks."
        : "Sharing.";
}
