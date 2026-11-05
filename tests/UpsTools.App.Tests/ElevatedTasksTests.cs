using System.Security.Principal;
using System.Xml.Linq;
using Microsoft.Win32;
using UpsTools.AppTests.Support;
using UpsTools.Core;
using UpsTools.Services;

namespace UpsTools.AppTests;

/// <summary>
/// Scripts that run as administrator: real scheduled tasks in the test profile's folder (\UpsTools-apptest-…),
/// started through the real UpsTools.exe. Registering them needs an elevated test run.
/// </summary>
[Collection("Registry")]
public sealed class ElevatedTasksTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly AppLog _log;
    private readonly ElevatedTasks _tasks;

    public ElevatedTasksTests()
    {
        _log = new AppLog(_folder.Path);
        _tasks = new ElevatedTasks(_log);
        TaskFolder.Delete();
    }

    public void Dispose()
    {
        TaskFolder.Delete();
        _folder.Dispose();
    }

    private ScriptAction Script(int exitCode = 0, string arguments = "")
    {
        var path = _folder.File($"admin-{Guid.NewGuid():N}.ps1", $$"""
            $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
            Set-Content -LiteralPath "$PSCommandPath.out" -Value (($args -join ',') + '|' + $principal.IsInRole('Administrators'))
            exit {{exitCode}}
            """);
        return new ScriptAction { Path = path, Arguments = arguments, RunElevated = true };
    }

    // ---- names and command lines

    [Fact]
    public void Tasks_are_named_after_the_script_id_in_the_profiles_folder()
    {
        var action = new ScriptAction { Id = "abc", Path = @"C:\s\x.ps1" };
        Assert.Equal("script-abc", ElevatedTasks.TaskName(action));
        Assert.Equal($@"\UpsTools-{TestProfile.Name}\script-abc", ElevatedTasks.TaskPath(action));
        Assert.Equal("--run-script \"C:\\s\\x.ps1\"", ElevatedTasks.TaskArguments(action));
        action.Arguments = "-Force \"a b\"";
        Assert.Equal("--run-script \"C:\\s\\x.ps1\" -Force \"a b\"", ElevatedTasks.TaskArguments(action));
    }

    [Theory]
    [InlineData(new[] { "--run-script", "x" }, true)]
    [InlineData(new[] { "--RUN-SCRIPT" }, true)]
    [InlineData(new[] { "--background", "--run-script" }, false)]
    [InlineData(new string[0], false)]
    public void Only_a_leading_run_script_runs_a_script(string[] args, bool expected)
    {
        Assert.Equal(expected, ElevatedTasks.IsRunScript(args));
    }

    [Theory]
    [InlineData("\"C:\\a\\UpsTools.exe\" --run-script \"C:\\s\\x.ps1\" -a \"b c\"", @"C:\s\x.ps1", "-a \"b c\"")]
    [InlineData("UpsTools.exe --RUN-SCRIPT \"C:\\s\\x.ps1\"", @"C:\s\x.ps1", "")]
    [InlineData("UpsTools.exe --run-script \"C:\\s\\x.ps1\"    spaced", @"C:\s\x.ps1", "spaced")]
    public void The_script_and_its_arguments_come_out_of_the_command_line_unchanged(string commandLine, string path, string arguments)
    {
        Assert.True(ElevatedTasks.TryParseRunScript(commandLine, out var p, out var a));
        Assert.Equal((path, arguments), (p, a));
    }

    [Theory]
    [InlineData("UpsTools.exe --run-script C:\\x.ps1")]
    [InlineData("UpsTools.exe --run-script \"C:\\x.ps1")]
    [InlineData("UpsTools.exe --run-script \"\"")]
    [InlineData("UpsTools.exe")]
    public void A_command_line_without_a_quoted_script_does_not_parse(string commandLine)
    {
        Assert.False(ElevatedTasks.TryParseRunScript(commandLine, out _, out _));
    }

    [Fact]
    public void The_raw_command_line_is_this_processes()
    {
        Assert.Contains("testhost", ElevatedTasks.RawCommandLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_task_runs_the_exe_elevated_as_this_user_also_on_battery()
    {
        var action = new ScriptAction { Path = @"C:\s\x.ps1", Arguments = "-q" };
        var xml = XDocument.Parse(ElevatedTasks.TaskXml(action));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string Value(string name) => xml.Descendants(ns + name).Single().Value;

        Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, Value("UserId"));
        Assert.Equal("InteractiveToken", Value("LogonType"));
        Assert.Equal("HighestAvailable", Value("RunLevel"));
        Assert.Equal("false", Value("DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value("StopIfGoingOnBatteries"));
        Assert.Equal("PT0S", Value("ExecutionTimeLimit"));
        Assert.Equal("Parallel", Value("MultipleInstancesPolicy"));
        Assert.Equal(TestProfile.AppExe, Value("Command"));
        Assert.Equal("--run-script \"C:\\s\\x.ps1\" -q", Value("Arguments"));
        Assert.Contains(@"C:\s\x.ps1", Value("Description"));
    }

    // ---- with Task Scheduler

    [Fact]
    public void Without_a_task_the_problem_is_that_it_is_missing()
    {
        Assert.Equal("its administrator task is missing", _tasks.Problem(Script()));
    }

    [Fact]
    public async Task Removing_a_script_without_a_task_needs_nothing()
    {
        Assert.Null(await _tasks.UnregisterAsync(Script()));
        Assert.Null(await _tasks.RemoveOwnTasksAsync());
        Assert.Empty(_log.Recent);
        Assert.Equal(0, ElevatedTasks.DeleteOwnTasks());
    }

    [AdminFact]
    public async Task A_registered_task_runs_the_script_elevated_and_reports_its_exit_code()
    {
        var action = Script(exitCode: 9, arguments: "x \"y z\"");

        Assert.Null(await _tasks.RegisterAsync(action));
        Assert.Null(_tasks.Problem(action));
        Assert.Equal([ElevatedTasks.TaskName(action)], TaskFolder.Tasks());
        Assert.Equal($"{Path.GetFileName(action.Path)} will run as administrator (task {ElevatedTasks.TaskPath(action)})",
            _log.Recent[^1].Message);

        var run = _tasks.Start(action);
        Assert.Equal(9, await run.WaitForExitAsync(CancellationToken.None));
        Assert.Equal("x,y z|True", File.ReadAllText(action.Path + ".out").Trim());
    }

    [AdminFact]
    public async Task The_runner_uses_the_task_for_a_script_set_to_run_as_administrator()
    {
        var runner = new ScriptRunner(_log, _tasks);
        var failing = Script(exitCode: 2);
        failing.WaitForExit = true;
        var quick = Script();
        Assert.Null(await _tasks.RegisterAsync(failing));
        Assert.Null(await _tasks.RegisterAsync(quick));

        Assert.Equal(StepResult.Failure("exit code 2"), await runner.RunAsync(failing, "Grid lost", CancellationToken.None));
        Assert.Contains(_log.Recent, e => e.Message.EndsWith(" as administrator; waiting for it to finish"));

        Assert.True((await runner.RunAsync(quick, "Grid lost", CancellationToken.None)).Ok);
        Assert.EndsWith(" as administrator", _log.Recent[^1].Message);
        await Wait.Until(() => File.Exists(quick.Path + ".out"), 20, "the task to run");
    }

    [AdminFact]
    public async Task Changed_arguments_make_the_task_out_of_date_until_it_is_registered_again()
    {
        var action = Script();
        Assert.Null(await _tasks.RegisterAsync(action));

        action.Arguments = "--new";
        Assert.Equal("its administrator task is out of date", _tasks.Problem(action));

        Assert.Null(await _tasks.RegisterAsync(action));
        Assert.Null(_tasks.Problem(action));
    }

    [AdminFact]
    public async Task A_task_of_another_exe_is_out_of_date()
    {
        var action = Script();
        Assert.Null(await _tasks.RegisterAsync(action));
        var exe = ElevatedTasks.Executable;
        try
        {
            ElevatedTasks.Executable = @"C:\Elsewhere\UpsTools.exe";
            Assert.Equal("its administrator task is out of date", _tasks.Problem(action));
        }
        finally
        {
            ElevatedTasks.Executable = exe;
        }
    }

    [AdminFact]
    public async Task Unregistering_deletes_the_task()
    {
        var action = Script();
        Assert.Null(await _tasks.RegisterAsync(action));

        Assert.Null(await _tasks.UnregisterAsync(action));

        Assert.Empty(TaskFolder.Tasks());
        Assert.Equal("its administrator task is missing", _tasks.Problem(action));
        Assert.StartsWith($"{Path.GetFileName(action.Path)} no longer runs as administrator", _log.Recent[^1].Message);
    }

    [AdminFact]
    public async Task Uninstalling_deletes_this_exes_tasks_and_their_folder()
    {
        Assert.Null(await _tasks.RegisterAsync(Script()));
        Assert.Null(await _tasks.RegisterAsync(Script()));
        Assert.Equal(2, ElevatedTasks.OwnTasks().Count);

        // Runs "UpsTools.exe --delete-admin-tasks --profile …" elevated.
        Assert.Null(await _tasks.RemoveOwnTasksAsync());

        Assert.False(TaskFolder.Exists());
        Assert.StartsWith("Deleted the administrator tasks (script-", _log.Recent[^1].Message);
    }

    [AdminFact]
    public async Task Uninstalling_keeps_the_tasks_of_another_copy()
    {
        var mine = Script();
        Assert.Null(await _tasks.RegisterAsync(mine));
        var exe = ElevatedTasks.Executable;
        var theirs = Script();
        try
        {
            ElevatedTasks.Executable = Environment.ProcessPath!; // another exe: testhost
            Assert.Null(await _tasks.RegisterAsync(theirs));
        }
        finally
        {
            ElevatedTasks.Executable = exe;
        }

        Assert.Equal([ElevatedTasks.TaskName(mine)], ElevatedTasks.OwnTasks());
        Assert.Equal(0, ElevatedTasks.DeleteOwnTasks());

        Assert.Equal([ElevatedTasks.TaskName(theirs)], TaskFolder.Tasks()); // and the folder stays for it
    }

    [AdminFact]
    public async Task The_uninstaller_cleans_up_everything_the_app_created()
    {
        Assert.Null(await _tasks.RegisterAsync(Script()));
        StartupRegistration.SetEnabled(true);
        using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Notifier.AppUserModelId}"))
            key.SetValue("DisplayName", "UPS Tools");
        Directory.CreateDirectory(Notifier.CacheFolder);
        File.WriteAllText(Path.Combine(Notifier.CacheFolder, "app.png"), "png");

        Assert.Equal(0, await Uninstaller.RunAsync());

        Assert.False(StartupRegistration.IsEnabled);
        Assert.Null(Registry.CurrentUser.OpenSubKey($@"Software\Classes\AppUserModelId\{Notifier.AppUserModelId}"));
        Assert.False(Directory.Exists(Notifier.CacheFolder));
        Assert.False(TaskFolder.Exists());
        var log = File.ReadAllText(new AppLog().FilePath);
        Assert.Contains("Uninstall cleanup finished", log);
    }
}
