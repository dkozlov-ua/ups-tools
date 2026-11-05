using System.Diagnostics;
using System.IO;
using UpsTools.Core;

namespace UpsTools.Services;

/// <summary>
/// Starts user scripts, and waits for the ones marked <see cref="ScriptAction.WaitForExit"/>. Scripts marked
/// <see cref="ScriptAction.RunElevated"/> run through their administrator task (<see cref="ElevatedTasks"/>).
/// </summary>
public sealed class ScriptRunner(AppLog log, ElevatedTasks elevated)
{
    /// <summary>
    /// Exit code of <c>UpsTools.exe --run-script</c> when it couldn't start the script. In the HRESULT customer
    /// range, so a script can't return it by accident.
    /// </summary>
    public const int CouldNotStartExitCode = unchecked((int)0xA0055001);

    /// <summary>
    /// Starts the script. With <see cref="ScriptAction.WaitForExit"/>, returns once it has exited; cancelling
    /// stops the waiting (with <see cref="OperationCanceledException"/>), not the script.
    /// </summary>
    public async Task<StepResult> RunAsync(ScriptAction action, string reason, CancellationToken cancel)
    {
        var name = Path.GetFileName(action.Path);
        var how = action.RunElevated ? " as administrator" : "";
        if (!File.Exists(action.Path))
        {
            log.Warn($"{reason}: script not found: {action.Path}");
            return StepResult.Failure("file not found");
        }

        if (action.RunElevated)
        {
            if (elevated.Problem(action) is { } problem)
            {
                log.Warn($"{reason}: didn't run {name}: {problem}. Open Scripts and click Repair.");
                return StepResult.Failure(problem);
            }

            ElevatedTasks.Run run;
            try
            {
                run = elevated.Start(action);
            }
            catch (Exception ex)
            {
                log.Warn($"{reason}: failed to start {name}{how}: {ex.Message}");
                return StepResult.Failure($"couldn't start: {ex.Message}");
            }

            if (!action.WaitForExit)
            {
                log.Info($"{reason}: started {name}{how}");
                return StepResult.Success;
            }

            log.Info($"{reason}: started {name}{how}; waiting for it to finish");
            return Finished(reason, name, await run.WaitForExitAsync(cancel));
        }

        Process? process;
        try
        {
            process = Start(action.Path, action.Arguments);
        }
        catch (Exception ex)
        {
            log.Warn($"{reason}: failed to start {name}: {ex.Message}");
            return StepResult.Failure($"couldn't start: {ex.Message}");
        }

        using (process)
        {
            if (!action.WaitForExit)
            {
                log.Info($"{reason}: started {name}");
                return StepResult.Success;
            }

            if (process is null)
            {
                // A file association can hand the file to an app that is already running.
                log.Warn($"{reason}: started {name}, but Windows gave no process to wait for");
                return StepResult.Failure("Windows gave no process to wait for");
            }

            log.Info($"{reason}: started {name}; waiting for it to finish");
            await process.WaitForExitAsync(cancel);
            return Finished(reason, name, process.ExitCode);
        }
    }

    private StepResult Finished(string reason, string name, int exitCode)
    {
        if (exitCode == 0)
        {
            log.Info($"{reason}: {name} finished");
            return StepResult.Success;
        }

        var problem = Describe(exitCode);
        log.Warn($"{reason}: {name} failed: {problem}");
        return StepResult.Failure(problem);
    }

    private static string Describe(int exitCode) => exitCode switch
    {
        CouldNotStartExitCode => "it couldn't be started",
        >= -0xFFFF and <= 0xFFFF => $"exit code {exitCode}",
        _ => $"exit code 0x{exitCode:X8}", // HRESULTs, e.g. from Task Scheduler
    };

    /// <summary>Starts a script the way UPS Tools runs it, with or without administrator rights.</summary>
    /// <returns>The process, or null when a file association handed the file to an existing process.</returns>
    public static Process? Start(string path, string arguments)
    {
        var psi = Path.GetExtension(path).Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            // Opening a .ps1 via its file association would just open it in an editor.
            ? new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\" {arguments}".TrimEnd())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }
            // Everything else (.ahk, .bat, .cmd, .exe, …) goes through its Windows file association.
            : new ProcessStartInfo(path, arguments) { UseShellExecute = true };

        psi.WorkingDirectory = Path.GetDirectoryName(path) ?? "";
        return Process.Start(psi);
    }

    /// <summary>
    /// <c>UpsTools.exe --run-script "path" arguments</c>: what an administrator task runs. Starts the script,
    /// waits for it and returns its exit code, so the task's result is the script's.
    /// </summary>
    /// <param name="commandLine">The raw command line: the arguments after the path are passed on unchanged.</param>
    public static int RunFromTask(string commandLine)
    {
        if (!ElevatedTasks.TryParseRunScript(commandLine, out var path, out var arguments)) return CouldNotStartExitCode;
        try
        {
            using var process = Start(path, arguments);
            if (process is null) return 0;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch
        {
            return CouldNotStartExitCode;
        }
    }
}
