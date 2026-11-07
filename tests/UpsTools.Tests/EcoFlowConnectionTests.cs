using System.Collections.Concurrent;
using UpsTools.Core;
using UpsTools.Core.EcoFlow;
using UpsTools.Tests.Support;
using static UpsTools.Tests.EcoFlowProtocolTests;
using P = UpsTools.Core.EcoFlow.EcoFlowStatusParser;

namespace UpsTools.Tests;

/// <summary>The EcoFlow connection against a simulated device: handshake, authentication, uploads, faults and reconnects.</summary>
public class EcoFlowConnectionTests
{
    private static readonly EcoFlowTimings Fast = new(
        Reply: TimeSpan.FromMilliseconds(300),
        FirstData: TimeSpan.FromMilliseconds(500),
        Silence: TimeSpan.FromSeconds(60),
        Write: TimeSpan.FromMilliseconds(300),
        Retry: TimeSpan.FromMilliseconds(10),
        Unsubscribe: TimeSpan.FromMilliseconds(300));

    private readonly FakeEcoFlowDevice _device = new();
    private readonly ConcurrentQueue<string> _log = new();
    private DateTimeOffset _now = new(2026, 11, 7, 12, 0, 0, TimeSpan.FromHours(2));

    public EcoFlowConnectionTests()
    {
        _device.Uploads.Add([.. Float(P.CmsBattSoc, 87), .. Float(P.PowGetAcIn, 120), .. Float(P.PowGetAcOut, -100)]);
        _device.Uploads.Add([.. ProtoFields.EncodeVarint(P.AcChargerFlag, 1), .. Float(P.AcInVoltage, 231.5f)]);
    }

    private EcoFlowConnection Connect(string userId = FakeEcoFlowDevice.UserId) =>
        new(EcoFlowModels.FromSerial(FakeEcoFlowDevice.Serial)!, FakeEcoFlowDevice.Serial, userId, "E4:B3:23:8E:7B:3E",
            _device.Factory, _log.Enqueue, Fast, () => _now);

    private async Task<UpsStatus> ReadWhenAsync(EcoFlowConnection connection, Func<UpsStatus, bool> condition)
    {
        var status = await connection.ReadAsync(CancellationToken.None);
        for (var i = 0; i < 100 && !condition(status); i++)
        {
            await Task.Delay(10);
            status = await connection.ReadAsync(CancellationToken.None);
        }
        return status;
    }

    [Fact]
    public async Task Reads_the_state_once_authenticated()
    {
        await using var connection = Connect();

        var status = await ReadWhenAsync(connection, s => s.AcInputVoltage is not null);

        Assert.Equal(("River 3 Plus", FakeEcoFlowDevice.Serial), (status.Model, status.Serial));
        Assert.Equal((87, 286.0, true), (status.BatteryPercent, status.CapacityWh, status.GridPresent));
        Assert.Equal((120.0, 100.0, 231.5), (status.AcInputW, status.AcOutputW, status.AcInputVoltage));
        Assert.Equal(_now, status.Timestamp);
        Assert.Contains("user accepted", _device.Log);
        Assert.Contains("Authenticated", _log);
        Assert.Equal(1, _device.Connections);
    }

    [Fact]
    public async Task Later_uploads_update_the_fields_they_carry()
    {
        await using var connection = Connect();
        await ReadWhenAsync(connection, s => s.GridPresent == true);

        _device.Current!.Upload([.. Float(P.AcInVoltage, 0), .. Float(P.PowGetAcIn, 0)]);
        var status = await ReadWhenAsync(connection, s => s.GridPresent == false);

        Assert.Equal((false, 0.0, 87, 100.0), (status.GridPresent, status.AcInputW, status.BatteryPercent, status.AcOutputW));
        Assert.Equal(1, _device.Connections);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(512)]
    public async Task Messages_split_across_notifications_are_reassembled(int mtu)
    {
        _device.Mtu = mtu;
        await using var connection = Connect();
        Assert.Equal(87, (await connection.ReadAsync(CancellationToken.None)).BatteryPercent);
    }

    [Fact]
    public async Task Repeated_handshake_replies_are_ignored()
    {
        _device.HandshakeCopies = 3;
        await using var connection = Connect();
        Assert.Equal(87, (await connection.ReadAsync(CancellationToken.None)).BatteryPercent);
        Assert.Equal(1, _device.Connections);
    }

    [Fact]
    public async Task A_first_upload_counts_as_accepted()
    {
        _device.UploadInsteadOfAuthReply = true;
        await using var connection = Connect();
        Assert.Equal(87, (await connection.ReadAsync(CancellationToken.None)).BatteryPercent);
    }

    [Fact]
    public async Task A_wrong_user_id_is_reported_and_not_retried()
    {
        await using var connection = Connect(userId: "42");

        var error = await Assert.ThrowsAsync<EcoFlowAuthException>(() => connection.ReadAsync(CancellationToken.None));

        Assert.Equal("The UPS rejected the EcoFlow user ID: check it, and that the UPS is bound to that account", error.Message);
        Assert.Equal(1, _device.Connections);
        Assert.Contains("user rejected", _device.Log);
        Assert.True(_device.Current!.Disposed);
    }

    [Theory]
    [InlineData(3, "The UPS is bound to another EcoFlow account")]
    [InlineData(9, "The UPS rejected the authentication (09)")]
    public async Task Other_refusals_say_why(byte result, string message)
    {
        _device.AuthResult = result;
        await using var connection = Connect();
        var error = await Assert.ThrowsAsync<EcoFlowAuthException>(() => connection.ReadAsync(CancellationToken.None));
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public async Task A_device_that_does_not_answer_is_tried_twice()
    {
        _device.Mute = true;
        await using var connection = Connect();

        var error = await Assert.ThrowsAsync<IOException>(() => connection.ReadAsync(CancellationToken.None));

        Assert.Equal("The UPS did not answer (key exchange)", error.Message);
        Assert.Equal(2, _device.Connections);
        Assert.Contains(_log, l => l.StartsWith("Connection attempt failed (The UPS did not answer (key exchange)); retrying"));
    }

    [Fact]
    public async Task A_device_that_sends_nothing_fails_the_read()
    {
        _device.Uploads.Clear();
        await using var connection = Connect();

        var error = await Assert.ThrowsAsync<IOException>(() => connection.ReadAsync(CancellationToken.None));

        Assert.Equal("The UPS sent no data", error.Message);
        Assert.True(_device.Current!.Disposed);
    }

    [Fact]
    public async Task A_device_gone_silent_counts_as_lost_and_is_reconnected()
    {
        await using var connection = Connect();
        await connection.ReadAsync(CancellationToken.None);

        _now += TimeSpan.FromSeconds(61);
        var error = await Assert.ThrowsAsync<IOException>(() => connection.ReadAsync(CancellationToken.None));
        Assert.Equal("No data from the UPS for 61 s", error.Message);

        Assert.Equal(87, (await connection.ReadAsync(CancellationToken.None)).BatteryPercent);
        Assert.Equal(2, _device.Connections);
    }

    [Fact]
    public async Task A_dropped_link_is_reconnected()
    {
        await using var connection = Connect();
        await connection.ReadAsync(CancellationToken.None);

        _device.Current!.Disconnect();
        await Wait.Until(() => _log.Contains("Bluetooth link dropped"));

        Assert.Equal(87, (await connection.ReadAsync(CancellationToken.None)).BatteryPercent);
        Assert.Equal(2, _device.Connections);
        Assert.True(_device.Links[0].Disposed);
    }

    [Fact]
    public async Task Reset_drops_the_link()
    {
        await using var connection = Connect();
        await connection.ReadAsync(CancellationToken.None);

        await connection.ResetAsync();

        Assert.True(_device.Current!.Disposed);
        Assert.Contains("unsubscribe", _device.Log);
    }

    [Fact]
    public async Task A_failed_connection_is_retried()
    {
        _device.ConnectFailures = 1;
        await using var connection = Connect();
        Assert.Equal(87, (await connection.ReadAsync(CancellationToken.None)).BatteryPercent);
        Assert.Equal(2, _device.Connections);
    }

    [Fact]
    public async Task The_device_asking_for_the_time_gets_it_once_in_a_while()
    {
        _device.AsksForTime = true;
        await using var connection = Connect();
        await connection.ReadAsync(CancellationToken.None);
        await Wait.Until(() => _device.Packets.Count(p => p.CmdSet == 0x01) == 3, what: "the time");

        var time = _device.Packets.Where(p => p.CmdSet == 0x01).ToList();
        Assert.Equal([(0x0B, 0x55), (0x35, 0x52), (0x35, 0x53)], time.Select(p => ((int)p.Dst, (int)p.CmdId)));
        var utc = (uint)_now.ToUnixTimeSeconds();
        Assert.Equal(ProtoFields.EncodeVarint(1, utc), time[0].Payload);
        Assert.Equal([(byte)utc, (byte)(utc >> 8), (byte)(utc >> 16), (byte)(utc >> 24), 2, 0], time[1].Payload);

        // Asked again at once: not answered again.
        _device.Current!.SendPacket(new EcoFlowPacket(0x35, 0x21, 0x01, 0x52, []));
        _device.Current.Upload(Float(P.CmsBattSoc, 50));
        await ReadWhenAsync(connection, s => s.BatteryPercent == 50);
        Assert.Equal(3, _device.Packets.Count(p => p.CmdSet == 0x01));
    }
}
