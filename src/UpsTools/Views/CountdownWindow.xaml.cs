using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using UpsTools.Core;

namespace UpsTools.Views;

/// <summary>
/// Counts down before UPS Tools sleeps, hibernates or shuts down the PC, on top of everything, so whoever is at the
/// PC can cancel it (or do it at once). With nobody there, it runs out and the action goes ahead.
/// </summary>
public partial class CountdownWindow : Window
{
    private readonly PowerAction _action;
    private readonly TimeSpan _delay;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private readonly TaskCompletionSource<bool?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;

    private CountdownWindow(PowerAction action, TimeSpan delay, string reason)
    {
        InitializeComponent();
        _action = action;
        _delay = delay;
        Title = $"{action.Title()} — UPS Tools";
        NowButton.Content = $"{action.Title()} now";
        ReasonText.Text = $"{reason}. {action.Description()}";
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher);
        Closed += (_, _) =>
        {
            _closed = true;
            Finish(false); // closing it cancels
        };
        Tick();
    }

    /// <summary>Shows the countdown (a <see cref="Services.PowerCountdown"/>); call on the UI thread.</summary>
    /// <returns>True to go ahead: the time ran out, or "now" was clicked; false: cancelled.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled; the window is closed.</exception>
    public static async Task<bool> ShowAsync(PowerAction action, TimeSpan delay, string reason, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var window = new CountdownWindow(action, delay, reason);
        await using var stop = cancel.Register(() => window.Dispatcher.InvokeAsync(() => window.Finish(null)));
        window.Show();
        window.Activate();
        window._timer.Start();
        return await window._result.Task ?? throw new OperationCanceledException(cancel);
    }

    private void Tick()
    {
        var left = _delay - _clock.Elapsed;
        if (left <= TimeSpan.Zero)
        {
            Finish(true);
            return;
        }
        CountdownText.Text = $"{_action.Doing()} in {Math.Ceiling(left.TotalSeconds):0} s";
        Remaining.Value = left / _delay;
    }

    private void Now_Click(object sender, RoutedEventArgs e) => Finish(true);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(false);

    /// <param name="result">Go ahead, cancelled, or null: stopped from outside.</param>
    private void Finish(bool? result)
    {
        if (!_result.TrySetResult(result)) return;
        _timer.Stop();
        if (!_closed) Close();
    }
}
