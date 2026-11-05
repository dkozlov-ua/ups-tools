using System.Diagnostics;
using UpsTools.AppTests.Support;
using UpsTools.Core;
using UpsTools.Services;

namespace UpsTools.AppTests;

/// <summary>Runs real scripts: PowerShell ones hidden, the rest through their file association.</summary>
public sealed class ScriptRunnerTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly AppLog _log;
    private readonly ScriptRunner _runner;

    public ScriptRunnerTests()
    {
        _log = new AppLog(_folder.Path);
        _runner = new ScriptRunner(_log, new ElevatedTasks(_log));
    }

    public void Dispose() => _folder.Dispose();

    /// <summary>A .ps1 that records its arguments and working folder in &lt;name&gt;.out, then exits with <paramref name="exitCode"/>.</summary>
    private string Ps1(string name, int exitCode = 0, int sleepMs = 0) => _folder.File(name, $$"""
        Start-Sleep -Milliseconds {{sleepMs}}
        Set-Content -LiteralPath (Join-Path $PSScriptRoot '{{name}}.out') -Value ((Get-Location).Path + '|' + ($args -join ','))
        exit {{exitCode}}
        """);

    private string Output(string name) => File.ReadAllText(Path.Combine(_folder.Path, name + ".out")).Trim();

    private ScriptAction Action(string path, bool wait = true, string arguments = "") =>
        new() { Path = path, WaitForExit = wait, Arguments = arguments };

    [Fact]
    public async Task A_waited_script_that_exits_0_succeeds()
    {
        var result = await _runner.RunAsync(Action(Ps1("ok.ps1"), arguments: "one \"two words\""), "Grid lost", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal($"{_folder.Path}|one,two words", Output("ok.ps1"));
        Assert.Equal(["Grid lost: started ok.ps1; waiting for it to finish", "Grid lost: ok.ps1 finished"],
            _log.Recent.Select(e => e.Message));
    }

    [Theory]
    [InlineData(3, "exit code 3")]
    [InlineData(-1, "exit code -1")]
    [InlineData(-1073741510, "exit code 0xC000013A")]
    [InlineData(ScriptRunner.CouldNotStartExitCode, "it couldn't be started")]
    public async Task A_waited_script_that_fails_says_how(int exitCode, string problem)
    {
        var result = await _runner.RunAsync(Action(Ps1("fail.ps1", exitCode)), "Grid lost", CancellationToken.None);

        Assert.Equal(StepResult.Failure(problem), result);
        Assert.Equal($"Grid lost: fail.ps1 failed: {problem}", _log.Recent[^1].Message);
        Assert.Equal(LogLevel.Warning, _log.Recent[^1].Level);
    }

    [Fact]
    public async Task A_script_not_waited_for_succeeds_once_started()
    {
        var watch = Stopwatch.StartNew();
        var result = await _runner.RunAsync(Action(Ps1("slow.ps1", exitCode: 1, sleepMs: 3000), wait: false), "Manual run", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2.5));
        Assert.Equal("Manual run: started slow.ps1", Assert.Single(_log.Recent).Message);
        await Wait.Until(() => File.Exists(Path.Combine(_folder.Path, "slow.ps1.out")), 20, "the script to run");
    }

    [Fact]
    public async Task Cancelling_stops_the_wait_not_the_script()
    {
        using var cancel = new CancellationTokenSource();
        var run = _runner.RunAsync(Action(Ps1("long.ps1", sleepMs: 2000)), "Grid lost", cancel.Token);
        await Wait.Until(() => _log.Recent.Count == 1);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await Wait.Until(() => File.Exists(Path.Combine(_folder.Path, "long.ps1.out")), 20, "the script to finish anyway");
    }

    [Fact]
    public async Task A_missing_script_fails_without_starting()
    {
        var path = Path.Combine(_folder.Path, "gone.bat");
        var result = await _runner.RunAsync(Action(path), "Grid lost", CancellationToken.None);

        Assert.Equal(StepResult.Failure("file not found"), result);
        Assert.Equal($"Grid lost: script not found: {path}", Assert.Single(_log.Recent).Message);
    }

    [Fact]
    public async Task A_file_that_cannot_be_started_fails()
    {
        var bogus = _folder.File("bogus.exe", "not a program");
        var result = await _runner.RunAsync(Action(bogus, wait: false), "Grid lost", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.StartsWith("couldn't start: ", result.Problem);
        Assert.StartsWith("Grid lost: failed to start bogus.exe: ", _log.Recent[^1].Message);
    }

    [Fact]
    public async Task Other_files_go_through_their_file_association()
    {
        var cmd = _folder.File("four.cmd", "@echo %~1>\"%~dp0four.cmd.out\"\r\n@exit /b 4\r\n");

        var result = await _runner.RunAsync(Action(cmd, arguments: "hello"), "Grid lost", CancellationToken.None);

        Assert.Equal(StepResult.Failure("exit code 4"), result);
        Assert.Equal("hello", Output("four.cmd"));
    }

    [Fact]
    public async Task A_script_set_to_run_as_administrator_needs_its_task()
    {
        var action = Action(Ps1("admin.ps1"));
        action.RunElevated = true;

        var result = await _runner.RunAsync(action, "Grid lost", CancellationToken.None);

        Assert.Equal(StepResult.Failure("its administrator task is missing"), result);
        Assert.Equal("Grid lost: didn't run admin.ps1: its administrator task is missing. Open Scripts and click Repair.",
            Assert.Single(_log.Recent).Message);
        Assert.False(File.Exists(Path.Combine(_folder.Path, "admin.ps1.out")));
    }

    // ---- UpsTools.exe --run-script, what an administrator task runs

    [Fact]
    public void Run_from_task_passes_the_arguments_unchanged_and_returns_the_exit_code()
    {
        var script = Ps1("task.ps1", exitCode: 7);
        var code = ScriptRunner.RunFromTask($"\"C:\\x\\UpsTools.exe\" --run-script \"{script}\" a \"b c\"");

        Assert.Equal(7, code);
        Assert.Equal($"{_folder.Path}|a,b c", Output("task.ps1"));
    }

    [Theory]
    [InlineData("UpsTools.exe")]
    [InlineData("UpsTools.exe --run-script C:\\no-quotes.ps1")]
    [InlineData("UpsTools.exe --run-script \"\"")]
    public void Run_from_task_without_a_script_could_not_start(string commandLine)
    {
        Assert.Equal(ScriptRunner.CouldNotStartExitCode, ScriptRunner.RunFromTask(commandLine));
    }

    [Fact]
    public void Run_from_task_with_a_file_that_cannot_start_says_so()
    {
        var bogus = _folder.File("bogus.exe", "not a program");
        Assert.Equal(ScriptRunner.CouldNotStartExitCode, ScriptRunner.RunFromTask($"x --run-script \"{bogus}\""));
    }
}
