using System.Net.Sockets;
using UpsTools.Core;
using UpsTools.Core.Sharing;

namespace UpsTools.Tests;

/// <summary>Sharing UPSes between PCs: the code, the encrypted link, and readings from one PC's monitor reaching another's.</summary>
public sealed class SharingTests : IDisposable
{
    private const string Code = "7KQ2-M9XD-4FTB-R1WE";
    private static readonly PairedUps Device = new("bluetti-ble", "0C:4E:A0:E0:81:E6", "EL100V2ABC", "EL100V2", UserId: "secret");

    private readonly SharingHub _hub = new() { Pc = "HOST-PC" };
    private readonly List<string> _log = [];
    private readonly SharingServer _server;
    private readonly List<IAsyncDisposable> _connections = [];

    public SharingTests()
    {
        _server = new SharingServer(_hub, Code, 0, loopbackOnly: true, log: m => { lock (_log) _log.Add(m); });
        _server.Start();
    }

    public void Dispose()
    {
        foreach (var connection in _connections) connection.DisposeAsync().AsTask().Wait();
        _server.Dispose();
    }

    private const string Host = "HOST-PC";

    private NetworkUpsConnection Connect(string? code = null, double retrySeconds = 0.2)
    {
        var connection = new NetworkUpsConnection(System.Net.IPAddress.Loopback, _server.Port, code ?? Code, SharingHub.KeyOf(Device), Host,
            TimeSpan.FromSeconds(retrySeconds)) { From = "WATCHER" };
        _connections.Add(connection);
        return connection;
    }

    private static UpsStatus Reading(bool grid, int battery) => new()
    {
        Timestamp = DateTimeOffset.Now, GridPresent = grid, BatteryPercent = battery, AcOutputW = 120, AverageOutputW = 110,
    };

    private static Task<T> Within<T>(Task<T> task, double seconds = 10) => task.WaitAsync(TimeSpan.FromSeconds(seconds));

    // ---- the code

    [Theory]
    [InlineData("7KQ2-M9XD-4FTB-R1WE", "7KQ2M9XD4FTBR1WE")]
    [InlineData(" 7kq2 m9xd 4ftb r1we ", "7KQ2M9XD4FTBR1WE")]
    [InlineData("OIL0-0000-0000-0000", "0110000000000000")] // read as they look
    [InlineData("7KQ2-M9XD-4FTB-R1W", null)]                // too short
    [InlineData("7KQ2-M9XD-4FTB-R1WU", null)]               // U isn't used
    [InlineData("", null)]
    public void A_code_is_read_as_typed(string typed, string? code) => Assert.Equal(code, SharingCode.Normalize(typed));

    [Fact]
    public void New_codes_are_random_and_grouped()
    {
        var a = SharingCode.New();
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){3}$", a);
        Assert.NotEqual(a, SharingCode.New());
        Assert.Equal(SharingCode.Key(a), SharingCode.Key(a.ToLowerInvariant().Replace("-", " ")));
    }

    // ---- the link

    [Fact]
    public async Task A_link_carries_messages_both_ways()
    {
        var (client, server) = await Pair(SharingCode.Key(Code), SharingCode.Key(Code));
        await client.SendAsync("hello"u8.ToArray(), default);
        Assert.Equal("hello"u8.ToArray(), await server.ReceiveAsync(default));
        await server.SendAsync("back"u8.ToArray(), default);
        Assert.Equal("back"u8.ToArray(), await client.ReceiveAsync(default));
    }

    [Fact]
    public async Task A_different_code_does_not_open_the_link()
    {
        var (client, server) = await Pair(SharingCode.Key(Code), SharingCode.Key(SharingCode.New()));
        await client.SendAsync("hello"u8.ToArray(), default);
        await Assert.ThrowsAsync<WrongSharingCodeException>(() => server.ReceiveAsync(default));
    }

    /// <summary>Two ends of a link over loopback TCP.</summary>
    private static async Task<(SecureChannel Client, SecureChannel Server)> Pair(byte[] clientKey, byte[] serverKey)
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            var tcp = new TcpClient();
            await tcp.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
            var serverSide = await accept;
            var server = SecureChannel.AcceptAsync(serverSide.GetStream(), serverKey, default);
            var client = await SecureChannel.ConnectAsync(tcp.GetStream(), clientKey, default);
            return (client, await server);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ---- listing

    [Fact]
    public async Task Another_pc_lists_what_is_shared_without_the_account()
    {
        _hub.Share(Device, "Desk", TimeSpan.FromSeconds(60));
        using var client = await SharingClient.ConnectAsync(System.Net.IPAddress.Loopback, _server.Port, Code, Host, default);
        client.From = "WATCHER";
        var pc = await client.ListAsync(default);

        Assert.Equal("HOST-PC", pc.Pc);
        Assert.Equal(_hub.Instance, pc.Instance);
        var ups = Assert.Single(pc.Upses);
        Assert.Equal(("bluetti-ble/0C:4E:A0:E0:81:E6", "Desk"), (ups.Key, ups.Title));
        Assert.Equal(Device with { UserId = null }, ups.Device);
        lock (_log) Assert.Contains("WATCHER (127.0.0.1) connected to watch the shared UPSes", _log);
    }

    [Fact]
    public async Task A_wrong_code_is_told_as_such_and_logged()
    {
        var error = await Assert.ThrowsAsync<WrongSharingCodeException>(async () =>
        {
            using var client = await SharingClient.ConnectAsync(System.Net.IPAddress.Loopback, _server.Port, SharingCode.New(), Host, default);
            await client.ListAsync(default);
        });
        Assert.Equal("The sharing code is wrong", error.Message);
        lock (_log) Assert.Contains("Refused 127.0.0.1: wrong sharing code", _log);
    }

    [Fact]
    public async Task A_pc_that_does_not_share_is_told_as_such()
    {
        var port = _server.Port;
        _server.Dispose();
        var error = await Assert.ThrowsAsync<SharingException>(() => SharingClient.ConnectAsync(System.Net.IPAddress.Loopback, port, Code, Host, default));
        Assert.Equal("HOST-PC isn't sharing its UPSes any more", error.Message);
    }

    // ---- readings

    [Fact]
    public async Task Each_read_waits_for_the_next_reading_and_gets_it_as_it_was()
    {
        _hub.Share(Device, "Desk", TimeSpan.FromSeconds(60));
        var first = Reading(true, 80);
        _hub.Publish(Device, first);
        var connection = Connect();

        Assert.Equal(first, await Within(connection.ReadAsync(default))); // the latest, at once

        var next = connection.ReadAsync(default);
        await Task.Delay(300);
        Assert.False(next.IsCompleted); // nothing newer yet
        var second = Reading(false, 79);
        _hub.Publish(Device, second);
        Assert.Equal(second, await Within(next));
    }

    [Fact]
    public async Task Only_the_latest_reading_counts_after_missing_some()
    {
        _hub.Share(Device, "Desk", TimeSpan.FromSeconds(60));
        _hub.Publish(Device, Reading(true, 80));
        var connection = Connect();
        await Within(connection.ReadAsync(default));

        _hub.Publish(Device, Reading(false, 79));
        var latest = Reading(true, 78);
        _hub.Publish(Device, latest);
        Assert.Equal(latest, await Within(connection.ReadAsync(default)));
    }

    [Fact]
    public async Task A_failed_read_on_the_sharing_pc_fails_here_too_and_the_link_stays()
    {
        _hub.Share(Device, "Desk", TimeSpan.FromSeconds(60));
        _hub.PublishFailure(Device, "Bluetooth device not found");
        var connection = Connect(retrySeconds: 30);

        var error = await Assert.ThrowsAnyAsync<SharingException>(() => Within(connection.ReadAsync(default)));
        Assert.Equal($"On {Host}: Bluetooth device not found", error.Message);

        // No wait before the next read (the link is fine): it is the next reading.
        var next = Reading(true, 80);
        _hub.Publish(Device, next);
        Assert.Equal(next, await Within(connection.ReadAsync(default), 5));
    }

    [Fact]
    public async Task A_ups_no_longer_shared_fails_and_comes_back_when_shared_again()
    {
        _hub.Share(Device, "Desk", TimeSpan.FromSeconds(60));
        _hub.Publish(Device, Reading(true, 80));
        var connection = Connect();
        await Within(connection.ReadAsync(default));

        var waiting = connection.ReadAsync(default);
        _hub.Unshare(Device);
        var error = await Assert.ThrowsAsync<SharingException>(() => Within(waiting));
        Assert.Equal("HOST-PC isn't connected to this UPS any more", error.Message);

        // Shared again (e.g. its monitor restarted): its readings count from 1 again, and still reach this PC.
        _hub.Share(Device, "Desk", TimeSpan.FromSeconds(60));
        var again = Reading(true, 77);
        _hub.Publish(Device, again);
        Assert.Equal(again, await Within(connection.ReadAsync(default)));
    }

    [Fact]
    public async Task A_wrong_code_fails_every_read()
    {
        _hub.Share(Device, "Desk", TimeSpan.FromSeconds(60));
        _hub.Publish(Device, Reading(true, 80));
        var connection = Connect(SharingCode.New());
        await Assert.ThrowsAsync<WrongSharingCodeException>(() => Within(connection.ReadAsync(default)));
    }

    [Fact]
    public async Task After_the_link_fails_the_next_read_waits_before_connecting_again()
    {
        var port = _server.Port;
        _server.Dispose();
        var connection = Connect(retrySeconds: 1);
        await Assert.ThrowsAsync<SharingException>(() => Within(connection.ReadAsync(default)));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<SharingException>(() => Within(connection.ReadAsync(default)));
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(0.9), $"retried after {watch.Elapsed}");
        Assert.NotEqual(0, port);
    }

    [Fact]
    public async Task Stopping_the_server_ends_the_waiting_reads()
    {
        _hub.Share(Device, "Desk", TimeSpan.FromSeconds(60));
        _hub.Publish(Device, Reading(true, 80));
        var connection = Connect();
        await Within(connection.ReadAsync(default));

        var waiting = connection.ReadAsync(default);
        await Task.Delay(200);
        _server.Dispose();
        await Assert.ThrowsAsync<SharingException>(() => Within(waiting));
    }

    [Fact]
    public async Task A_monitor_on_the_watching_pc_raises_its_own_events()
    {
        _hub.Share(Device, "Desk", TimeSpan.FromSeconds(60));
        _hub.Publish(Device, Reading(true, 80));
        await using var monitor = new UpsMonitor(Connect(), TimeSpan.FromMinutes(5)); // reads as often as the sharing PC
        var events = new List<UpsEventKind>();
        var online = new TaskCompletionSource();
        monitor.LinkStateChanged += state => { if (state == UpsLinkState.Online) online.TrySetResult(); };
        var raised = new TaskCompletionSource();
        monitor.EventRaised += e =>
        {
            lock (events) events.Add(e.Kind);
            raised.TrySetResult();
        };
        monitor.Start();
        await online.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _hub.Publish(Device, Reading(false, 79));
        await raised.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lock (events) Assert.Equal([UpsEventKind.GridLost], events);
        Assert.Equal(110, monitor.LastStatus!.AverageOutputW); // the sharing PC's average
    }
}
