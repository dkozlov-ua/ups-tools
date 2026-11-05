using Microsoft.Win32;

namespace UpsTools.Services;

/// <summary>"Start with Windows" via the per-user Run key (no admin rights needed).</summary>
public static class StartupRegistration
{
    public const string BackgroundArgument = "--background";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UpsTools";

    private static string Command => $"\"{Environment.ProcessPath}\" {BackgroundArgument}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Deletes the entry when it starts this exe (not another copy's).</summary>
    /// <returns>Whether there was one to delete.</returns>
    public static bool RemoveIfThisExe()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not string current ||
            !current.Contains($"\"{Environment.ProcessPath}\"", StringComparison.OrdinalIgnoreCase)) return false;
        key.DeleteValue(ValueName, throwOnMissingValue: false);
        return true;
    }

    /// <summary>Point an existing entry at the current exe, e.g. after the user replaced it with a new release elsewhere.</summary>
    public static void RefreshPathIfEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string current && current != Command) key.SetValue(ValueName, Command);
    }
}
