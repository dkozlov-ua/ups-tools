using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace UpsTools.Services;

/// <summary>
/// Runs scripts as administrator without a UAC prompt when an event fires (nobody may be at the PC during a
/// power cut). Ticking "Run as administrator" registers a scheduled task, \UpsTools\script-{id}, with the
/// highest privileges, which asks for administrator rights once. Running the script starts that task on demand.
/// The task runs <c>UpsTools.exe --run-script "path" arguments</c>, which starts the script the same way as
/// without elevation and waits for it, so the task's result is the script's exit code.
/// </summary>
/// <remarks>
/// Task Scheduler is used through its COM API (late-bound) on the UI thread. Registering a task with the
/// highest privileges needs administrator rights, so that runs elevated schtasks.exe; starting and reading
/// the task doesn't.
/// </remarks>
public sealed class ElevatedTasks(AppLog log)
{
    /// <summary>\UpsTools, or \UpsTools-name for a profile (<see cref="AppProfile"/>).</summary>
    public static string FolderPath { get; } = @"\UpsTools" + AppProfile.Suffix;
    public const string RunScriptArgument = "--run-script";

    private const int TaskRunLevelHighest = 1;
    private const int TaskEnumHidden = 1;
    private const int ErrorCancelled = 1223; // the user said No to the UAC prompt
    private const int SchedTaskRunning = 0x41301;
    private const int SchedTaskHasNotRun = 0x41303;

    public static string TaskName(ScriptAction action) => $"script-{action.Id}";
    public static string TaskPath(ScriptAction action) => $@"{FolderPath}\{TaskName(action)}";

    /// <summary>What the tasks start: this exe, unless a test points it at UpsTools.exe.</summary>
    internal static string Executable
    {
        get => field ?? Environment.ProcessPath ?? throw new InvalidOperationException("Unknown exe path");
        set;
    }

    public static string TaskArguments(ScriptAction action) =>
        $"{RunScriptArgument} \"{action.Path}\"" + (action.Arguments.Length > 0 ? " " + action.Arguments : "");

    public static bool IsRunScript(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals(RunScriptArgument, StringComparison.OrdinalIgnoreCase);

    /// <summary>This process's command line exactly as it was passed (<see cref="Environment.GetCommandLineArgs"/> re-splits it).</summary>
    public static string RawCommandLine => Marshal.PtrToStringUni(GetCommandLineW()) ?? "";

    /// <summary>Takes the script path and its arguments, unchanged, out of a <c>--run-script</c> command line.</summary>
    public static bool TryParseRunScript(string commandLine, out string path, out string arguments)
    {
        path = arguments = "";
        var marker = RunScriptArgument + " \"";
        var at = commandLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;
        var start = at + marker.Length;
        var end = commandLine.IndexOf('"', start); // a path can't contain quotes
        if (end <= start) return false;
        path = commandLine[start..end];
        arguments = commandLine[(end + 1)..].TrimStart();
        return true;
    }

    /// <summary>Why the script's task can't be used, or null when it is registered as expected.</summary>
    public string? Problem(ScriptAction action)
    {
        try
        {
            var task = Find(action);
            if (task is null) return "its administrator task is missing";

            var definition = task.Definition;
            if ((int)definition.Principal.RunLevel != TaskRunLevelHighest || (int)definition.Actions.Count != 1)
                return "its administrator task is out of date";
            var exec = definition.Actions[1];
            if (!string.Equals((string)exec.Path, Executable, StringComparison.OrdinalIgnoreCase) ||
                (string)exec.Arguments != TaskArguments(action))
                return "its administrator task is out of date";
            return null;
        }
        catch (Exception ex)
        {
            return $"its administrator task can't be read ({ex.Message})";
        }
    }

    /// <summary>Registers (or replaces) the script's task; shows a UAC prompt.</summary>
    /// <returns>Null on success, otherwise what went wrong.</returns>
    public async Task<string?> RegisterAsync(ScriptAction action)
    {
        var name = Path.GetFileName(action.Path);
        var file = Path.Combine(Path.GetTempPath(), $"UpsTools-{TaskName(action)}.xml");
        try
        {
            File.WriteAllText(file, TaskXml(action), Encoding.Unicode);
            var code = await RunElevatedAsync(Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                $"/Create /TN \"{TaskPath(action)}\" /XML \"{file}\" /F");
            var problem = code switch
            {
                null => "the administrator prompt was cancelled",
                0 => Problem(action),
                _ => $"schtasks.exe failed with exit code {code}",
            };
            if (problem is null) log.Info($"{name} will run as administrator (task {TaskPath(action)})");
            else log.Warn($"Couldn't set up {name} to run as administrator: {problem}");
            return problem;
        }
        catch (Exception ex)
        {
            log.Warn($"Couldn't set up {name} to run as administrator: {ex.Message}");
            return ex.Message;
        }
        finally
        {
            try { File.Delete(file); } catch { /* temp file */ }
        }
    }

    /// <summary>Deletes the script's task, if there is one; may show a UAC prompt.</summary>
    /// <returns>Null on success, otherwise what went wrong.</returns>
    public async Task<string?> UnregisterAsync(ScriptAction action)
    {
        var name = Path.GetFileName(action.Path);
        try
        {
            if (Find(action) is null) return null;
            try
            {
                Connect().GetFolder(FolderPath).DeleteTask(TaskName(action), 0);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070005))
            {
                var code = await RunElevatedAsync(Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                    $"/Delete /TN \"{TaskPath(action)}\" /F");
                if (code is null) return "the administrator prompt was cancelled";
                if (code != 0) return $"schtasks.exe failed with exit code {code}";
            }

            log.Info($"{name} no longer runs as administrator (deleted task {TaskPath(action)})");
            return null;
        }
        catch (Exception ex)
        {
            log.Warn($"Couldn't delete the administrator task of {name}: {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>
    /// Deletes the tasks that start this exe, for the uninstaller; asks for administrator rights once, if there
    /// are any. Tasks of another copy of UPS Tools (e.g. a development build) stay.
    /// </summary>
    /// <returns>Null on success, otherwise what went wrong.</returns>
    public async Task<string?> RemoveOwnTasksAsync()
    {
        try
        {
            var names = OwnTasks();
            if (names.Count == 0) return null;
            var code = await RunElevatedAsync(Executable, Uninstaller.DeleteTasksArgument + AppProfile.CommandLine);
            var problem = code switch
            {
                null => "the administrator prompt was cancelled",
                0 => OwnTasks().Count == 0 ? null : "some are still there",
                _ => $"deleting them failed (exit code {code})",
            };
            if (problem is null) log.Info($"Deleted the administrator tasks ({string.Join(", ", names)})");
            else log.Warn($"Couldn't delete the administrator tasks: {problem}");
            return problem;
        }
        catch (Exception ex)
        {
            log.Warn($"Couldn't delete the administrator tasks: {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>
    /// <c>UpsTools.exe --delete-admin-tasks</c>, run elevated by <see cref="RemoveOwnTasksAsync"/>: deletes the
    /// tasks that start this exe, and the \UpsTools folder once it is empty.
    /// </summary>
    /// <returns>The process exit code.</returns>
    public static int DeleteOwnTasks()
    {
        try
        {
            var service = Connect();
            var folder = service.GetFolder(FolderPath);
            foreach (var name in OwnTasks()) folder.DeleteTask(name, 0);
            if ((int)folder.GetTasks(TaskEnumHidden).Count == 0 && (int)folder.GetFolders(0).Count == 0)
                service.GetFolder(@"\").DeleteFolder(FolderPath.TrimStart('\\'), 0);
            return 0;
        }
        catch (Exception ex) when (ex.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003))
        {
            return 0; // no folder: nothing to delete
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>Names of the tasks in \UpsTools that start this exe.</summary>
    internal static List<string> OwnTasks()
    {
        var names = new List<string>();
        dynamic tasks;
        try
        {
            tasks = Connect().GetFolder(FolderPath).GetTasks(TaskEnumHidden);
        }
        catch (Exception ex) when (ex.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003))
        {
            return names;
        }
        int count = tasks.Count;
        for (var i = 1; i <= count; i++)
        {
            var task = tasks[i];
            var actions = task.Definition.Actions;
            int actionCount = actions.Count;
            for (var a = 1; a <= actionCount; a++)
                if (string.Equals((string)actions[a].Path, Executable, StringComparison.OrdinalIgnoreCase))
                {
                    names.Add((string)task.Name);
                    break;
                }
        }
        return names;
    }

    /// <summary>Starts the script's task. Check <see cref="Problem"/> first.</summary>
    public Run Start(ScriptAction action)
    {
        var task = Find(action) ?? throw new InvalidOperationException("its administrator task is missing");
        var running = task.Run(null);
        return new Run(task, (string)running.InstanceGuid);
    }

    /// <summary>One run of a task.</summary>
    public sealed class Run(dynamic task, string instance)
    {
        /// <returns>The task's result: the script's exit code, or a Task Scheduler error.</returns>
        public async Task<int> WaitForExitAsync(CancellationToken cancel)
        {
            while (IsRunning()) await Task.Delay(500, cancel);

            // The result can lag behind the instance disappearing.
            for (var i = 0; ; i++)
            {
                int result = task.LastTaskResult;
                if (result is not (SchedTaskRunning or SchedTaskHasNotRun) || i >= 10) return result;
                await Task.Delay(200, cancel);
            }
        }

        private bool IsRunning()
        {
            var instances = task.GetInstances(0);
            int count = instances.Count;
            for (var i = 1; i <= count; i++)
                if ((string)instances[i].InstanceGuid == instance) return true;
            return false;
        }
    }

    private static dynamic? Find(ScriptAction action)
    {
        try
        {
            return Connect().GetFolder(FolderPath).GetTask(TaskName(action));
        }
        catch (Exception ex) when (ex.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003))
        {
            return null; // no such task or folder (surfaces as FileNotFoundException / DirectoryNotFoundException)
        }
    }

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is not available");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    internal static string TaskXml(ScriptAction action)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, params object[] content) => new(ns + name, content);

        var task = E("Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo",
                E("Author", "UPS Tools"),
                E("Description", $"Runs {action.Path} as administrator when UPS Tools asks. Unticking " +
                                 "\"Run as administrator\" or removing the script in UPS Tools deletes this task.")),
            E("Principals",
                E("Principal", new XAttribute("id", "Author"),
                    E("UserId", WindowsIdentity.GetCurrent().User!.Value),
                    E("LogonType", "InteractiveToken"),
                    E("RunLevel", "HighestAvailable"))),
            E("Settings",
                E("MultipleInstancesPolicy", "Parallel"),
                // The defaults would refuse to start, or stop, the script on a laptop running on battery.
                E("DisallowStartIfOnBatteries", "false"),
                E("StopIfGoingOnBatteries", "false"),
                E("AllowHardTerminate", "true"),
                E("StartWhenAvailable", "false"),
                E("AllowStartOnDemand", "true"),
                E("Enabled", "true"),
                E("ExecutionTimeLimit", "PT0S"),
                E("Priority", "5")),
            E("Actions", new XAttribute("Context", "Author"),
                E("Exec",
                    E("Command", Executable),
                    E("Arguments", TaskArguments(action)))));

        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" + task;
    }

    /// <returns>The exit code, or null when the UAC prompt was declined.</returns>
    private static async Task<int?> RunElevatedAsync(string file, string arguments)
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            // Returns only once the UAC prompt is answered: keep the window responsive meanwhile.
            using var process = await Task.Run(() => Process.Start(psi))
                                ?? throw new InvalidOperationException($"{Path.GetFileName(file)} didn't start");
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetCommandLineW();
}
