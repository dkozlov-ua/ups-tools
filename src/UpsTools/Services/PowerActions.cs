using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using UpsTools.Core;

namespace UpsTools.Services;

/// <summary>Puts the PC to sleep, hibernates it or shuts it down.</summary>
public interface IPowerControl
{
    /// <summary>Returns once it's done: for sleep and hibernation, when the PC is running again.</summary>
    /// <exception cref="Exception">Windows refused, e.g. hibernation is turned off.</exception>
    void Do(PowerAction action);
}

/// <summary>The real thing.</summary>
public sealed class WindowsPower : IPowerControl
{
    public void Do(PowerAction action)
    {
        switch (action)
        {
            case PowerAction.Sleep:
            case PowerAction.Hibernate:
                if (!SetSuspendState(action == PowerAction.Hibernate, force: false, disableWakeEvent: false))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                break;

            case PowerAction.ShutDown:
                // /f: on battery, an app asking to save its work would otherwise hold the shutdown until the battery is empty.
                using (var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/s /f /t 0")
                       {
                           UseShellExecute = false,
                           CreateNoWindow = true,
                       })!)
                {
                    process.WaitForExit();
                    if (process.ExitCode != 0) throw new InvalidOperationException($"shutdown.exe failed with exit code {process.ExitCode}");
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);
}

/// <summary>With the simulated UPS (<see cref="SimulatedUpsDriver"/>), the PC stays on: what would happen is logged.</summary>
public sealed class SimulatedPower(AppLog log) : IPowerControl
{
    public void Do(PowerAction action) => log.Info($"Simulated UPS: {action.Doing()} skipped");
}

/// <summary>
/// Counts down before a power action, where the user can cancel it or do it at once.
/// </summary>
/// <returns>True to go ahead, false when the user cancelled.</returns>
/// <exception cref="OperationCanceledException">The token was cancelled: the countdown is gone.</exception>
public delegate Task<bool> PowerCountdown(PowerAction action, TimeSpan delay, string reason, CancellationToken cancel);

/// <summary>Runs an event's built-in actions (<see cref="ScriptAction.Power"/>): a countdown, then the action.</summary>
public sealed class PowerRunner(AppLog log, IPowerControl power, PowerCountdown countdown)
{
    public const int DefaultCountdownSeconds = 60;
    public const int MaxCountdownSeconds = 600;

    /// <summary>Once the countdown ran out, the action can't be stopped any more; for sleep, returns once the PC woke up.</summary>
    public async Task<StepResult> RunAsync(ScriptAction action, string reason, CancellationToken cancel)
    {
        var what = action.Power ?? throw new ArgumentException("Not a built-in action", nameof(action));
        var seconds = Math.Clamp(action.CountdownSeconds ?? DefaultCountdownSeconds, 0, MaxCountdownSeconds);
        if (seconds > 0)
        {
            log.Info($"{reason}: {what.Doing()} in {seconds} s unless cancelled");
            if (!await countdown(what, TimeSpan.FromSeconds(seconds), reason, cancel))
            {
                log.Warn($"{reason}: {what.Title()} was cancelled");
                return StepResult.CancelledByUser;
            }
        }

        log.Info($"{reason}: {what.Doing()}");
        try
        {
            // Not on the UI thread: going to sleep only returns once the PC is back.
            await Task.Run(() => power.Do(what), CancellationToken.None);
            return StepResult.Success;
        }
        catch (Exception ex)
        {
            log.Warn($"{reason}: {what.Title()} failed: {ex.Message}");
            return StepResult.Failure(ex.Message);
        }
    }
}
