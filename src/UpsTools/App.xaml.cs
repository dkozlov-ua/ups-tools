using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using UpsTools.Services;

namespace UpsTools;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\UpsTools.SingleInstance";
    private const string ShowWindowEventName = @"Local\UpsTools.ShowWindow";
    private const string ExitEventName = @"Local\UpsTools.Exit";
    private const string ExitArgument = "--exit";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showWindowSignal;
    private EventWaitHandle? _exitSignal;
    private AppController? _controller;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;
    private bool _trayHintShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        var exitRequested = e.Args.Contains(ExitArgument, StringComparer.OrdinalIgnoreCase);
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance || exitRequested)
        {
            // Already running: bring its window forward, or ask it to exit (used before upgrading the exe).
            try { EventWaitHandle.OpenExisting(exitRequested ? ExitEventName : ShowWindowEventName).Set(); }
            catch { /* not running, or already exiting */ }
            if (isFirstInstance) _instanceMutex.ReleaseMutex();
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
        _controller.AttachTray(_tray);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _controller.Log.Info($"UPS Tools started ({_controller.Configuration.Version})");
        _controller.StartMonitoring();

        if (!e.Args.Contains(StartupRegistration.BackgroundArgument, StringComparer.OrdinalIgnoreCase))
            ShowMainWindow();
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
            _ = ExitAsync();
            return;
        }

        _window?.Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray?.Notify("UPS Tools is still running", "It keeps watching your UPS from the notification area. Right-click the icon to exit.");
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.InvokeAsync(() => _controller?.OnResumedFromSleep());
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
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
