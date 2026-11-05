using System.IO;
using System.Windows;

namespace UpsTools.Services;

/// <summary>
/// <c>UpsTools.exe --uninstall</c>, run by the uninstaller (installer\UpsTools.iss) before it deletes the
/// installed files: exits a running copy and removes what the app itself created outside its folder.
/// </summary>
/// <remarks>
/// The settings and the log in %APPDATA%\UpsTools are the user's and stay.
/// </remarks>
internal static class Uninstaller
{
    public const string Argument = "--uninstall";
    public const string DeleteTasksArgument = "--delete-admin-tasks";

    /// <returns>The process exit code: 0 when everything was removed.</returns>
    public static async Task<int> RunAsync()
    {
        var log = new AppLog();
        log.Info($"Uninstalling UPS Tools from {Path.GetDirectoryName(Environment.ProcessPath)}");
        var failed = false;

        if (!App.StopRunningCopy())
        {
            log.Warn("UPS Tools is still running after 30 s");
            failed = true;
        }

        Try(log, "the Start with Windows entry", () =>
        {
            if (StartupRegistration.RemoveIfThisExe()) log.Info("Removed the Start with Windows entry");
        }, ref failed);
        Try(log, "the notification registration", () => Notifier.Unregister(log), ref failed);

        var problem = await new ElevatedTasks(log).RemoveOwnTasksAsync();
        if (problem is not null)
        {
            failed = true;
            MessageBox.Show(
                $"UPS Tools couldn't delete the scheduled tasks that run your scripts as administrator: {problem}.\n\n" +
                $@"They are in Task Scheduler, in the {ElevatedTasks.FolderPath} folder; you can delete them there.",
                "Uninstall UPS Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        log.Info(failed ? "Uninstall cleanup finished with problems" : "Uninstall cleanup finished");
        return failed ? 1 : 0;
    }

    private static void Try(AppLog log, string what, Action action, ref bool failed)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            log.Warn($"Couldn't remove {what}: {ex.Message}");
            failed = true;
        }
    }
}
