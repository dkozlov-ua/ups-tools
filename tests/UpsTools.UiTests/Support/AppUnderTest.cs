using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Microsoft.Win32;

[assembly: CollectionBehavior(DisableTestParallelization = true)] // one app on the desktop at a time

namespace UpsTools.UiTests.Support;

/// <summary>
/// UpsTools.exe started as its own profile (its own settings, single-instance signals, registry entries),
/// with a simulated UPS whose readings are a JSON file the test edits. Everything is removed afterwards.
/// </summary>
internal sealed class AppUnderTest : IDisposable
{
    private static int _count;

    public static string Exe { get; } = Path.Combine(AppContext.BaseDirectory, "UpsTools.exe");

    public string Profile { get; } = $"e2e-{Environment.ProcessId}-{Interlocked.Increment(ref _count)}";
    public string Work { get; }
    public string SimulatorFile => Path.Combine(Work, "ups.json");
    public string SettingsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UpsTools-" + Profile);
    public string SettingsFile => Path.Combine(SettingsFolder, "settings.json");
    public string LogFile => Path.Combine(SettingsFolder, "ups-tools.log");
    public string CacheFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpsTools-" + Profile);
    public string RunValueName => "UpsTools-" + Profile;
    public string AppUserModelId => "DKozlov.UpsTools-" + Profile;
    public string TaskFolder => @"\UpsTools-" + Profile;

    public static bool IsAdministrator { get; } =
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    /// <summary>The names of the administrator tasks in the profile's Task Scheduler folder.</summary>
    public IReadOnlyList<string> Tasks()
    {
        try
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            service.Connect();
            var names = new List<string>();
            foreach (var task in service.GetFolder(TaskFolder).GetTasks(1)) names.Add((string)task.Name);
            return names;
        }
        catch
        {
            return [];
        }
    }

    private void DeleteTaskFolder()
    {
        try
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            service.Connect();
            var folder = service.GetFolder(TaskFolder);
            foreach (var name in Tasks()) folder.DeleteTask(name, 0);
            service.GetFolder(@"\").DeleteFolder(TaskFolder.TrimStart('\\'), 0);
        }
        catch
        {
            // none
        }
    }

    public Process? Process { get; private set; }
    public int Pid => Process!.Id;

    public AppUnderTest()
    {
        Work = Path.Combine(Path.GetTempPath(), "UpsTools.UiTests", Profile);
        Directory.CreateDirectory(Work);
        Simulate(new { GridPresent = true, BatteryPercent = 80, AcInputVoltage = 230.4, AcOutputVoltage = 229.8, AcInputW = 150, AcOutputW = 120 });
    }

    /// <summary>Writes settings.json for the profile: paired with the simulator, polling every 2 s, notifications off.</summary>
    public void Seed(Action<JsonObject>? edit = null, bool paired = true)
    {
        var settings = new JsonObject
        {
            ["Ups"] = paired ? new JsonObject
            {
                ["DriverId"] = "simulated", ["Address"] = "00:00:00:00:00:01", ["Name"] = "SIMULATOR0001", ["Model"] = "Simulator",
            } : null,
            ["CloseToTray"] = true,
            ["UpdatePeriodSeconds"] = 2,
            ["Scripts"] = new JsonObject(),
            ["Notifications"] = new JsonObject
            {
                ["GridLost"] = false, ["BatteryLow"] = false, ["GridRestored"] = false, ["UpsUnreachable"] = false, ["UpsReachable"] = false,
            },
        };
        edit?.Invoke(settings);
        Directory.CreateDirectory(SettingsFolder);
        File.WriteAllText(SettingsFile, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>What the simulated UPS reports from its next poll on.</summary>
    public void Simulate(object reading)
    {
        var temp = SimulatorFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(reading));
        File.Move(temp, SimulatorFile, overwrite: true);
    }

    /// <summary>A PowerShell script that appends a line to &lt;name&gt;.out each time it runs.</summary>
    public string Script(string name, int exitCode = 0) => WriteFile(name, $$"""
        Add-Content -LiteralPath (Join-Path $PSScriptRoot '{{name}}.out') -Value ('ran ' + ($args -join ','))
        exit {{exitCode}}
        """);

    public string WriteFile(string name, string content)
    {
        var path = Path.Combine(Work, name);
        File.WriteAllText(path, content);
        return path;
    }

    public int Runs(string script)
    {
        var file = Path.Combine(Work, script + ".out");
        return File.Exists(file) ? File.ReadAllLines(file).Length : 0;
    }

    public JsonNode Settings() => Ui.WaitFor(() =>
    {
        try { return JsonNode.Parse(File.ReadAllText(SettingsFile)); }
        catch (IOException) { return null; } // being replaced
    }, "settings.json")!;

    public string Log() => File.Exists(LogFile) ? ReadShared(LogFile) : "";

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    public ProcessStartInfo StartInfo(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["UPSTOOLS_PROFILE"] = Profile;
        psi.Environment["UPSTOOLS_SIMULATOR"] = SimulatorFile;
        return psi;
    }

    /// <summary>Starts the app and waits for its main window (unless it starts in the background).</summary>
    public AutomationElement? Start(params string[] args)
    {
        Process = Process.Start(StartInfo(args))!;
        if (args.Contains("--background"))
        {
            Ui.Until(() => Log().Contains("UPS Tools started"), "the app to start");
            return null;
        }
        return MainWindow();
    }

    /// <summary>Runs UpsTools.exe with these arguments to the end, e.g. --exit; returns its exit code.</summary>
    public int Run(params string[] args)
    {
        using var process = Process.Start(StartInfo(args))!;
        if (!process.WaitForExit(TimeSpan.FromSeconds(45))) throw new TimeoutException($"UpsTools.exe {string.Join(' ', args)} didn't end");
        return process.ExitCode;
    }

    public AutomationElement MainWindow(TimeSpan? timeout = null) => Ui.WindowOf(Pid, "UPS Tools", timeout);

    public AutomationElement? TryMainWindow() =>
        AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, Pid),
            new PropertyCondition(AutomationElement.NameProperty, "UPS Tools")));

    /// <summary>Opens a tab of the main window and returns the window.</summary>
    public AutomationElement Tab(string name)
    {
        var window = MainWindow();
        var tab = window.ByName(name);
        tab.Select();
        Ui.Until(() => ((SelectionItemPattern)tab.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected, $"tab {name}");
        return window;
    }

    public bool HasExited => Process?.HasExited ?? true;

    public void Dispose()
    {
        try
        {
            if (Process is { HasExited: false })
            {
                Run("--exit");
                if (!Process.WaitForExit(TimeSpan.FromSeconds(10))) Process.Kill();
            }
        }
        catch
        {
            try { Process?.Kill(); } catch { /* gone */ }
        }
        Process?.Dispose();

        TryDelete(SettingsFolder);
        TryDelete(CacheFolder);
        TryDelete(Work);
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{AppUserModelId}", false); } catch { }
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch { }
        try { Windows.UI.Notifications.ToastNotificationManager.History.Clear(AppUserModelId); } catch { }
        DeleteTaskFolder();
    }

    private static void TryDelete(string folder)
    {
        for (var i = 0; i < 10; i++)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                return;
            }
            catch
            {
                Thread.Sleep(200); // a script may still be closing a file
            }
        }
    }
}

/// <summary>Needs an elevated test run: it registers administrator tasks, which would otherwise ask for rights.</summary>
internal sealed class AdminFactAttribute : FactAttribute
{
    public AdminFactAttribute()
    {
        if (!AppUnderTest.IsAdministrator) Skip = "Registers administrator tasks: run the tests elevated";
    }
}
