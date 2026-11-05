using System.Windows.Threading;

namespace UpsTools.AppTests.Support;

/// <summary>An STA thread running a WPF dispatcher, shared by the tests that need the UI thread.</summary>
internal static class UiThread
{
    private static readonly Lazy<Dispatcher> Instance = new(() =>
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "Test UI thread" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    });

    public static Dispatcher Dispatcher => Instance.Value;

    public static Task Run(Action action) => Dispatcher.InvokeAsync(action).Task;

    public static Task<T> Run<T>(Func<T> func) => Dispatcher.InvokeAsync(func).Task;

    public static Task Run(Func<Task> func) => Dispatcher.InvokeAsync(func).Task.Unwrap();

    /// <summary>Lets the UI thread process everything queued so far.</summary>
    public static Task Drain() => Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
