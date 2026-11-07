using System.Buffers.Binary;
using System.Security.Cryptography;
using UpsTools.Core.Ble;
using UpsTools.Core.Bluetti;
using UpsTools.Tests.Support;
using static UpsTools.Tests.Support.TestData;

namespace UpsTools.Tests;

/// <summary>Edge cases of the Modbus framing, the register parser, the models and the crypto, beyond the reference vectors.</summary>
public class BluettiProtocolTests
{
    // ---- Modbus

    [Fact]
    public void Crc16_matches_the_modbus_check_value()
    {
        Assert.Equal(0x4B37, Modbus.Crc16("123456789"u8)); // CRC-16/MODBUS check value
        Assert.Equal(0xFFFF, Modbus.Crc16([]));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("01", null)]
    [InlineData("0103", null)]
    [InlineData("010304", 9)]
    [InlineData("0183", 5)]
    [InlineData("018302", 5)]
    public void Expected_response_length_is_known_once_the_header_is_in(string hex, int? expected)
    {
        Assert.Equal(expected, Modbus.ExpectedResponseLength(Hex(hex)));
    }

    [Fact]
    public void Short_responses_throw()
    {
        var error = Assert.Throws<ModbusException>(() => Modbus.ParseReadResponse(Hex("01030200"), 1));
        Assert.Equal("Response too short (4 bytes)", error.Message);
    }

    [Fact]
    public void Unexpected_function_codes_throw()
    {
        var frame = FakeBluettiDevice.ReadResponse(Hex("0064"));
        frame[1] = 0x04;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(^2), Modbus.Crc16(frame.AsSpan(0, frame.Length - 2)));
        var error = Assert.Throws<ModbusException>(() => Modbus.ParseReadResponse(frame, 1));
        Assert.Equal("Unexpected function code 0x04", error.Message);
    }

    [Fact]
    public void A_payload_of_the_wrong_size_throws()
    {
        var frame = FakeBluettiDevice.ReadResponse(Hex("00640065"));
        var error = Assert.Throws<ModbusException>(() => Modbus.ParseReadResponse(frame, 1));
        Assert.Equal("Unexpected payload size 4 for 1 registers", error.Message);
    }

    // ---- register parser

    [Fact]
    public void Models_with_input_voltage_read_four_blocks_others_two()
    {
        Assert.Equal(4, BluettiStatusParser.BlocksFor(El100V2).Count);
        Assert.Equal([new RegisterBlock(102, 18), new RegisterBlock(140, 8)], BluettiStatusParser.BlocksFor(BluettiModels.FromName("AC2P")!));
    }

    [Theory]
    [InlineData(102, 1, true)]
    [InlineData(119, 1, true)]
    [InlineData(120, 1, false)]
    [InlineData(101, 1, false)]
    [InlineData(116, 4, true)]
    [InlineData(118, 4, false)]
    public void Register_blocks_know_what_they_contain(ushort address, int count, bool expected)
    {
        Assert.Equal(expected, new RegisterBlock(102, 18).Contains(address, count));
    }

    [Fact]
    public void Missing_blocks_leave_readings_unknown()
    {
        var status = BluettiStatusParser.Parse(El100V2, new Dictionary<RegisterBlock, byte[]>(), DateTimeOffset.UnixEpoch);

        Assert.Equal("EL100V2", status.Model); // from the pairing
        Assert.Null(status.Serial);
        Assert.Null(status.GridPresent);
        Assert.Null(status.BatteryPercent);
        Assert.Null(status.AcInputW);
        Assert.Null(status.AcOutputVoltage);
        Assert.Equal(1024, status.CapacityWh);
    }

    [Fact]
    public void Implausible_values_are_dropped()
    {
        var blocks = new Dictionary<RegisterBlock, byte[]>(RealEl100V2OnGrid)
        {
            // SOC 101 %, no model text
            [new(102, 18)] = Hex("0065" + "0000" + "7578" + new string('0', 66)),
            [new(1314, 2)] = Hex("00000000"), // on battery: the runtime would count
        };
        var status = BluettiStatusParser.Parse(El100V2, blocks, DateTimeOffset.UnixEpoch);

        Assert.Null(status.BatteryPercent);
        Assert.Null(status.EstimatedRuntime);
        Assert.Equal("EL100V2", status.Model);
        Assert.Equal("0", status.Serial);
    }

    [Theory]
    [InlineData(799, false)] // 79.9 V
    [InlineData(800, true)]  // 80.0 V
    public void The_grid_is_present_from_80_volts(int tenthsOfVolt, bool expected)
    {
        var blocks = new Dictionary<RegisterBlock, byte[]>(RealEl100V2OnGrid) { [new(1314, 2)] = [(byte)(tenthsOfVolt >> 8), (byte)tenthsOfVolt, 0, 0] };
        Assert.Equal(expected, BluettiStatusParser.Parse(El100V2, blocks, DateTimeOffset.UnixEpoch).GridPresent);
    }

    [Theory]
    [InlineData("0000", false)]
    [InlineData("0001", true)]
    public void Without_input_voltage_the_grid_is_present_when_power_comes_in(string acInput, bool expected)
    {
        var model = BluettiModels.FromName("AC2A")!;
        var blocks = new Dictionary<RegisterBlock, byte[]>
        {
            [new(140, 8)] = Hex("000000000000000000000000" + acInput + "0000"),
        };
        var status = BluettiStatusParser.Parse(model, blocks, DateTimeOffset.UnixEpoch);
        Assert.Equal(expected, status.GridPresent);
        Assert.Null(status.CapacityWh);
    }

    [Fact]
    public void Without_any_grid_reading_the_grid_is_unknown()
    {
        Assert.Null(BluettiStatusParser.Parse(BluettiModels.FromName("AC2A")!, new Dictionary<RegisterBlock, byte[]>(), DateTimeOffset.UnixEpoch).GridPresent);
    }

    // ---- models and addresses

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AC")]
    public void Unknown_names_have_no_model(string? name)
    {
        Assert.Null(BluettiModels.FromAdvertisedName(name));
        Assert.Null(BluettiModels.FromName(name));
    }

    [Fact]
    public void Model_lookups_ignore_case()
    {
        Assert.Equal("EL100V2", BluettiModels.FromName("el100v2")?.Name);
        Assert.Equal("EL100V2", BluettiModels.FromAdvertisedName("el100v2xyz")?.Name);
    }

    [Fact]
    public void Model_names_are_unique_and_only_the_tested_one_says_so()
    {
        Assert.Equal(BluettiModels.All.Count, BluettiModels.All.Select(m => m.Name).Distinct().Count());
        Assert.Equal("EL100V2", Assert.Single(BluettiModels.All, m => m.Tested).Name);
    }

    [Fact]
    public void Bluetooth_addresses_parse_with_dashes_and_format_with_leading_zeros()
    {
        Assert.Equal(0x0C4EA0E081E6UL, BluetoothAddressFormat.Parse("0c-4e-a0-e0-81-e6"));
        Assert.Equal("00:00:00:00:00:01", BluetoothAddressFormat.Format(1));
    }

    // ---- crypto

    [Fact]
    public void The_full_handshake_agrees_on_a_session_key_both_ways()
    {
        // The device side, with a test key in place of K2.
        using var k2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var k2Q = k2.ExportParameters(false).Q;
        using var crypto = new BluettiCrypto([.. k2Q.X!, .. k2Q.Y!]);

        var seed = Hex("a1b2c3d4");
        crypto.HandleChallenge(BluettiCrypto.BuildKexFrame([0x01, 0x04, .. seed]));
        var iv = MD5.HashData(seed.Reverse().ToArray());
        var key = iv.Zip(BluettiCrypto.StaticAesKey, (a, b) => (byte)(a ^ b)).ToArray();

        using var device = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = device.ExportParameters(false).Q;
        byte[] point = [.. q.X!, .. q.Y!];
        var peerFrame = BluettiCrypto.BuildKexFrame([0x04, 0x80, .. point, .. k2.SignData([.. point, .. iv], HashAlgorithmName.SHA256)]);

        var answer = crypto.Decrypt(BluettiCrypto.EncryptFixedIv(peerFrame, key, iv));
        Assert.Equal(peerFrame, answer);
        var ours = BluettiCrypto.DecryptFixedIv(crypto.HandlePeerPublicKey(answer), key, iv);
        Assert.Equal(5, ours[2]); // our public key

        Assert.False(crypto.IsEstablished);
        Assert.Throws<InvalidOperationException>(() => crypto.Encrypt([1]));
        crypto.HandlePublicKeyAccepted(BluettiCrypto.BuildKexFrame([0x06, 0x01, 0x00]));
        Assert.True(crypto.IsEstablished);

        using var ourKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = ours[4..36], Y = ours[36..68] },
        });
        var session = device.DeriveRawSecretAgreement(ourKey.PublicKey);

        var request = Modbus.BuildReadRequest(102, 18);
        var sent = crypto.Encrypt(request);
        Assert.Equal(6 + 16, sent.Length);
        Assert.Equal(sent.Length, crypto.ExpectedEncryptedLength(sent));
        Assert.Equal(request, BluettiCrypto.DecryptWithSeed(sent, session));
        var reply = BluettiCrypto.EncryptWithSeed(Hex("0102030405"), session, Hex("00000001"));
        Assert.Equal(Hex("0102030405"), crypto.Decrypt(reply));
    }

    [Fact]
    public void Handshake_steps_out_of_order_throw()
    {
        using var crypto = new BluettiCrypto();
        Assert.Throws<InvalidOperationException>(() => crypto.HandlePeerPublicKey(new byte[134]));
        Assert.Throws<InvalidOperationException>(() => crypto.Decrypt(new byte[18]));
        Assert.Throws<InvalidOperationException>(() => crypto.HandlePublicKeyAccepted(BluettiCrypto.BuildKexFrame([0x06, 0x01, 0x00])));
        Assert.Null(crypto.ExpectedEncryptedLength([0x00]));
    }

    [Fact]
    public void Malformed_handshake_frames_throw()
    {
        using var crypto = new BluettiCrypto();
        Assert.Throws<CryptographicException>(() => crypto.HandleChallenge(BluettiCrypto.BuildKexFrame([0x01, 0x03, 1, 2, 3])));
        crypto.HandleChallenge(BluettiCrypto.BuildKexFrame([0x01, 0x04, 1, 2, 3, 4]));
        Assert.Throws<CryptographicException>(() => crypto.HandlePeerPublicKey(BluettiCrypto.BuildKexFrame([0x04, 0x02, 1, 2])));
        Assert.Throws<CryptographicException>(() => crypto.HandlePublicKeyAccepted(BluettiCrypto.BuildKexFrame([0x06, 0x01, 0x01])));
        Assert.Throws<CryptographicException>(() => BluettiCrypto.DecryptWithSeed(Hex("0001000000000102"), new byte[32]));
    }

    [Fact]
    public void Reset_forgets_the_keys()
    {
        using var crypto = new BluettiCrypto();
        crypto.HandleChallenge(BluettiCrypto.BuildKexFrame([0x01, 0x04, 1, 2, 3, 4]));
        crypto.Reset();
        Assert.False(crypto.HasTemporaryKey);
        Assert.False(crypto.IsEstablished);
    }

    [Theory]
    [InlineData("2a2a01040000", true)]
    [InlineData("2a2a010400", false)]
    [InlineData("2a2b01040000", false)]
    public void Kex_frames_start_with_two_stars(string hex, bool expected)
    {
        Assert.Equal(expected, BluettiCrypto.IsKexFrame(Hex(hex)));
    }
}
