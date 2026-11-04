using System.Diagnostics;
using System.IO;

namespace UpsTools.Services;

/// <summary>Starts user scripts. Scripts run detached; the app does not wait for them.</summary>
public sealed class ScriptRunner(AppLog log)
{
    /// <returns>Whether the script was started.</returns>
    public bool Run(ScriptAction action, string reason)
    {
        var path = action.Path;
        if (!File.Exists(path))
        {
            log.Warn($"{reason}: script not found: {path}");
            return false;
        }

        try
        {
            var psi = Path.GetExtension(path).Equals(".ps1", StringComparison.OrdinalIgnoreCase)
                // Opening a .ps1 via its file association would just open it in an editor.
                ? new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\" {action.Arguments}".TrimEnd())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
                // Everything else (.ahk, .bat, .cmd, .exe, …) goes through its Windows file association.
                : new ProcessStartInfo(path, action.Arguments) { UseShellExecute = true };

            psi.WorkingDirectory = Path.GetDirectoryName(path) ?? "";
            using var process = Process.Start(psi);
            log.Info($"{reason}: started {Path.GetFileName(path)}");
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"{reason}: failed to start {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }
}
