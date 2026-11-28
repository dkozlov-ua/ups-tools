using System.Runtime.InteropServices;
using Microsoft.Win32;
using UpsTools.Core.Sharing;

namespace UpsTools.Services;

/// <summary>
/// Tells when the PC goes to sleep and wakes up again, including on laptops with Modern Standby (S0 low power idle),
/// where the screen goes off and Windows suspends desktop apps instead of the whole PC. Such a laptop tells only apps
/// that asked for it (<c>PowerRegisterSuspendResumeNotification</c>): the plain broadcast that
/// <see cref="SystemEvents.PowerModeChanged"/> listens to doesn't come. And in case Windows suspends the app without a
/// word, a clock check notices that it was frozen for a while (<see cref="Thawed"/>). The same check looks whether the
/// PC runs on mains power or on its battery (<see cref="PowerSource"/>).
/// </summary>
/// <remarks>The events come on a background thread, apart from the <see cref="SystemEvents"/> fallback's.</remarks>
public sealed class PowerWatcher : IDisposable
{
    /// <summary>How often the clock check looks.</summary>
    internal static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);

    /// <summary>A gap this long between two clock checks means the app was frozen, not just slow.</summary>
    internal static readonly TimeSpan FrozenAfter = TimeSpan.FromSeconds(60);

    private const int DeviceNotifyCallback = 2;
    private const uint PbtApmSuspend = 0x4;
    private const uint PbtApmResumeSuspend = 0x7;
    private const uint PbtApmResumeAutomatic = 0x12;

    private readonly DeviceNotifyCallbackRoutine? _callback; // kept alive while Windows may call it
    private readonly IntPtr _parameters;
    private readonly IntPtr _registration;
    private readonly bool _systemEvents;
    private readonly ManualResetEvent _disposed = new(false);
    private int _asleep;
    private int _wokeSinceCheck; // the gap the next clock check finds is the sleep Windows told of
    private long _lastCheck;
    private int _powerSource = (int)PowerSource.Mains;

    /// <summary>The PC is about to sleep: close what has to be closed before this returns.</summary>
    public event Action? Suspending;

    /// <summary>The PC woke up (once per <see cref="Suspending"/>).</summary>
    public event Action? Resumed;

    /// <summary>The app was frozen this long without being told the PC was going to sleep, and runs again.</summary>
    public event Action<TimeSpan>? Thawed;

    /// <summary>The PC was plugged in or unplugged: what it runs on now.</summary>
    public event Action<PowerSource>? PowerSourceChanged;

    /// <summary>What the PC runs on: mains power, unless it is a laptop (or a tablet) on its battery.</summary>
    public PowerSource PowerSource => (PowerSource)Volatile.Read(ref _powerSource);

    /// <summary>Whether Windows will say when the PC goes into Modern Standby (or the older broadcast is all there is).</summary>
    public bool SeesModernStandby => _registration != IntPtr.Zero;

    /// <summary>Starts listening to Windows.</summary>
    public PowerWatcher()
    {
        _callback = OnNotification;
        _parameters = Marshal.AllocHGlobal(Marshal.SizeOf<DeviceNotifySubscribeParameters>());
        Marshal.StructureToPtr(new DeviceNotifySubscribeParameters
        {
            Callback = Marshal.GetFunctionPointerForDelegate(_callback),
            Context = IntPtr.Zero,
        }, _parameters, fDeleteOld: false);

        try
        {
            if (PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, _parameters, out _registration) != 0)
                _registration = IntPtr.Zero;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _registration = IntPtr.Zero;
        }

        if (_registration == IntPtr.Zero)
        {
            // Not there (before Windows 8): the broadcast covers sleep and hibernation all the same.
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _systemEvents = true;
        }

        _lastCheck = Environment.TickCount64;
        _powerSource = (int)ReadPowerSource();
        new Thread(WatchClock) { IsBackground = true, Name = "Freeze check" }.Start();
    }

    /// <summary>For tests: listens to nothing; <see cref="OnSuspend"/>, <see cref="OnResume"/> and <see cref="OnCheck"/> stand in.</summary>
    internal PowerWatcher(long now)
    {
        _lastCheck = now;
    }

    internal void OnSuspend()
    {
        _asleep = 1;
        Suspending?.Invoke();
    }

    internal void OnResume()
    {
        // Windows may say it twice (woken automatically, then by the user), and the clock check may have said it first.
        if (Interlocked.Exchange(ref _asleep, 0) == 1)
        {
            Volatile.Write(ref _wokeSinceCheck, 1);
            Resumed?.Invoke();
        }
    }

    /// <param name="now">Milliseconds, counting the time the PC spends asleep (<see cref="Environment.TickCount64"/>).</param>
    internal void OnCheck(long now)
    {
        var gap = TimeSpan.FromMilliseconds(now - Interlocked.Exchange(ref _lastCheck, now));
        var woke = Interlocked.Exchange(ref _wokeSinceCheck, 0) == 1;
        if (gap < FrozenAfter) return;
        if (Volatile.Read(ref _asleep) == 1) OnResume(); // woke up before Windows said so
        else if (!woke) Thawed?.Invoke(gap); // not just the sleep Windows told of
    }

    internal void OnPowerSource(PowerSource source)
    {
        if (Interlocked.Exchange(ref _powerSource, (int)source) != (int)source) PowerSourceChanged?.Invoke(source);
    }

    private void WatchClock()
    {
        while (!_disposed.WaitOne(CheckEvery))
        {
            OnCheck(Environment.TickCount64);
            OnPowerSource(ReadPowerSource());
        }
    }

    /// <summary>On battery only when Windows says the AC line is off and there is a battery; mains power otherwise.</summary>
    internal static PowerSource ReadPowerSource()
    {
        try
        {
            if (GetSystemPowerStatus(out var status) && status.AcLineStatus == AcLineOffline && status.BatteryFlag != NoSystemBattery)
                return PowerSource.Battery;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Can't tell: as a desktop.
        }
        return PowerSource.Mains;
    }

    private uint OnNotification(IntPtr context, uint type, IntPtr setting)
    {
        try
        {
            if (type == PbtApmSuspend) OnSuspend();
            else if (type is PbtApmResumeAutomatic or PbtApmResumeSuspend) OnResume();
        }
        catch
        {
            // Never into Windows' power manager.
        }
        return 0;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) OnSuspend();
        else if (e.Mode == PowerModes.Resume) OnResume();
    }

    public void Dispose()
    {
        if (_disposed.WaitOne(0)) return;
        _disposed.Set(); // not disposed itself: the clock check may still be waiting on it
        if (_systemEvents) SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_registration != IntPtr.Zero) PowerUnregisterSuspendResumeNotification(_registration);
        if (_parameters != IntPtr.Zero) Marshal.FreeHGlobal(_parameters);
        GC.KeepAlive(_callback);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint DeviceNotifyCallbackRoutine(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    private const byte AcLineOffline = 0;
    private const byte NoSystemBattery = 128;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("powrprof.dll")]
    private static extern uint PowerRegisterSuspendResumeNotification(int flags, IntPtr recipient, out IntPtr registrationHandle);

    [DllImport("powrprof.dll")]
    private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);
}
