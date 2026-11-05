using UpsTools.AppTests.Support;
using UpsTools.Core;
using UpsTools.Services;

namespace UpsTools.AppTests;

/// <summary>Built-in actions: the countdown, then the action, with a fake PC.</summary>
public sealed class PowerRunnerTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly AppLog _log;
    private readonly FakePower _power = new();
    private readonly FakeCountdown _countdown = new();
    private readonly PowerRunner _runner;

    public PowerRunnerTests()
    {
        _log = new AppLog(_folder.Path);
        _runner = new PowerRunner(_log, _power, _countdown.ShowAsync);
    }

    public void Dispose() => _folder.Dispose();

    private static ScriptAction Action(PowerAction power, int? countdown = 60) => new() { Power = power, CountdownSeconds = countdown };

    private IReadOnlyList<string> Log() => _log.Recent.Select(e => e.Message).ToList();

    [Fact]
    public async Task It_counts_down_then_acts()
    {
        var run = _runner.RunAsync(Action(PowerAction.Hibernate, 45), "Battery low", CancellationToken.None);
        Assert.Equal((PowerAction.Hibernate, TimeSpan.FromSeconds(45), "Battery low"), Assert.Single(_countdown.Shown));
        Assert.False(run.IsCompleted);
        Assert.Empty(_power.Done);

        _countdown.GoAhead();

        Assert.Equal(StepResult.Success, await run);
        Assert.Equal([PowerAction.Hibernate], _power.Done);
        Assert.Equal(["Battery low: Hibernating the PC in 45 s unless cancelled", "Battery low: Hibernating the PC"], Log());
    }

    [Fact]
    public async Task Cancelling_the_countdown_keeps_the_pc_on()
    {
        var run = _runner.RunAsync(Action(PowerAction.ShutDown), "Manual run", CancellationToken.None);
        _countdown.Cancel();

        Assert.Equal(StepResult.CancelledByUser, await run);
        Assert.Empty(_power.Done);
        Assert.Contains(_log.Recent, e => e.Level == LogLevel.Warning && e.Message == "Manual run: Shut down was cancelled");
    }

    [Fact]
    public async Task Stopping_it_closes_the_countdown_and_keeps_the_pc_on()
    {
        using var stop = new CancellationTokenSource();
        var run = _runner.RunAsync(Action(PowerAction.Sleep), "Battery low", stop.Token);
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(_countdown.WasStopped);
        Assert.Empty(_power.Done);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Without_a_countdown_it_acts_at_once(int seconds)
    {
        var result = await _runner.RunAsync(Action(PowerAction.Sleep, seconds), "Battery low", CancellationToken.None);
        Assert.Equal(StepResult.Success, result);
        Assert.Empty(_countdown.Shown);
        Assert.Equal([PowerAction.Sleep], _power.Done);
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData(5000, 600)]
    public async Task The_countdown_is_a_minute_unless_set_and_ten_at_most(int? seconds, int expected)
    {
        var run = _runner.RunAsync(Action(PowerAction.Hibernate, seconds), "x", CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(expected), _countdown.Shown.Single().Delay);
        _countdown.Cancel();
        await run;
    }

    [Fact]
    public async Task A_refusal_from_windows_is_a_failure()
    {
        _power.Refuse = "hibernation is off";
        var run = _runner.RunAsync(Action(PowerAction.Hibernate), "Battery low", CancellationToken.None);
        _countdown.GoAhead();

        Assert.Equal(StepResult.Failure("hibernation is off"), await run);
        Assert.Contains(_log.Recent, e => e.Level == LogLevel.Warning && e.Message == "Battery low: Hibernate failed: hibernation is off");
    }

    [Fact]
    public async Task A_script_is_not_a_built_in_action()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _runner.RunAsync(new ScriptAction { Path = "a.ps1" }, "x", CancellationToken.None));
    }

    [Fact]
    public void The_simulated_pc_only_logs()
    {
        new SimulatedPower(_log).Do(PowerAction.ShutDown);
        Assert.Equal(["Simulated UPS: Shutting down the PC skipped"], Log());
    }
}
