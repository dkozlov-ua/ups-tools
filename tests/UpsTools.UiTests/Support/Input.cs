using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;

namespace UpsTools.UiTests.Support;

/// <summary>Real mouse and keyboard input (SendInput), for what UI Automation can't do: dragging, shortcuts.</summary>
internal static class Input
{
    public const ushort Alt = 0x12, Up = 0x26, Down = 0x28, Tab = 0x09, Apps = 0x5D, Escape = 0x1B, Enter = 0x0D;

    [ModuleInitializer]
    internal static void DpiAware() => SetProcessDpiAwarenessContext(-4); // per-monitor v2: UIA and SendInput agree on pixels

    public static void Keys(params ushort[] chord)
    {
        var inputs = new List<INPUT>();
        foreach (var key in chord) inputs.Add(Key(key, up: false));
        foreach (var key in chord.Reverse()) inputs.Add(Key(key, up: true));
        Send(inputs);
        Thread.Sleep(150);
    }

    public static void MoveTo(Point p)
    {
        SetCursorPos((int)p.X, (int)p.Y);
        Thread.Sleep(30);
    }

    /// <summary>Presses at <paramref name="from"/>, moves to <paramref name="to"/> in small steps, releases.</summary>
    public static void Drag(Point from, Point to)
    {
        MoveTo(from);
        Send([Mouse(0x0002)]); // left down
        Thread.Sleep(100);
        const int steps = 20;
        for (var i = 1; i <= steps; i++)
        {
            MoveTo(new Point(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps));
            Thread.Sleep(25);
        }
        Thread.Sleep(200);
        Send([Mouse(0x0004)]); // left up
        Thread.Sleep(300);
    }

    public static void Click(Point p, bool right = false)
    {
        MoveTo(p);
        Send([Mouse(right ? 0x0008u : 0x0002u), Mouse(right ? 0x0010u : 0x0004u)]);
        Thread.Sleep(200);
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = 1,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = (up ? 0x0002u : 0) | (IsExtended(vk) ? 0x0001u : 0) } },
    };

    private static bool IsExtended(ushort vk) => vk is Up or Down or Apps;

    private static INPUT Mouse(uint flags) => new() { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } } };

    private static void Send(List<INPUT> inputs)
    {
        var sent = SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<INPUT>());
        if (sent != inputs.Count) throw new InvalidOperationException($"SendInput sent {sent} of {inputs.Count} (error {Marshal.GetLastWin32Error()})");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(nint value);
}
