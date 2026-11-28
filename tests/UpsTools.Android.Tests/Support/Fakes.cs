using System.Collections.Concurrent;
using System.Threading.Channels;
using UpsTools.Core;
using UpsTools.Core.Ble;

namespace UpsTools.Droid.Tests.Support;

/// <summary>A UI thread of its own, as Android's main looper: posted actions run on it, one at a time, in order.</summary>
internal sealed class TestUiThread : IUiThread, IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;

    public TestUiThread()
    {
        _thread = new Thread(() =>
        {
            foreach (var action in _queue.GetConsumingEnumerable()) action();
        }) { IsBackground = true, Name = "UI" };
        _thread.Start();
    }

    public void Post(Action action) => _queue.Add(action);

    public Task<T> Run<T>(Func<T> func)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { done.SetResult(func()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    public Task Run(Action action) => Run(() => { action(); return true; });

    /// <summary>Runs an async method that starts on the UI thread (its continuations don't come back to it).</summary>
    public async Task<T> Run<T>(Func<Task<T>> func) => await await Run<Task<T>>(func);

    public async Task Run(Func<Task> func) => await await Run<Task>(func);

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>A UPS that answers each read with what the test says, when it says.</summary>
internal sealed class FakeUps(PairedUps? device = null) : IUpsDriver, IUpsConnection
{
    public const string DriverId = "fake";

    public static readonly PairedUps Paired = new(DriverId, "00:11:22:33:44:55", "FAKE123", "EL100V2");

    private readonly Channel<Func<Task<UpsStatus>>> _steps = Channel.CreateUnbounded<Func<Task<UpsStatus>>>();

    public int Reads;

    public PairedUps Device { get; } = device ?? Paired;
    public string Id => Device.DriverId;
    public string DisplayName => "Fake UPS";

    public Task DiscoverAsync(Action<DiscoveredUps> onFound, TimeSpan duration, CancellationToken cancellationToken)
    {
        onFound(new DiscoveredUps(Id, Device.Address, Device.Name, Device.Model, -50));
        return Task.CompletedTask;
    }

    public IUpsConnection CreateConnection(PairedUps device) => this;

    public static UpsStatus Status(bool? grid, int battery = 80, double output = 120) => new()
    {
        Timestamp = DateTimeOffset.Now,
        Model = "EL100V2",
        Serial = "2527008220279",
        GridPresent = grid,
        BatteryPercent = battery,
        CapacityWh = 1024,
        AcInputW = grid == true ? output : 0,
        AcOutputW = output,
    };

    public void Answer(bool? grid, int battery = 80) => _steps.Writer.TryWrite(() => Task.FromResult(Status(grid, battery)));

    public void Fail(string message = "out of range") =>
        _steps.Writer.TryWrite(() => Task.FromException<UpsStatus>(new IOException(message)));

    public async Task<UpsStatus> ReadAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Reads);
        var step = await _steps.Reader.ReadAsync(cancellationToken);
        return await step();
    }

    public Task ResetAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeNotifications : IEventNotifications
{
    public ConcurrentQueue<(UpsNotice Notice, string Ups, string Source)> Shown { get; } = new();
    public int Tests;

    public void Show(UpsNotice notice, string ups, string source) => Shown.Enqueue((notice, ups, source));

    public void ShowTest(string? ups) => Interlocked.Increment(ref Tests);
}

/// <summary>Bluetooth that hears the advertisements the test gives it, as Android hands them over (raw bytes).</summary>
internal sealed class FakeBle(params (ulong Address, short Rssi, byte[] Raw)[] heard) : IBlePlatform
{
    public Task ScanAsync(Action<BleAdvertisement> onReceived, TimeSpan duration, CancellationToken cancellationToken)
    {
        foreach (var (address, rssi, raw) in heard) onReceived(AdvertisementParser.Parse(address, rssi, raw));
        return Task.CompletedTask;
    }

    public Task<IBleLink> ConnectAsync(ulong address, IReadOnlyList<GattChannel> channels, string brand, Action<string>? trace,
        CancellationToken cancellationToken) => throw new IOException("not in this test");
}

internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("upstools-android-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* a file still open: the temp folder gets cleaned eventually */ }
    }
}
