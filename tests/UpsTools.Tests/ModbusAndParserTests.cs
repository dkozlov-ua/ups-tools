using System.Buffers.Binary;
using UpsTools.Core.Bluetti;

namespace UpsTools.Tests;

public class ModbusAndParserTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    // Raw register dumps captured from a real Elite 100 V2 on grid power (2026-11-03).
    private static readonly Dictionary<RegisterBlock, byte[]> RealEl100V2OnGrid = new()
    {
        [new(102, 18)] = Hex("00640000176a176a00000001000100004c453031563000320000000064775d6d024c0000"),
        [new(140, 8)] = Hex("00000000009d000000000000009d0000"),
        [new(1314, 2)] = Hex("093e0006"),
        [new(1511, 1)] = Hex("0939"),
    };

    private static byte[] ResponseFrame(byte[] payload)
    {
        byte[] frame = [0x01, 0x03, (byte)payload.Length, .. payload, 0, 0];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(^2), Modbus.Crc16(frame.AsSpan(0, frame.Length - 2)));
        return frame;
    }

    [Theory]
    [InlineData(102, 18, "01030066001225d8")]
    [InlineData(140, 8, "0103008c000885e7")]
    public void Read_request_matches_reference(ushort start, ushort count, string expected)
    {
        Assert.Equal(Hex(expected), Modbus.BuildReadRequest(start, count));
    }

    [Fact]
    public void Read_response_is_validated_and_unwrapped()
    {
        var payload = RealEl100V2OnGrid[new(140, 8)];
        var frame = ResponseFrame(payload);

        Assert.Equal(frame.Length, Modbus.ExpectedResponseLength(frame));
        Assert.Equal(payload, Modbus.ParseReadResponse(frame, 8));
    }

    [Fact]
    public void Corrupted_or_exception_responses_throw()
    {
        var frame = ResponseFrame(Hex("0064"));
        frame[3] ^= 0xFF;
        Assert.Throws<ModbusException>(() => Modbus.ParseReadResponse(frame, 1));

        byte[] exception = [0x01, 0x83, 0x02, 0, 0];
        BinaryPrimitives.WriteUInt16LittleEndian(exception.AsSpan(3), Modbus.Crc16(exception.AsSpan(0, 3)));
        Assert.Equal(5, Modbus.ExpectedResponseLength(exception));
        Assert.Throws<ModbusException>(() => Modbus.ParseReadResponse(exception, 1));
    }

    [Fact]
    public void Real_el100v2_dump_parses()
    {
        var model = BluettiModels.FromName("EL100V2")!;
        var status = BluettiStatusParser.Parse(model, RealEl100V2OnGrid, DateTimeOffset.UnixEpoch);

        Assert.Equal("EL100V2", status.Model);
        Assert.Equal("2527008220279", status.Serial);
        Assert.Equal(100, status.BatteryPercent);
        Assert.True(status.GridPresent);
        Assert.Equal(236.6, status.AcInputVoltage!.Value, 3);
        Assert.Equal(236.1, status.AcOutputVoltage!.Value, 3);
        Assert.Equal(157, status.AcInputW);
        Assert.Equal(157, status.AcOutputW);
        Assert.Equal(0, status.DcInputW);
        Assert.Equal(0, status.DcOutputW);
        Assert.Equal(0, status.BatteryFlowW);
        // 1024 Wh at 100 % and 157 W load ≈ 6.5 h
        Assert.Equal(1024.0 / 157, status.EstimatedRuntime!.Value.TotalHours, 3);
    }

    [Fact]
    public void Grid_loss_is_detected_from_input_voltage()
    {
        var model = BluettiModels.FromName("EL100V2")!;
        var blocks = new Dictionary<RegisterBlock, byte[]>(RealEl100V2OnGrid)
        {
            [new(102, 18)] = Hex("004b0000002a002a00000001000100004c453031563000320000000064775d6d024c0000"),
            [new(140, 8)] = Hex("00000000009d00000000000000000000"), // 157 W AC out, nothing in
            [new(1314, 2)] = Hex("00000000"),
        };

        var status = BluettiStatusParser.Parse(model, blocks, DateTimeOffset.UnixEpoch);

        Assert.False(status.GridPresent);
        Assert.Equal(75, status.BatteryPercent);
        Assert.Equal(-157, status.BatteryFlowW);
        // Register 104 says 42 minutes; the runtime comes from 75 % of 1024 Wh at 157 W instead.
        Assert.Equal(1024 * 0.75 / 157, status.EstimatedRuntime!.Value.TotalHours, 3);
    }

    [Fact]
    public void The_upss_own_runtime_estimate_is_ignored()
    {
        // Seen on a real Elite 100 V2 (2026-11-05): 98 %, 223 W out, register 104 = 405 (minutes; 6 h 45 m).
        var model = BluettiModels.FromName("EL100V2")!;
        var blocks = new Dictionary<RegisterBlock, byte[]>(RealEl100V2OnGrid)
        {
            [new(102, 18)] = Hex("0062000001950195" + new string('0', 56)),
            [new(140, 8)] = Hex("0000000000df00000000000000000000"),
            [new(1314, 2)] = Hex("00000000"),
        };

        var status = BluettiStatusParser.Parse(model, blocks, DateTimeOffset.UnixEpoch);

        Assert.Equal(1024 * 0.98 / 223, status.EstimatedRuntime!.Value.TotalHours, 3); // ≈ 4.5 h
    }

    [Theory]
    [InlineData("EL100V22527008220279", "EL100V2")]
    [InlineData("EL1012345", "EL10")]
    [InlineData("AC180P2235000000000", "AC180P")]
    [InlineData("AC1802235000000000", "AC180")]
    [InlineData("WH-1000XM6", null)]
    public void Advertised_name_maps_to_longest_matching_model(string name, string? expected)
    {
        Assert.Equal(expected, BluettiModels.FromAdvertisedName(name)?.Name);
    }

    [Fact]
    public void Bluetooth_address_round_trips()
    {
        var value = BluetoothAddressFormat.Parse("0C:4E:A0:E0:81:E6");
        Assert.Equal(0x0C4EA0E081E6UL, value);
        Assert.Equal("0C:4E:A0:E0:81:E6", BluetoothAddressFormat.Format(value));
    }
}
