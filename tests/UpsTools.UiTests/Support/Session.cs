using System.Runtime.InteropServices;

namespace UpsTools.UiTests.Support;

/// <summary>Tells a process the Windows session is ending, the way Windows does on shutdown, restart or sign-out.</summary>
internal static class Session
{
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private static readonly nint ENDSESSION_LOGOFF = unchecked((nint)0x80000000u);
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>
    /// Sends WM_QUERYENDSESSION, then WM_ENDSESSION, to each top-level window of the process (shown or hidden),
    /// waiting up to 5 s for each, as Windows does.
    /// </summary>
    public static void End(int pid, bool signingOut)
    {
        var lParam = signingOut ? ENDSESSION_LOGOFF : 0;
        var windows = WindowsOf(pid);
        Assert.NotEmpty(windows);
        foreach (var window in windows) SendMessageTimeout(window, WM_QUERYENDSESSION, 0, lParam, SMTO_ABORTIFHUNG, 5000, out _);
        foreach (var window in windows) SendMessageTimeout(window, WM_ENDSESSION, 1, lParam, SMTO_ABORTIFHUNG, 5000, out _);
    }

    private static List<nint> WindowsOf(int pid)
    {
        var found = new List<nint>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == pid) found.Add(window);
            return true;
        }, 0);
        return found;
    }

    private delegate bool EnumWindowsProc(nint window, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}
