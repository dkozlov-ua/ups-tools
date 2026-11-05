using UpsTools.Core;

namespace UpsTools.Tests;

/// <summary>A script that is waited for, but only up to its time limit.</summary>
public class TimeLimitTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    /// <summary>Waited-for steps never finish on their own; the others start at once.</summary>
    private static Task<StepResult> Hang(int index, ScriptStep step, CancellationToken cancel) =>
        step.WaitForExit ? Task.Delay(Timeout.Infinite, cancel).ContinueWith(_ => StepResult.Success, cancel) : Task.FromResult(StepResult.Success);

    [Fact]
    public async Task A_script_still_running_at_its_limit_is_left_running_and_the_next_one_starts()
    {
        var chain = new ScriptChain([new("save.ps1", true, TimeLimit: Short), new("hibernate.ps1", false)]);
        var late = new List<string>();

        var run = chain.RunAsync(Hang, CancellationToken.None, step => late.Add(step.Name));
        Assert.Equal("Waiting for save.ps1, then hibernate.ps1", ScriptReport.Footnote(chain));
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["save.ps1"], late);
        Assert.Equal(["save.ps1", "hibernate.ps1"], chain.Started);
        Assert.Equal("save.ps1", Assert.Single(chain.TimedOut).Name);
        Assert.Null(chain.StoppedAt);
        Assert.False(chain.Cancelled);
        Assert.Equal("Started save.ps1 and hibernate.ps1; save.ps1 took too long", ScriptReport.Footnote(chain));
        Assert.Equal(("Battery low: save.ps1 took too long", "save.ps1 was still running after 0 m, so the chain went on without it."),
            ScriptReport.Problems(chain, "Battery low"));
    }

    [Fact]
    public async Task One_that_finishes_in_time_is_not_late()
    {
        var chain = new ScriptChain([new("quick.ps1", true, TimeLimit: TimeSpan.FromMinutes(5)), new("next.ps1", false)]);
        await chain.RunAsync((_, _, _) => Task.FromResult(StepResult.Success), CancellationToken.None);
        Assert.Empty(chain.TimedOut);
        Assert.Null(ScriptReport.Problems(chain, "Grid lost"));
    }

    [Fact]
    public async Task One_that_fails_in_time_still_stops_the_chain()
    {
        var chain = new ScriptChain([new("save.ps1", true, TimeLimit: TimeSpan.FromMinutes(5)), new("next.ps1", false)]);
        await chain.RunAsync((_, _, _) => Task.FromResult(StepResult.Failure("exit code 1")), CancellationToken.None);
        Assert.Equal("save.ps1", chain.StoppedAt!.Name);
        Assert.Equal(["next.ps1"], chain.Skipped);
    }

    [Fact]
    public async Task Stopping_the_chain_before_the_limit_is_a_stop_not_a_time_out()
    {
        var chain = new ScriptChain([new("save.ps1", true, TimeLimit: TimeSpan.FromMinutes(5)), new("next.ps1", false)]);
        using var stop = new CancellationTokenSource();
        var run = chain.RunAsync(Hang, stop.Token);
        await stop.CancelAsync();
        await run;

        Assert.True(chain.Cancelled);
        Assert.Empty(chain.TimedOut);
        Assert.Equal(["next.ps1"], chain.Skipped);
    }

    [Fact]
    public async Task A_step_not_waited_for_has_no_limit_to_reach()
    {
        CancellationToken seen = default;
        var chain = new ScriptChain([new("a.ps1", false, TimeLimit: Short)]);
        await chain.RunAsync((_, _, token) =>
        {
            seen = token;
            return Task.FromResult(StepResult.Success);
        }, CancellationToken.None);
        Assert.False(seen.CanBeCanceled);
    }

    [Fact]
    public async Task Late_scripts_are_named_along_with_other_problems()
    {
        var chain = new ScriptChain([new("gone.bat", false), new("slow.ps1", true, TimeLimit: Short), new("save.ps1", true)]);
        await chain.RunAsync((i, step, token) => i switch
        {
            0 => Task.FromResult(StepResult.Failure("file not found")),
            1 => Hang(i, step, token),
            _ => Task.FromResult(StepResult.Failure("exit code 2")),
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(("Grid lost: save.ps1 failed",
                "Exit code 2. Couldn't start gone.bat either. slow.ps1 was still running after 0 m, so the chain went on without it."),
            ScriptReport.Problems(chain, "Grid lost"));
    }
}
