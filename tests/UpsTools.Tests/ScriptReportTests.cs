using UpsTools.Core;

namespace UpsTools.Tests;

/// <summary>Script chains and how notifications describe them, beyond <see cref="ScriptChainTests"/>.</summary>
public class ScriptReportTests
{
    private static ScriptChain Chain(params (string Name, bool Wait)[] steps) =>
        new([.. steps.Select(s => new ScriptStep(s.Name, s.Wait))]);

    private static Task<StepResult> Ok(int i, ScriptStep s, CancellationToken c) => Task.FromResult(StepResult.Success);

    [Theory]
    [InlineData(new string[0], "")]
    [InlineData(new[] { "a" }, "a")]
    [InlineData(new[] { "a", "b" }, "a and b")]
    [InlineData(new[] { "a", "b", "c" }, "a, b and 1 more")]
    [InlineData(new[] { "a", "b", "c", "d", "e" }, "a, b and 3 more")]
    public void Lists_name_two_and_count_the_rest(string[] names, string expected)
    {
        Assert.Equal(expected, ScriptReport.List(names));
    }

    [Fact]
    public async Task An_empty_chain_is_done_at_once_and_says_nothing()
    {
        var chain = new ScriptChain([]);
        await chain.RunAsync(Ok, CancellationToken.None);
        Assert.True(chain.Done);
        Assert.Empty(chain.Pending);
        Assert.Empty(chain.Skipped);
        Assert.Null(ScriptReport.Footnote(chain));
        Assert.Null(ScriptReport.Problems(chain, "Grid lost"));
        Assert.Null(ScriptReport.Footnote(null));
    }

    [Fact]
    public async Task A_chain_runs_once()
    {
        var chain = Chain(("a", false));
        await chain.RunAsync(Ok, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chain.RunAsync(Ok, CancellationToken.None));
    }

    [Fact]
    public async Task A_chain_cancelled_before_it_starts_runs_nothing()
    {
        var ran = 0;
        var chain = Chain(("a", false), ("b", false));
        await chain.RunAsync((_, _, _) => { ran++; return Task.FromResult(StepResult.Success); }, new CancellationToken(true));

        Assert.Equal(0, ran);
        Assert.True(chain.Cancelled);
        Assert.Equal(["a", "b"], chain.Skipped);
    }

    [Fact]
    public async Task Steps_get_their_index_and_the_token()
    {
        using var cancel = new CancellationTokenSource();
        var seen = new List<(int, string, CancellationToken)>();
        var chain = Chain(("a", false), ("b", true));
        await chain.RunAsync((i, s, ct) => { seen.Add((i, s.Name, ct)); return Task.FromResult(StepResult.Success); }, cancel.Token);
        Assert.Equal([(0, "a", cancel.Token), (1, "b", cancel.Token)], seen);
    }

    [Fact]
    public async Task A_cancellation_the_chain_did_not_ask_for_is_a_failure()
    {
        var chain = Chain(("a", true), ("b", false));
        await chain.RunAsync((_, _, _) => throw new OperationCanceledException("timed out"), CancellationToken.None);
        Assert.False(chain.Cancelled);
        Assert.Equal("timed out", chain.StoppedAt?.Problem);
    }

    [Fact]
    public async Task A_failure_without_a_reason_says_failed()
    {
        var chain = Chain(("a", false));
        await chain.RunAsync((_, _, _) => Task.FromResult(new StepResult(false)), CancellationToken.None);
        Assert.Equal("failed", Assert.Single(chain.Failed).Problem);
    }

    [Fact]
    public async Task The_last_waited_script_failing_skips_nothing()
    {
        var chain = Chain(("a", false), ("b", true));
        await chain.RunAsync((i, _, _) => Task.FromResult(i == 1 ? StepResult.Failure("exit code 2") : StepResult.Success), CancellationToken.None);

        Assert.Empty(chain.Skipped);
        Assert.Equal("Started a; b failed", ScriptReport.Footnote(chain));
        Assert.Equal(("Grid lost: b failed", "Exit code 2."), ScriptReport.Problems(chain, "Grid lost"));
    }

    [Fact]
    public async Task A_failed_wait_also_reports_scripts_that_could_not_start()
    {
        var chain = Chain(("gone.bat", false), ("save.ps1", true), ("c", false));
        await chain.RunAsync((i, _, _) => Task.FromResult(i switch
        {
            0 => StepResult.Failure("file not found"),
            1 => StepResult.Failure("exit code 1"),
            _ => StepResult.Success,
        }), CancellationToken.None);

        Assert.Equal("Couldn't start gone.bat; save.ps1 failed, so c didn't run", ScriptReport.Footnote(chain));
        Assert.Equal(("Grid lost: save.ps1 failed", "Exit code 1, so c didn't run. Couldn't start gone.bat either."),
            ScriptReport.Problems(chain, "Grid lost"));
    }

    [Fact]
    public async Task Several_start_failures_are_listed_with_their_reasons()
    {
        var chain = Chain(("a", false), ("b", false));
        await chain.RunAsync((i, s, _) => Task.FromResult(StepResult.Failure(i == 0 ? "file not found" : "access denied")), CancellationToken.None);
        Assert.Equal(("Manual run: couldn't start a and b", "a: file not found. b: access denied."),
            ScriptReport.Problems(chain, "Manual run"));
    }

    [Fact]
    public void Stopped_scripts_lead_the_footnote()
    {
        Assert.Equal("Stopped the Grid lost scripts before hibernate.ps1",
            ScriptReport.Footnote(null, [(UpsEventKind.GridLost, ["hibernate.ps1"])]));
        Assert.Null(ScriptReport.Footnote(null, [(UpsEventKind.GridLost, [])]));
        Assert.Null(ScriptReport.Footnote(null, []));
    }

    [Fact]
    public async Task Stopped_scripts_then_a_wait_read_as_one_sentence()
    {
        var exit = new TaskCompletionSource<StepResult>();
        var chain = Chain(("save.ps1", true), ("wake.ahk", false));
        var run = chain.RunAsync((_, _, _) => exit.Task, CancellationToken.None);

        Assert.Equal("Stopped the UPS unreachable scripts before a and b; waiting for save.ps1, then wake.ahk",
            ScriptReport.Footnote(chain, [(UpsEventKind.UpsUnreachable, ["a", "b"])]));
        exit.SetResult(StepResult.Success);
        await run;
    }
}
