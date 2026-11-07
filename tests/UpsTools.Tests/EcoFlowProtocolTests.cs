using System.Security.Cryptography;
using System.Text;
using UpsTools.Core;
using UpsTools.Core.EcoFlow;
using static UpsTools.Tests.Support.TestData;

namespace UpsTools.Tests;

/// <summary>
/// The EcoFlow protocol pieces against reference vectors: bytes produced by ha-ef-ble's own algorithms, packets captured
/// from a real River 3, and the advertisement of a real River 3 Plus.
/// </summary>
public class EcoFlowProtocolTests
{
    /// <summary>DisplayPropertyUpload packets captured from a River 3 UPS (from ha-ef-ble's tests), in the order sent.</summary>
    internal static readonly string[] RealRiver3Uploads =
    [
        "aa132c01292c7537e009014502210101fe157d75687575213750757521374d74353e3875757575207575757528757575751075757575fd74d57ee5747fed7475c57475cd747abd7475d87775757575d87675757575c0760c324c37a57175987175757575ed7c74d57c75807c757575b5ed7974ed7847d57875af7875ed7b9374e07a7575e337e87a7575bd37b57af511857aaa688d7a8668e56555ed6554d5656cdd656cc0657575e337c8657575bd379565aa689d65866885652f8d657fed6474bd6475a564759d6475856475ed6375dd6375c56375cd6347b86375757575f0620c324cb7b86f757575758f69257f737d776580847e7f777d767f717d7165557f777d707f717d7365707f777d727f757f757f757f757f757f757f757f757f757f757f757f757f757f757f757f757f757f757f757f757f757f757f757f753e0a",
        "aa135f00b32c7e37e009014502210101fe158e7f7ee67b7ede7b7ed67b7ece7b7ec67b7ebc7b7eb67b7eae7b7ea47b7e9e767e94765d747d76a07a747d76a07a747d76a07a747d76a67d747d76ef7d747e747e747e747e747ed6737ec6737ebe707efc657eee657ede657ece657ec6657e22b0",
        "aa136500c82c8a37e009014502210101fe15e28afa8af28a0a8b8a02888a62888a7a888a72888862898a7a898a7a8e8a728e8a4a83944283945a868b0287b87a8426882a9c8c4a9c8a5a9c8a529c8b6a9c8a629c8a7a9c8a729c8832908a4a908a5a908a52908a6a908a72908a00918a12918a5a963b881f06",
    ];

    private static readonly EcoFlowModel River3Plus = EcoFlowModels.FromSerial("R631ZE1AWH5K2080")!;

    [Fact]
    public void The_checksums_are_crc8_and_crc16_arc()
    {
        var check = "123456789"u8;
        Assert.Equal(0xF4, EcoFlowCrc.Crc8(check));
        Assert.Equal(0xBB3D, EcoFlowCrc.Crc16(check));
    }

    [Fact]
    public void Packets_are_encoded_as_ha_ef_ble_does()
    {
        Assert.Equal("aa030000de0d00000000000021350101358930e4",
            Convert.ToHexString(new EcoFlowPacket(0x21, 0x35, 0x35, 0x89, []).ToBytes()).ToLowerInvariant());

        var token = Encoding.ASCII.GetBytes("7B2721F1D37703A3BB28D7CCFEBA07B8"); // MD5 of "1234567890" + "R631ZE1AWH5K2080"
        Assert.Equal("7B2721F1D37703A3BB28D7CCFEBA07B8",
            Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes("1234567890R631ZE1AWH5K2080"))));
        Assert.Equal("aa032000700d000000000000213501013586374232373231463144333737303341334242323844374343464542413037423829c1",
            Convert.ToHexString(new EcoFlowPacket(0x21, 0x35, 0x35, 0x86, token).ToBytes()).ToLowerInvariant());
    }

    [Fact]
    public void Frames_are_encoded_as_ha_ef_ble_does()
    {
        Assert.Equal("5a5a000103000286c2", Convert.ToHexString(new EcoFlowFrame(EcoFlowFrame.Command, [0x02]).ToBytes()).ToLowerInvariant());
    }

    [Fact]
    public void A_packet_reads_back_as_it_was_written()
    {
        var sent = new EcoFlowPacket(0x02, 0x21, 0xFE, 0x15, [1, 2, 3], 0x03) { Dsrc = 3, Ddst = 4, Seq = 0x01020304 };

        var read = EcoFlowPacket.Parse(sent.ToBytes());

        // The device XORs payloads with the first byte of the sequence number, and so does Parse in reverse.
        Assert.Equal((0x02, 0x21, 0xFE, 0x15, 3, 4), (read.Src, read.Dst, read.CmdSet, read.CmdId, read.Dsrc, read.Ddst));
        Assert.Equal([1 ^ 4, 2 ^ 4, 3 ^ 4], read.Payload);
    }

    [Fact]
    public void A_damaged_packet_is_refused()
    {
        var bytes = new EcoFlowPacket(0x21, 0x35, 0x35, 0x89, [1, 2]).ToBytes();
        bytes[^3] ^= 1;
        Assert.Throws<FormatException>(() => EcoFlowPacket.Parse(bytes));
        Assert.Throws<FormatException>(() => EcoFlowPacket.Parse([0xAA, 0x04, 0, 0, 0]));
        Assert.Throws<FormatException>(() => EcoFlowPacket.Parse([1, 2, 3]));
    }

    [Fact]
    public void A_real_river_3_upload_is_read()
    {
        var packet = EcoFlowPacket.Parse(Hex(RealRiver3Uploads[0]));
        Assert.Equal((0x02, 0x21, 0xFE, 0x15), (packet.Src, packet.Dst, packet.CmdSet, packet.CmdId));

        var fields = new ProtoFields();
        fields.Merge(packet.Payload);

        Assert.Equal(54, fields.Count); // and two strings or messages
        Assert.Equal(75, fields.Double(EcoFlowStatusParser.CmsBattSoc));
        Assert.Equal(46.32, fields.Double(EcoFlowStatusParser.PowGetAcIn)!.Value, 2);
        Assert.Equal(-46.32, fields.Double(EcoFlowStatusParser.PowGetAcOut)!.Value, 2);
    }

    [Fact]
    public void Uploads_in_parts_make_one_status()
    {
        var fields = new ProtoFields();
        foreach (var hex in RealRiver3Uploads) fields.Merge(EcoFlowPacket.Parse(Hex(hex)).Payload);

        var status = EcoFlowStatusParser.Parse(River3Plus, "R631ZE1AWH5K2080", fields, DateTimeOffset.UnixEpoch);

        Assert.Equal("River 3 Plus", status.Model);
        Assert.Equal("R631ZE1AWH5K2080", status.Serial);
        Assert.Equal(75, status.BatteryPercent);
        Assert.Equal(286, status.CapacityWh); // the model's: this River 3 doesn't report its energy
        Assert.True(status.GridPresent); // the third part says the AC input is plugged in
        Assert.Equal(46.3, status.AcInputW);
        Assert.Equal(46.3, status.AcOutputW);
        Assert.Equal(0, status.DcInputW);
        Assert.Equal(0, status.DcOutputW);
        Assert.Null(status.AcInputVoltage);
    }

    [Fact]
    public void Grid_comes_from_the_input_voltage_then_the_plug_then_the_input_power()
    {
        UpsStatus Status(params (int Field, float Value)[] values)
        {
            var fields = new ProtoFields();
            foreach (var (field, value) in values) fields.Merge(Float(field, value));
            return EcoFlowStatusParser.Parse(River3Plus, "R631X", fields, DateTimeOffset.UnixEpoch);
        }

        Assert.False(Status((EcoFlowStatusParser.AcInVoltage, 0), (EcoFlowStatusParser.PowGetAcIn, 50)).GridPresent);
        Assert.True(Status((EcoFlowStatusParser.AcInVoltage, 230), (EcoFlowStatusParser.PowGetAcIn, 0)).GridPresent);

        var unplugged = new ProtoFields();
        unplugged.Merge([.. Float(EcoFlowStatusParser.PowGetAcIn, 50), .. ProtoFields.EncodeVarint(EcoFlowStatusParser.AcChargerFlag, 0)]);
        Assert.False(EcoFlowStatusParser.Parse(River3Plus, "R631X", unplugged, DateTimeOffset.UnixEpoch).GridPresent);

        Assert.True(Status((EcoFlowStatusParser.PowGetAcIn, 50)).GridPresent);
        Assert.False(Status((EcoFlowStatusParser.PowGetAcIn, 0)).GridPresent);
        Assert.Null(Status((EcoFlowStatusParser.CmsBattSoc, 50)).GridPresent);
    }

    [Fact]
    public void The_reported_energy_and_the_outputs_make_the_runtime()
    {
        var fields = new ProtoFields();
        fields.Merge([
            .. Float(EcoFlowStatusParser.CmsBattSoc, 49.6f),
            .. ProtoFields.EncodeVarint(EcoFlowStatusParser.CmsBattFullEnergy, 572), // with an extra battery
            .. Float(EcoFlowStatusParser.PowGetAcOut, -100),
            .. Float(EcoFlowStatusParser.PowGetTypeC1, -30),
            .. Float(EcoFlowStatusParser.PowGet12V, -13),
        ]);

        var status = EcoFlowStatusParser.Parse(River3Plus, "R631X", fields, DateTimeOffset.UnixEpoch);

        Assert.Equal((50, 572.0, 100.0, 43.0), (status.BatteryPercent, status.CapacityWh, status.AcOutputW, status.DcOutputW));
        Assert.Equal(TimeSpan.FromHours(572 * 0.5 / 143), status.EstimatedRuntime);
    }

    [Fact]
    public void An_extra_battery_adds_its_capacity()
    {
        // As a real River 3 Plus with its Smart Extra Battery sends them (2026-11-07): plugged in, serial, empty second slot.
        var fields = new ProtoFields();
        fields.Merge([
            .. Float(EcoFlowStatusParser.CmsBattSoc, 96),
            .. ProtoFields.EncodeVarint(EcoFlowStatusParser.ExtraBatteryFlag, 1),
            .. Text(EcoFlowStatusParser.ExtraBatterySerial, "R633ZEBAWH3E0122"),
            .. ProtoFields.EncodeVarint(EcoFlowStatusParser.ExtraBattery2Flag, 0),
            .. Text(EcoFlowStatusParser.ExtraBattery2Serial, ""),
        ]);
        Assert.Equal(572, EcoFlowStatusParser.Parse(River3Plus, "R631X", fields, DateTimeOffset.UnixEpoch).CapacityWh);

        // One not listed counts as big as the device; unplugged, none.
        fields.Merge([.. ProtoFields.EncodeVarint(EcoFlowStatusParser.ExtraBattery2Flag, 1), .. Text(EcoFlowStatusParser.ExtraBattery2Serial, "R699X")]);
        Assert.Equal(858, EcoFlowStatusParser.Parse(River3Plus, "R631X", fields, DateTimeOffset.UnixEpoch).CapacityWh);
        fields.Merge([.. ProtoFields.EncodeVarint(EcoFlowStatusParser.ExtraBatteryFlag, 0), .. ProtoFields.EncodeVarint(EcoFlowStatusParser.ExtraBattery2Flag, 0)]);
        Assert.Equal(286, EcoFlowStatusParser.Parse(River3Plus, "R631X", fields, DateTimeOffset.UnixEpoch).CapacityWh);
    }

    [Fact]
    public void Protobuf_fields_are_read_by_wire_type()
    {
        var fields = new ProtoFields();
        fields.Merge([
            .. ProtoFields.EncodeVarint(300, 1234567), // varint with a multi-byte key
            .. Float(5, 1.5f),
            0x32, 3, (byte)'a', (byte)'b', (byte)'c', // field 6: a string
            0x39, .. BitConverter.GetBytes(2.25), // field 7: a double
        ]);

        Assert.Equal(1234567, fields.Integer(300));
        Assert.Equal(1.5, fields.Double(5));
        Assert.Equal(2, fields.Integer(5));
        Assert.Null(fields[6]);
        Assert.Equal("abc"u8.ToArray(), fields.Bytes(6));
        Assert.Equal(2.25, fields.Double(7));

        fields.Merge(Float(5, 3));
        Assert.Equal((3.0, 1234567L), (fields.Double(5)!.Value, fields.Integer(300)!.Value));

        Assert.Throws<FormatException>(() => fields.Merge([0x08])); // ends inside a varint
        Assert.Throws<FormatException>(() => fields.Merge([0x0D, 1, 2])); // ends inside a fixed32
        Assert.Throws<FormatException>(() => fields.Merge([0x0B])); // wire type 3
    }

    [Fact]
    public void Frames_are_found_across_notifications_and_noise()
    {
        var a = new EcoFlowFrame(EcoFlowFrame.Protocol, [1, 2, 3, 0x5A, 0x5A, 9]).ToBytes();
        var b = new EcoFlowFrame(EcoFlowFrame.Command, [0x02]).ToBytes();
        var damaged = new EcoFlowFrame(EcoFlowFrame.Protocol, [7, 7]).ToBytes();
        damaged[^1] ^= 0xFF;
        byte[] stream = [0x00, 0x5A, .. a, .. damaged, .. b];

        var reader = new EcoFlowFrameReader();
        var frames = new List<EcoFlowFrame>();
        foreach (var chunk in stream.Chunk(5)) frames.AddRange(reader.Add(chunk));

        Assert.Equal(2, frames.Count);
        Assert.Equal((EcoFlowFrame.Protocol, "0102035A5A09"), (frames[0].Type, Convert.ToHexString(frames[0].Payload)));
        Assert.Equal((EcoFlowFrame.Command, "02"), (frames[1].Type, Convert.ToHexString(frames[1].Payload)));
    }

    [Theory]
    [InlineData("0a02", "1a9456575597ba7879bf11dfa1a6a772")]
    [InlineData("0001", "cf19aab35c7605235e885521945354d8")]
    public void The_session_key_is_derived_as_ha_ef_ble_does(string seed, string key)
    {
        var random = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        Assert.Equal(key, Convert.ToHexString(EcoFlowCrypto.SessionKey(Hex(seed), random)).ToLowerInvariant());
    }

    [Fact]
    public void A_seed_beyond_the_key_table_is_refused()
    {
        Assert.Equal(65280, EcoFlowKeyData.Table.Length);
        Assert.Throws<IOException>(() => EcoFlowCrypto.SessionKey([0xFF, 0x00], new byte[16]));
    }

    [Fact]
    public void Our_public_key_is_a_point_on_secp160r1()
    {
        using var crypto = new EcoFlowCrypto();
        for (var i = 0; i < 20; i++)
        {
            var hello = crypto.StartKeyExchange();
            Assert.Equal(42, hello.Length);
            Assert.Equal([0x01, 0x00], hello[..2]);
            Assert.True(EcoFlowCrypto.OnCurve(hello[2..22], hello[22..42]));
        }
    }

    [Fact]
    public void The_real_advertisement_names_a_river_3_plus()
    {
        // Manufacturer data (company 0xB5B5) of a real River 3 Plus.
        var serial = EcoFlowBleDriver.SerialFromAdvertisement(Hex("13523633315A4531415748354B3230383060000100003E42"));

        Assert.Equal("R631ZE1AWH5K2080", serial);
        Assert.Equal("River 3 Plus", EcoFlowModels.FromSerial(serial)!.Name);
        Assert.Null(EcoFlowBleDriver.SerialFromAdvertisement(Hex("1300000000000000000000000000000000C4")));
        Assert.Null(EcoFlowBleDriver.SerialFromAdvertisement([0x13, 0x52]));
    }

    [Theory]
    [InlineData("R631ZE1AWH5K2080", "River 3 Plus")]
    [InlineData("r635abc", "River 3 Plus Wireless")]
    [InlineData("R655TEST1234", "River 3 UPS")]
    [InlineData("R634X", "River 3 Plus (270)")]
    [InlineData("P231X", null)]
    [InlineData("R6", null)]
    public void Models_are_known_by_the_start_of_the_serial(string serial, string? model)
    {
        Assert.Equal(model, EcoFlowModels.FromSerial(serial)?.Name);
    }

    [Fact]
    public void The_user_id_is_read_from_the_sign_in_response()
    {
        Assert.Equal("1234567890123456789",
            EcoFlowAccount.UserIdFromResponse("""{"code":"0","message":"Success","data":{"user":{"userId":"1234567890123456789","email":"a@b.c"},"token":"x"}}"""));
        Assert.Equal("42", EcoFlowAccount.UserIdFromResponse("""{"code":"0","data":{"user":{"userId":42}}}"""));

        var refused = Assert.Throws<IOException>(() =>
            EcoFlowAccount.UserIdFromResponse("""{"code":"2026","message":"Incorrect password"}"""));
        Assert.Equal("EcoFlow sign-in failed: Incorrect password", refused.Message);
        Assert.Throws<IOException>(() => EcoFlowAccount.UserIdFromResponse("""{"code":"0","data":{}}"""));
    }

    [Fact]
    public void The_driver_needs_a_user_id_and_a_known_serial()
    {
        var driver = new EcoFlowBleDriver();
        Assert.True(driver.RequiresUserId);
        Assert.Equal("ecoflow-ble", driver.Id);

        var known = new PairedUps(EcoFlowBleDriver.DriverId, "E4:B3:23:8E:7B:3E", "R631ZE1AWH5K2080", "River 3 Plus");
        Assert.Throws<InvalidOperationException>(() => driver.CreateConnection(known));
        Assert.Throws<NotSupportedException>(() => driver.CreateConnection(known with { Name = "P231X", UserId = "1" }));
        Assert.Equal("EcoFlow River 3 Plus", known.Title);
    }

    private static byte[] Text(int field, string value) =>
        [.. ProtoFields.EncodeVarint(1, ((ulong)field << 3) | 2)[1..], (byte)value.Length, .. Encoding.ASCII.GetBytes(value)];

    /// <summary>A float field: its key (wire type 5) and four bytes.</summary>
    internal static byte[] Float(int field, float value) =>
        [.. ProtoFields.EncodeVarint(1, ((ulong)field << 3) | 5)[1..], .. BitConverter.GetBytes(value)]; // [1..]: drop field 1's own key
}
