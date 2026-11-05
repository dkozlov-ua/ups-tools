using UpsTools.Core;

namespace UpsTools.Tests;

/// <summary>Built-in actions in a chain: a countdown that stopping the chain undoes, and that the user can cancel.</summary>
public class PowerStepTests
{
    private static readonly ScriptStep Hibernate = new("Hibernate", WaitForExit: true, UndoneByStopping: true);

    /// <summary>Waits for every step until the test finishes it; cancelling the token cancels the wait.</summary>
    private sealed class Steps
    {
        public Dictionary<string, TaskCompletionSource<StepResult>> Running { get; } = [];

        public Task<StepResult> Run(int index, ScriptStep step, CancellationToken cancel)
        {
            if (!step.WaitForExit) return Task.FromResult(StepResult.Success);
            var done = new TaskCompletionSource<StepResult>();
            cancel.Register(() => done.TrySetCanceled(cancel));
            Running[step.Name] = done;
            return done.Task;
        }
    }

    [Fact]
    public async Task Stopping_the_chain_during_a_countdown_means_the_action_never_happened()
    {
        var steps = new Steps();
        var chain = new ScriptChain([new("save.ps1", false), Hibernate, new("after.ps1", false)]);
        using var cancel = new CancellationTokenSource();
        var run = chain.RunAsync(steps.Run, cancel.Token);

        Assert.Equal(["after.ps1"], chain.Pending);
        Assert.Equal(["Hibernate", "after.ps1"], chain.Unstarted);
        Assert.Same(Hibernate, chain.WaitingStep);
        Assert.Equal("Started save.ps1; counting down to Hibernate, then after.ps1", ScriptReport.Footnote(chain));

        await cancel.CancelAsync();
        await run;

        Assert.True(chain.Cancelled);
        Assert.Equal(["save.ps1"], chain.Started);
        Assert.Equal(["Hibernate", "after.ps1"], chain.Skipped);
        Assert.Null(chain.WaitingStep);
        Assert.Null(ScriptReport.Problems(chain, "Battery low"));
    }

    [Fact]
    public async Task Stopping_the_chain_while_a_script_runs_leaves_it_running()
    {
        var steps = new Steps();
        var chain = new ScriptChain([new("save.ps1", true), new("after.ps1", false)]);
        using var cancel = new CancellationTokenSource();
        var run = chain.RunAsync(steps.Run, cancel.Token);
        Assert.Equal(["after.ps1"], chain.Unstarted);

        await cancel.CancelAsync();
        await run;
        Assert.Equal(["save.ps1"], chain.Started);
        Assert.Equal(["after.ps1"], chain.Skipped);
    }

    [Fact]
    public async Task A_countdown_as_the_last_step_says_so()
    {
        var steps = new Steps();
        var chain = new ScriptChain([Hibernate]);
        var run = chain.RunAsync(steps.Run, CancellationToken.None);

        Assert.Equal("Counting down to Hibernate", ScriptReport.Footnote(chain));
        Assert.Equal(["Hibernate"], chain.Unstarted);

        steps.Running["Hibernate"].SetResult(StepResult.Success);
        await run;
        Assert.Equal(["Hibernate"], chain.Started);
        Assert.Empty(chain.Unstarted);
        Assert.Equal("Started Hibernate", ScriptReport.Footnote(chain));
    }

    [Fact]
    public async Task Cancelled_by_the_user_stops_the_chain_without_reporting_a_problem()
    {
        var steps = new Steps();
        var chain = new ScriptChain([Hibernate, new("after.ps1", false)]);
        var run = chain.RunAsync(steps.Run, CancellationToken.None);

        steps.Running["Hibernate"].SetResult(StepResult.CancelledByUser);
        await run;

        Assert.False(chain.Cancelled);
        Assert.Equal(new FailedStep(0, "Hibernate", "cancelled", ByUser: true), chain.StoppedAt);
        Assert.Equal(["after.ps1"], chain.Skipped);
        Assert.Equal("Hibernate was cancelled, so after.ps1 didn't run", ScriptReport.Footnote(chain));
        Assert.Null(ScriptReport.Problems(chain, "Battery low"));
    }

    [Fact]
    public async Task Other_failures_are_still_reported_after_a_cancelled_countdown()
    {
        var chain = new ScriptChain([new("gone.bat", false), Hibernate]);
        await chain.RunAsync((i, _, _) => Task.FromResult(i == 0 ? StepResult.Failure("file not found") : StepResult.CancelledByUser),
            CancellationToken.None);

        Assert.Equal("Couldn't start gone.bat; Hibernate was cancelled", ScriptReport.Footnote(chain));
        Assert.Equal(("Battery low: couldn't start gone.bat", "gone.bat: file not found."), ScriptReport.Problems(chain, "Battery low"));
    }

    [Theory]
    [InlineData(PowerAction.Sleep, "Sleep", "Putting the PC to sleep")]
    [InlineData(PowerAction.Hibernate, "Hibernate", "Hibernating the PC")]
    [InlineData(PowerAction.ShutDown, "Shut down", "Shutting down the PC")]
    public void Power_actions_have_names(PowerAction action, string title, string doing)
    {
        Assert.Equal(title, action.Title());
        Assert.Equal(doing, action.Doing());
        Assert.EndsWith(".", action.Description());
    }

    [Fact]
    public void Unknown_power_actions_fall_back_to_their_number()
    {
        var unknown = (PowerAction)9;
        Assert.Equal(("9", "9", ""), (unknown.Title(), unknown.Doing(), unknown.Description()));
    }
}
