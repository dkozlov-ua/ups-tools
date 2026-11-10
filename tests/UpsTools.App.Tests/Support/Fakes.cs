using System.Collections.Concurrent;
using System.Threading.Channels;
using UpsTools.Core;
using UpsTools.Services;

namespace UpsTools.AppTests.Support;

/// <summary>
/// A UPS driver whose one connection reads what the test queues, one reading per poll. A test with several UPSes
/// has one of these each, with a driver ID of its own.
/// </summary>
internal sealed class FakeUps(PairedUps? device = null) : IUpsDriver, IUpsConnection
{
    public const string DriverId = "fake";

    private readonly Channel<Func<CancellationToken, Task<UpsStatus>>> _steps = Channel.CreateUnbounded<Func<CancellationToken, Task<UpsStatus>>>();

    public static readonly PairedUps Paired = new(DriverId, "00:11:22:33:44:55", "FAKE123", "EL100V2");

    public int Reads;
    public int Resets;
    public int Connections;
    public bool Disposed;

    /// <summary>The UPS it plays.</summary>
    public PairedUps Device { get; } = device ?? Paired;

    public string Id => Device.DriverId;
    public string DisplayName => "Fake UPS";

    public Task DiscoverAsync(Action<DiscoveredUps> onFound, TimeSpan duration, CancellationToken cancellationToken)
    {
        onFound(new DiscoveredUps(Id, Device.Address, Device.Name, Device.Model, -50));
        return Task.CompletedTask;
    }

    public IUpsConnection CreateConnection(PairedUps device)
    {
        Connections++;
        Disposed = false;
        return this;
    }

    public static UpsStatus Status(bool? grid, int battery = 80, double output = 120, DateTimeOffset? at = null) => new()
    {
        Timestamp = at ?? DateTimeOffset.Now,
        Model = "EL100V2",
        Serial = "2527008220279",
        GridPresent = grid,
        BatteryPercent = battery,
        CapacityWh = 1024,
        AcInputW = grid == true ? output : 0,
        AcOutputW = output,
    };

    public void Answer(UpsStatus status) => _steps.Writer.TryWrite(_ => Task.FromResult(status));

    public void Answer(bool? grid, int battery = 80, DateTimeOffset? at = null) => Answer(Status(grid, battery, at: at));

    public void Fail(string message = "out of range") =>
        _steps.Writer.TryWrite(_ => Task.FromException<UpsStatus>(new IOException(message)));

    public async Task<UpsStatus> ReadAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Reads);
        var step = await _steps.Reader.ReadAsync(cancellationToken);
        return await step(cancellationToken);
    }

    public Task ResetAsync()
    {
        Interlocked.Increment(ref Resets);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeTray : ITray
{
    public (TrayState State, string Status) Current { get; private set; }
    public List<(TrayState State, string Status)> Updates { get; } = [];
    public List<(string Title, string Text, bool Warning)> Balloons { get; } = [];

    public void Update(TrayState state, string status)
    {
        Current = (state, status);
        Updates.Add(Current);
    }

    public void Notify(string title, string text, bool warning = false) => Balloons.Add((title, text, warning));
}

internal sealed class FakeNotifications : INotifications
{
    public sealed record Shown(string Tag, TrayState State, string Title, string Body, string? Footnote, string? Attribution, bool Important);

    public List<Shown> All { get; } = [];
    public List<UpsNotice> Notices { get; } = [];

    public void Show(UpsNotice notice, string? attribution, string? source = null)
    {
        Notices.Add(notice);
        All.Add(new(Notifier.TagOf(notice.Tag, source), Notifier.ToTray(notice.Tone), notice.Title, notice.Body, notice.Footnote, attribution, notice.Important));
    }

    public void Show(string tag, TrayState state, string title, string body, string? footnote = null,
        string? attribution = null, bool important = false) =>
        All.Add(new(tag, state, title, body, footnote, attribution, important));
}

/// <summary>Plays scripts for the controller: records each run; scripts set to wait finish when the test says.</summary>
internal sealed class FakeScripts
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<StepResult>> _running = new();

    public ConcurrentQueue<(string Name, string Reason)> Ran { get; } = new();

    /// <summary>The environment variables each run got, by script name.</summary>
    public ConcurrentDictionary<string, IReadOnlyDictionary<string, string?>> Variables { get; } = new();

    /// <summary>Scripts that can't be started.</summary>
    public HashSet<string> Broken { get; } = [];

    public Task<StepResult> RunAsync(ScriptAction action, string reason, IReadOnlyDictionary<string, string?> variables,
        CancellationToken cancel)
    {
        var name = Path.GetFileName(action.Path);
        Ran.Enqueue((name, reason));
        Variables[name] = variables;
        if (Broken.Contains(name)) return Task.FromResult(StepResult.Failure("file not found"));
        if (!action.WaitForExit) return Task.FromResult(StepResult.Success);

        var exit = new TaskCompletionSource<StepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancel.Register(() => exit.TrySetCanceled(cancel));
        _running[name] = exit;
        return exit.Task;
    }

    public bool IsRunning(string name) => _running.TryGetValue(name, out var exit) && !exit.Task.IsCompleted;

    public void Exit(string name, int code = 0) =>
        _running[name].SetResult(code == 0 ? StepResult.Success : StepResult.Failure($"exit code {code}"));

    public IReadOnlyList<string> Names => Ran.Select(r => r.Name).ToList();
}

/// <summary>Records the power actions instead of doing them; one can be made to fail.</summary>
internal sealed class FakePower : IPowerControl
{
    public ConcurrentQueue<PowerAction> Done { get; } = new();
    public string? Refuse { get; set; }

    public void Do(PowerAction action)
    {
        if (Refuse is { } why) throw new InvalidOperationException(why);
        Done.Enqueue(action);
    }
}

/// <summary>Plays the countdown window: each countdown waits until the test answers it.</summary>
internal sealed class FakeCountdown
{
    private TaskCompletionSource<bool>? _shown;

    public List<(PowerAction Action, TimeSpan Delay, string Reason)> Shown { get; } = [];
    public bool IsShowing => _shown is { Task.IsCompleted: false };
    public bool WasStopped { get; private set; }

    public Task<bool> ShowAsync(PowerAction action, TimeSpan delay, string reason, CancellationToken cancel)
    {
        Shown.Add((action, delay, reason));
        var shown = _shown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancel.Register(() =>
        {
            if (shown.TrySetCanceled(cancel)) WasStopped = true;
        });
        return shown.Task;
    }

    /// <summary>The time ran out, or "now" was clicked.</summary>
    public void GoAhead() => _shown!.SetResult(true);

    public void Cancel() => _shown!.SetResult(false);
}
