using UpsTools.Core;

namespace UpsTools.Tests;

public class ScriptChainTests
{
    /// <summary>Plays the scripts: "fail" in a name means it fails; waited-for ones finish when the test says so.</summary>
    private sealed class Scripts
    {
        public List<string> Ran { get; } = [];
        public Dictionary<string, TaskCompletionSource<StepResult>> Running { get; } = [];

        public Task<StepResult> Run(int index, ScriptStep step, CancellationToken cancel)
        {
            Ran.Add(step.Name);
            var result = step.Name.Contains("fail") ? StepResult.Failure("exit code 1") : StepResult.Success;
            if (!step.WaitForExit || step.Name.Contains("quick")) return Task.FromResult(result);

            var exit = new TaskCompletionSource<StepResult>();
            cancel.Register(() => exit.TrySetCanceled(cancel));
            Running[step.Name] = exit;
            return exit.Task;
        }

        public void Exit(string name, bool ok = true) =>
            Running[name].SetResult(ok ? StepResult.Success : StepResult.Failure("exit code 1"));
    }

    private static ScriptChain Chain(params (string Name, bool Wait)[] steps) =>
        new([.. steps.Select(s => new ScriptStep(s.Name, s.Wait))]);

    [Fact]
    public async Task Scripts_not_waited_for_all_start_even_if_one_fails()
    {
        var scripts = new Scripts();
        var chain = Chain(("a.ahk", false), ("fail.bat", false), ("c.ps1", false));
        await chain.RunAsync(scripts.Run, CancellationToken.None);

        Assert.Equal(["a.ahk", "fail.bat", "c.ps1"], scripts.Ran);
        Assert.Equal(["a.ahk", "c.ps1"], chain.Started);
        Assert.Equal("fail.bat", Assert.Single(chain.Failed).Name);
        Assert.Null(chain.StoppedAt);
        Assert.Empty(chain.Skipped);
        Assert.Equal("Started a.ahk and c.ps1; couldn't start fail.bat", ScriptReport.Footnote(chain));
    }

    [Fact]
    public async Task The_next_script_starts_only_after_a_waited_one_finished()
    {
        var scripts = new Scripts();
        var chain = Chain(("pause.ahk", false), ("save.ps1", true), ("hibernate.bat", false));
        var run = chain.RunAsync(scripts.Run, CancellationToken.None);

        // Everything up to the first wait happens synchronously.
        Assert.Equal(["pause.ahk", "save.ps1"], scripts.Ran);
        Assert.Equal("save.ps1", chain.Waiting);
        Assert.Equal(["hibernate.bat"], chain.Pending);
        Assert.Equal("Started pause.ahk; waiting for save.ps1, then hibernate.bat", ScriptReport.Footnote(chain));

        scripts.Exit("save.ps1");
        await run;
        Assert.Equal(["pause.ahk", "save.ps1", "hibernate.bat"], scripts.Ran);
        Assert.Equal(["pause.ahk", "save.ps1", "hibernate.bat"], chain.Started);
        Assert.Null(ScriptReport.Problems(chain, "Grid lost"));
    }

    [Fact]
    public async Task A_failed_waited_script_skips_the_rest()
    {
        var scripts = new Scripts();
        var chain = Chain(("save.ps1", true), ("b.ahk", false), ("c.bat", false));
        var run = chain.RunAsync(scripts.Run, CancellationToken.None);
        Assert.Equal("Waiting for save.ps1, then b.ahk and c.bat", ScriptReport.Footnote(chain));

        scripts.Exit("save.ps1", ok: false);
        await run;
        Assert.Equal(["save.ps1"], scripts.Ran);
        Assert.Equal("save.ps1", chain.StoppedAt?.Name);
        Assert.Equal(["b.ahk", "c.bat"], chain.Skipped);
        Assert.Equal(("Grid lost: save.ps1 failed", "Exit code 1, so b.ahk and c.bat didn't run."),
            ScriptReport.Problems(chain, "Grid lost"));
    }

    [Fact]
    public async Task A_waited_script_that_fails_at_once_is_reported_in_the_event_notification()
    {
        var scripts = new Scripts();
        var chain = Chain(("a.ahk", false), ("quick_fail.ps1", true), ("c.bat", false));
        await chain.RunAsync(scripts.Run, CancellationToken.None);
        Assert.Equal("Started a.ahk; quick_fail.ps1 failed, so c.bat didn't run", ScriptReport.Footnote(chain));
    }

    [Fact]
    public async Task The_last_script_being_waited_for_counts_as_started()
    {
        var scripts = new Scripts();
        var chain = Chain(("a.ahk", false), ("b.ps1", true));
        var run = chain.RunAsync(scripts.Run, CancellationToken.None);
        Assert.Equal("Started a.ahk and b.ps1", ScriptReport.Footnote(chain));
        scripts.Exit("b.ps1");
        await run;
    }

    [Fact]
    public async Task Cancelling_stops_waiting_and_skips_the_rest()
    {
        var scripts = new Scripts();
        using var cancel = new CancellationTokenSource();
        var chain = Chain(("wait_5_min.ps1", true), ("hibernate.bat", false));
        var run = chain.RunAsync(scripts.Run, cancel.Token);

        cancel.Cancel();
        await run;
        Assert.True(chain.Cancelled);
        Assert.Equal(["wait_5_min.ps1"], scripts.Ran);
        Assert.Equal(["wait_5_min.ps1"], chain.Started);
        Assert.Equal(["hibernate.bat"], chain.Skipped);
        Assert.Null(ScriptReport.Problems(chain, "Grid lost"));
    }

    [Fact]
    public async Task Start_failures_after_a_wait_are_reported_afterwards()
    {
        var scripts = new Scripts();
        var chain = Chain(("save.ps1", true), ("fail.exe", false));
        var run = chain.RunAsync(scripts.Run, CancellationToken.None);
        scripts.Exit("save.ps1");
        await run;
        Assert.Equal(("Manual run: couldn't start fail.exe", "fail.exe: exit code 1."),
            ScriptReport.Problems(chain, "Manual run"));
    }

    [Fact]
    public async Task An_exception_from_a_script_is_a_failure()
    {
        var chain = Chain(("a.ps1", true), ("b.ps1", false));
        await chain.RunAsync((_, _, _) => throw new InvalidOperationException("boom"), CancellationToken.None);
        Assert.Equal("boom", chain.StoppedAt?.Problem);
        Assert.Equal(["b.ps1"], chain.Skipped);
    }
}
