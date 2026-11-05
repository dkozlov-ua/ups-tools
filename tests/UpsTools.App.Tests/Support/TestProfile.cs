using System.Runtime.CompilerServices;
using System.Security.Principal;
using Microsoft.Win32;
using UpsTools.Services;

namespace UpsTools.AppTests.Support;

/// <summary>
/// Runs the tests as a separate profile of UPS Tools (<see cref="AppProfile"/>), so what they register — the
/// Start with Windows entry, the notification registration, the administrator tasks — never touches the
/// installed copy, and removes it all when the tests end.
/// </summary>
internal static class TestProfile
{
    public static readonly string Name = $"apptest-{Environment.ProcessId}";

    /// <summary>UpsTools.exe, built next to the tests: what administrator tasks start.</summary>
    public static string AppExe { get; } = Path.Combine(AppContext.BaseDirectory, "UpsTools.exe");

    public static bool IsAdministrator { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable(AppProfile.EnvironmentVariable, Name);
        Environment.SetEnvironmentVariable("UPSTOOLS_SIMULATOR", null);
        ElevatedTasks.Executable = AppExe;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanUp();
    }

    public static void CleanUp()
    {
        if (AppProfile.Name != Name) return; // never the installed copy's
        TryDelete(SettingsStore.DefaultFolder);
        TryDelete(Notifier.CacheFolder);
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{Notifier.AppUserModelId}", false); } catch { }
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.DeleteValue(StartupRegistration.ValueName, throwOnMissingValue: false);
        }
        catch { }
        try { TaskFolder.Delete(); } catch { }
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch { }
    }
}

/// <summary>A fresh temporary folder, deleted afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UpsTools.App.Tests", Guid.NewGuid().ToString("N")[..8]);

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name, string? content = null)
    {
        var file = System.IO.Path.Combine(Path, name);
        if (content is not null) System.IO.File.WriteAllText(file, content);
        return file;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* a script may still hold it */ }
    }
}

/// <summary>The profile's Task Scheduler folder, through the same COM API the app uses.</summary>
internal static class TaskFolder
{
    private static dynamic Service()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();
        return service;
    }

    public static bool Exists()
    {
        try
        {
            Service().GetFolder(ElevatedTasks.FolderPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<string> Tasks()
    {
        try
        {
            var tasks = Service().GetFolder(ElevatedTasks.FolderPath).GetTasks(1);
            var names = new List<string>();
            foreach (var task in tasks) names.Add((string)task.Name);
            return names;
        }
        catch
        {
            return [];
        }
    }

    public static void Delete()
    {
        if (!Exists()) return;
        var service = Service();
        var folder = service.GetFolder(ElevatedTasks.FolderPath);
        foreach (var name in Tasks()) folder.DeleteTask(name, 0);
        service.GetFolder(@"\").DeleteFolder(ElevatedTasks.FolderPath.TrimStart('\\'), 0);
    }
}

/// <summary>Needs an elevated test run: it registers administrator tasks.</summary>
internal sealed class AdminFactAttribute : FactAttribute
{
    public AdminFactAttribute()
    {
        if (!TestProfile.IsAdministrator) Skip = "Registers administrator tasks: run the tests elevated";
    }
}

internal static class Wait
{
    /// <summary>Waits until <paramref name="condition"/> holds; fails the test after <paramref name="seconds"/>.</summary>
    public static async Task Until(Func<bool> condition, double seconds = 10, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what ?? "the condition"}");
            await Task.Delay(10);
        }
    }
}
