using System.Collections.Concurrent;
using System.Security.Cryptography;
using UpsTools.Core;
using UpsTools.Core.Bluetti;
using UpsTools.Tests.Support;

namespace UpsTools.Tests;

/// <summary>The Bluetti connection against a simulated device: handshake, reads, faults and reconnects.</summary>
public class BluettiConnectionTests
{
    private static readonly BluettiTimings Fast = new(
        ChallengeWait: TimeSpan.FromMilliseconds(300),
        Handshake: TimeSpan.FromMilliseconds(500),
        Response: TimeSpan.FromMilliseconds(500),
        Write: TimeSpan.FromMilliseconds(500),
        Retry: TimeSpan.FromMilliseconds(10),
        Unsubscribe: TimeSpan.FromMilliseconds(300));

    private readonly FakeBluettiDevice _device = new();
    private readonly ConcurrentQueue<string> _log = new();

    public BluettiConnectionTests() => _device.Load(TestData.RealEl100V2OnGrid);

    private BluettiConnection Connect(BluettiModel? model = null, BluettiTimings? timings = null) =>
        new(model ?? TestData.El100V2, "0C:4E:A0:E0:81:E6", _device.Factory, _log.Enqueue, timings ?? Fast,
            FakeBluettiDevice.SigningKey);

    private static void AssertRealReading(UpsStatus status)
    {
        Assert.Equal("EL100V2", status.Model);
        Assert.Equal("2527008220279", status.Serial);
        Assert.Equal(100, status.BatteryPercent);
        Assert.True(status.GridPresent);
        Assert.Equal(157, status.AcOutputW);
    }

    [Fact]
    public async Task Encrypted_session_reads_the_registers()
    {
        await using var connection = Connect();
        var status = await connection.ReadAsync(CancellationToken.None);

        AssertRealReading(status);
        Assert.True(connection.Encrypted);
        Assert.Contains("app key valid", _device.Log);
        Assert.Contains("Encrypted session established", _log);
        Assert.Equal([(102, 18, true), (140, 8, true), (1314, 2, true), (1511, 1, true)],
            _device.Reads.Select(r => ((int)r.Start, (int)r.Count, r.Encrypted)));
        Assert.Equal(1, _device.Connections);
    }

    [Fact]
    public async Task Later_reads_reuse_the_session()
    {
        await using var connection = Connect();
        await connection.ReadAsync(CancellationToken.None);
        _device.Registers[102] = 42;

        var status = await connection.ReadAsync(CancellationToken.None);

        Assert.Equal(42, status.BatteryPercent);
        Assert.Equal(1, _device.Connections);
        Assert.Equal(1, _device.Current!.Subscriptions);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(512)]
    public async Task Messages_split_across_notifications_are_reassembled(int mtu)
    {
        _device.Mtu = mtu;
        await using var connection = Connect();
        AssertRealReading(await connection.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(512)]
    public async Task A_device_without_encryption_is_read_in_plain_text(int mtu)
    {
        _device.Encrypted = false;
        _device.Mtu = mtu;
        await using var connection = Connect();

        AssertRealReading(await connection.ReadAsync(CancellationToken.None));

        Assert.False(connection.Encrypted);
        Assert.All(_device.Reads, r => Assert.False(r.Encrypted));
        Assert.Contains("Device did not start an encryption handshake; using plain Modbus", _log);
        Assert.Equal(2, _device.Current!.Subscriptions); // re-subscribed once, hoping for a challenge
    }

    [Fact]
    public async Task No_challenge_at_first_resubscribes_and_gets_one()
    {
        _device.ChallengesToSkip = 1;
        await using var connection = Connect();

        AssertRealReading(await connection.ReadAsync(CancellationToken.None));

        Assert.True(connection.Encrypted);
        Assert.Equal(["subscribe", "unsubscribe", "subscribe", "challenge"], _device.Log.Take(4));
    }

    [Fact]
    public async Task A_device_that_used_encryption_never_falls_back_to_plain_text()
    {
        await using var connection = Connect();
        await connection.ReadAsync(CancellationToken.None);
        await connection.ResetAsync();
        _device.Encrypted = false;

        var error = await Assert.ThrowsAsync<IOException>(() => connection.ReadAsync(CancellationToken.None));

        Assert.Equal("Device did not start the encryption handshake", error.Message);
        Assert.Equal(3, _device.Connections); // the first session, then an attempt and its retry
    }

    [Fact]
    public async Task A_stalled_handshake_is_retried_once()
    {
        _device.HandshakesToStall = 1;
        await using var connection = Connect();

        AssertRealReading(await connection.ReadAsync(CancellationToken.None));

        Assert.Equal(2, _device.Connections);
        Assert.Contains(_log, l => l.StartsWith("Connection attempt failed (") && l.EndsWith("; retrying"));
        Assert.True(_device.Links[0].Disposed);
    }

    [Fact]
    public async Task Two_stalled_handshakes_fail_the_read()
    {
        _device.HandshakesToStall = 2;
        await using var connection = Connect();

        await Assert.ThrowsAsync<TimeoutException>(() => connection.ReadAsync(CancellationToken.None));
        Assert.Equal(2, _device.Connections);
        Assert.All(_device.Links, l => Assert.True(l.Disposed));
    }

    [Fact]
    public async Task A_failed_connection_is_retried_once()
    {
        _device.ConnectFailures = 1;
        await using var connection = Connect();
        AssertRealReading(await connection.ReadAsync(CancellationToken.None));
        Assert.Contains("Connection attempt failed (Bluetooth device not found); retrying", _log);

        _device.ConnectFailures = 2;
        await connection.ResetAsync();
        var error = await Assert.ThrowsAsync<IOException>(() => connection.ReadAsync(CancellationToken.None));
        Assert.Equal("Bluetooth device not found", error.Message);
    }

    [Fact]
    public async Task A_device_key_with_a_bad_signature_is_refused()
    {
        _device.BadSignature = true;
        await using var connection = Connect();

        var error = await Assert.ThrowsAnyAsync<CryptographicException>(() => connection.ReadAsync(CancellationToken.None));

        Assert.Equal("Device public key signature is invalid", error.Message);
        Assert.Contains("Protocol error: Device public key signature is invalid", _log);
        Assert.Empty(_device.Reads);
    }

    [Fact]
    public async Task Bluettis_own_key_refuses_a_device_signed_with_another()
    {
        // Without a test key, the connection trusts only Bluetti's K2, which the simulated device can't sign with.
        await using var connection = new BluettiConnection(TestData.El100V2, "x", _device.Factory, _log.Enqueue, Fast);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => connection.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_rejected_key_fails_the_handshake()
    {
        _device.RejectKey = true;
        await using var connection = Connect();
        var error = await Assert.ThrowsAnyAsync<CryptographicException>(() => connection.ReadAsync(CancellationToken.None));
        Assert.Equal("Device rejected our public key", error.Message);
    }

    [Fact]
    public async Task A_modbus_exception_fails_the_read_and_the_next_read_reconnects()
    {
        await using var connection = Connect();
        await connection.ReadAsync(CancellationToken.None);

        _device.Answer = (start, count) => start == 140 ? ExceptionFrame(2) : FakeBluettiDevice.ReadResponse(new byte[count * 2]);
        var error = await Assert.ThrowsAsync<ModbusException>(() => connection.ReadAsync(CancellationToken.None));
        Assert.Equal("Device returned Modbus exception 2", error.Message);
        Assert.True(_device.Links[0].Disposed);

        _device.Answer = null;
        AssertRealReading(await connection.ReadAsync(CancellationToken.None));
        Assert.Equal(2, _device.Connections);
    }

    [Fact]
    public async Task A_corrupted_response_fails_the_read()
    {
        _device.Answer = (_, count) =>
        {
            var frame = FakeBluettiDevice.ReadResponse(new byte[count * 2]);
            frame[4] ^= 0x01;
            return frame;
        };
        await using var connection = Connect();
        var error = await Assert.ThrowsAsync<ModbusException>(() => connection.ReadAsync(CancellationToken.None));
        Assert.Equal("Response CRC mismatch", error.Message);
    }

    [Fact]
    public async Task No_answer_times_out_and_the_next_read_reconnects()
    {
        _device.Answer = (_, _) => null;
        await using var connection = Connect();

        await Assert.ThrowsAsync<TimeoutException>(() => connection.ReadAsync(CancellationToken.None));

        _device.Answer = null;
        AssertRealReading(await connection.ReadAsync(CancellationToken.None));
        Assert.Equal(2, _device.Connections);
    }

    [Fact]
    public async Task A_link_dropped_while_waiting_fails_the_read_at_once()
    {
        await using var connection = Connect(timings: Fast with { Response = TimeSpan.FromSeconds(30) });
        await connection.ReadAsync(CancellationToken.None);
        _device.Answer = (_, _) =>
        {
            _device.Current!.Disconnect();
            return null;
        };

        var started = DateTime.UtcNow;
        var error = await Assert.ThrowsAsync<IOException>(() => connection.ReadAsync(CancellationToken.None));

        Assert.Equal("Bluetooth link dropped", error.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.Contains("Bluetooth link dropped", _log);
    }

    [Fact]
    public async Task A_link_dropped_between_reads_reconnects_on_the_next()
    {
        await using var connection = Connect();
        await connection.ReadAsync(CancellationToken.None);

        _device.Current!.Disconnect();
        AssertRealReading(await connection.ReadAsync(CancellationToken.None));

        Assert.Equal(2, _device.Connections);
    }

    [Fact]
    public async Task A_failed_write_fails_the_read()
    {
        await using var connection = Connect();
        await connection.ReadAsync(CancellationToken.None);
        _device.WritesFail = true;

        var error = await Assert.ThrowsAsync<IOException>(() => connection.ReadAsync(CancellationToken.None));
        Assert.Equal("Bluetooth write failed (Unreachable)", error.Message);
    }

    [Fact]
    public async Task Reset_unsubscribes_and_releases_the_link()
    {
        await using var connection = Connect();
        await connection.ReadAsync(CancellationToken.None);
        var link = _device.Current!;

        await connection.ResetAsync();

        Assert.False(link.Subscribed);
        Assert.True(link.Disposed);
        Assert.False(connection.Encrypted);
        AssertRealReading(await connection.ReadAsync(CancellationToken.None));
        Assert.Equal(2, _device.Connections);
    }

    [Fact]
    public async Task Dispose_releases_the_link()
    {
        var connection = Connect();
        await connection.ReadAsync(CancellationToken.None);
        await connection.DisposeAsync();
        Assert.True(_device.Current!.Disposed);
    }

    [Fact]
    public async Task A_cancelled_read_throws_and_leaves_no_link()
    {
        _device.Answer = (_, _) => null;
        await using var connection = Connect(timings: Fast with { Response = TimeSpan.FromSeconds(30) });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.ReadAsync(cancel.Token));
        Assert.True(_device.Current!.Disposed);
    }

    [Fact]
    public async Task A_model_without_input_voltage_reads_two_blocks_and_infers_the_grid_from_input_power()
    {
        await using var connection = Connect(BluettiModels.FromName("AC2A"));
        var status = await connection.ReadAsync(CancellationToken.None);

        Assert.Equal([(102, 18), (140, 8)], _device.Reads.Select(r => ((int)r.Start, (int)r.Count)));
        Assert.True(status.GridPresent); // 157 W AC input
        Assert.Null(status.AcInputVoltage);
        Assert.Null(status.AcOutputVoltage);
    }

    [Fact]
    public async Task Concurrent_reads_take_turns()
    {
        await using var connection = Connect();
        var reads = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => connection.ReadAsync(CancellationToken.None)));
        Assert.All(reads, AssertRealReading);
        Assert.Equal(16, _device.Reads.Count);
    }

    private static byte[] ExceptionFrame(byte code)
    {
        byte[] frame = [0x01, 0x83, code, 0, 0];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3), Modbus.Crc16(frame.AsSpan(0, 3)));
        return frame;
    }
}
