using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using UpsTools.Services;

namespace UpsTools;

public partial class App : Application
{
    // installer\UpsTools.iss checks the normal copy's mutex by name.
    private static readonly string InstanceMutexName = $@"Local\UpsTools{AppProfile.Suffix}.SingleInstance";
    private static readonly string ShowWindowEventName = $@"Local\UpsTools{AppProfile.Suffix}.ShowWindow";
    private static readonly string ExitEventName = $@"Local\UpsTools{AppProfile.Suffix}.Exit";
    private const string ExitArgument = "--exit";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showWindowSignal;
    private EventWaitHandle? _exitSignal;
    private AppController? _controller;
    private TrayIcon? _tray;
    private Notifier? _notifier;
    private MainWindow? _window;
    private PowerWatcher? _power;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Started by a script's administrator task: run the script, report its exit code, show nothing.
        if (ElevatedTasks.IsRunScript(e.Args))
            Environment.Exit(ScriptRunner.RunFromTask(ElevatedTasks.RawCommandLine));

        // Used by the installer before replacing the exe: exit code 0 once no copy is running.
        if (e.Args.Contains(ExitArgument, StringComparer.OrdinalIgnoreCase))
            Environment.Exit(StopRunningCopy() ? 0 : 1);

        // Used by the uninstaller: removes what the app registered outside its folder.
        if (e.Args.Contains(Uninstaller.Argument, StringComparer.OrdinalIgnoreCase))
            Environment.Exit(Task.Run(Uninstaller.RunAsync).GetAwaiter().GetResult());
        if (e.Args.Contains(Uninstaller.DeleteTasksArgument, StringComparer.OrdinalIgnoreCase))
            Environment.Exit(ElevatedTasks.DeleteOwnTasks());

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Already running: bring its window forward.
            try { EventWaitHandle.OpenExisting(ShowWindowEventName).Set(); }
            catch { /* already exiting */ }
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }
        ListenForSignals();

        base.OnStartup(e);

        _controller = new AppController(Dispatcher);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _controller.Log.Warn($"Background error: {args.Exception.GetBaseException().Message}");
            args.SetObserved();
        };

        try { StartupRegistration.RefreshPathIfEnabled(); } catch { /* not critical */ }

        _tray = new TrayIcon(ShowMainWindow, () => _ = ExitAsync());
        _notifier = new Notifier(_controller.Log, _tray, () => Dispatcher.InvokeAsync(ShowMainWindow));
        _controller.AttachTray(_tray, _notifier);
        _power = new PowerWatcher();
        _power.Suspending += OnSuspending;
        _power.Resumed += () => Dispatcher.InvokeAsync(() => _controller?.OnResumedFromSleep());
        _power.Thawed += frozen => Dispatcher.InvokeAsync(() => _controller?.OnThawed(frozen));
        _controller.SetPowerSource(_power.PowerSource);
        _power.PowerSourceChanged += source => Dispatcher.InvokeAsync(() => _controller?.SetPowerSource(source));
        if (!_power.SeesModernStandby) _controller.Log.Info("Windows won't say when the PC goes into Modern Standby");
        SessionEnding += OnSessionEnding;

        _controller.Log.Info($"UPS Tools started ({_controller.Configuration.Version})");
        _controller.StartSharing(); // first: the PCs agree who connects before anyone does
        _controller.StartMonitoring();

        if (!e.Args.Contains(StartupRegistration.BackgroundArgument, StringComparer.OrdinalIgnoreCase))
            ShowMainWindow();
    }

    /// <summary>
    /// Asks a running copy to exit (it disconnects from the UPS first) and waits until it has, up to 30 s.
    /// </summary>
    /// <returns>Whether no copy is running any more.</returns>
    internal static bool StopRunningCopy()
    {
        using var mutex = new Mutex(initiallyOwned: false, InstanceMutexName);
        try { EventWaitHandle.OpenExisting(ExitEventName).Set(); }
        catch { /* not running, or already exiting */ }
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) return false;
        }
        catch (AbandonedMutexException)
        {
            // It ended without releasing it: gone all the same, and the mutex is now ours.
        }
        mutex.ReleaseMutex();
        return true;
    }

    private void ListenForSignals()
    {
        _showWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var thread = new Thread(() =>
        {
            WaitHandle[] handles = [_showWindowSignal, _exitSignal];
            while (!_exiting)
            {
                var which = WaitHandle.WaitAny(handles);
                if (_exiting) return;
                if (which == 0) Dispatcher.InvokeAsync(ShowMainWindow);
                else Dispatcher.InvokeAsync(() => _ = ExitAsync());
            }
        })
        { IsBackground = true, Name = "Instance signal listener" };
        thread.Start();
    }

    private void ShowMainWindow()
    {
        if (_exiting || _controller is null) return;
        if (_window is null)
        {
            _window = new MainWindow(_controller);
            _window.Closing += OnWindowClosing;
            _window.Closed += (_, _) => _window = null;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;

        if (_controller?.Settings.CloseToTray != true)
        {
            // Not from inside Closing: ExitAsync closes the window, which WPF refuses while it is closing.
            Dispatcher.InvokeAsync(() => _ = ExitAsync());
            return;
        }

        _window?.Hide();
        _controller?.OnClosedToTray();
    }

    private void OnSuspending()
    {
        // Handled before this returns, as Windows goes on to sleep (or suspends the app) after that; the links have
        // to be closed by then. On the UI thread, where the controller works, but not for longer than Windows waits.
        if (_exiting) return;
        try { Dispatcher.Invoke(() => _controller?.OnGoingToSleep(), DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { _controller?.Log.Info($"Getting ready for sleep failed: {ex.GetBaseException().Message}"); }
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        // Shutting down, restarting or signing out. WPF shuts the app down once this returns, and Windows ends the
        // process soon after, so the links are closed right here, as before sleep.
        if (_exiting) return;
        _exiting = true;
        _power?.Dispose();
        if (_controller is not null)
        {
            _controller.OnSessionEnding(signingOut: e.ReasonSessionEnding == ReasonSessionEnding.Logoff);
            _controller.Log.Info("UPS Tools exiting");
        }
        _tray?.Dispose();
        _showWindowSignal?.Set(); // release the listener thread
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;

        _power?.Dispose();
        _window?.Close();

        if (_controller is not null)
        {
            _controller.Log.Info("UPS Tools exiting");
            try { await _controller.StopMonitoringAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* exit anyway */ }
        }

        _tray?.Dispose();
        _showWindowSignal?.Set(); // release the listener thread
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _instanceMutex?.ReleaseMutex(); } catch { /* not owned */ }
        _instanceMutex?.Dispose();
        _showWindowSignal?.Dispose();
        _exitSignal?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _controller?.Log.Warn($"Unexpected error: {e.Exception.Message}");
        MessageBox.Show(e.Exception.Message, "UPS Tools", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
