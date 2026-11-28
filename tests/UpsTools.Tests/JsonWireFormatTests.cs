using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using UpsTools.Core;
using UpsTools.Core.EcoFlow;
using UpsTools.Core.Sharing;

namespace UpsTools.Tests;

/// <summary>
/// The JSON that goes over the network is written by serializers generated at build time (so that the trimmed
/// Android app can write it): it must be byte for byte what reflection wrote before, or a PC and a phone of different
/// versions wouldn't understand each other.
/// </summary>
public class JsonWireFormatTests
{
    /// <summary>The options as they were, through reflection.</summary>
    private static readonly JsonSerializerOptions Reflection = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        IgnoreReadOnlyProperties = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly PairedUps Device = new("ecoflow-ble", "E4:B3:23:8E:7B:3E", "R631ZE1AWH5K2080", "River 3 Plus");

    private static readonly UpsStatus Status = new()
    {
        Timestamp = new DateTimeOffset(2026, 11, 28, 13, 10, 0, TimeSpan.FromHours(2)),
        Model = "River 3 Plus",
        Serial = "R631ZE1AWH5K2080",
        GridPresent = true,
        BatteryPercent = 99,
        CapacityWh = 572,
        AcInputW = 160.5,
        AcOutputW = 160,
        AverageOutputW = 158.25,
    };

    public static TheoryData<object> Messages() =>
    [
        new SharingRequest { Op = SharingRequest.Next, From = "Galaxy S22", Ups = SharingHub.KeyOf(Device), Run = "r1", Seq = 42, WaitSeconds = 90 },
        new SharingRequest { Op = SharingRequest.List },
        new SharingResponse { Run = "r1", Seq = 43, Status = Status, PeriodSeconds = 60 },
        new SharingResponse { Missing = true },
        new SharingResponse { Error = "out of range", Seq = 2, Run = "r" },
        new SharingResponse { Pc = new SharedPc("DESK", "abc", [new SharedUps(SharingHub.KeyOf(Device), "Desk", Device)]) },
        new Announcement.Header("abc", "Galaxy S22"),
        new Announcement.Body(47870, 1_790_000_000_000, [new AnnouncedUps("k", "Desk", Device, UpsRole.Holding, Online: true),
            new AnnouncedUps("k2", "Rack", Device with { UserId = null }, UpsRole.Wanting)], Probe: true, Bye: false),
    ];

    [Theory]
    [MemberData(nameof(Messages))]
    public void Each_message_is_written_as_reflection_wrote_it_and_reads_back(object message)
    {
        var type = message.GetType();
        var generated = JsonSerializer.SerializeToUtf8Bytes(message, SharingJson.Options.GetTypeInfo(type));
        var reflected = JsonSerializer.SerializeToUtf8Bytes(message, type, Reflection);
        Assert.Equal(Encoding.UTF8.GetString(reflected), Encoding.UTF8.GetString(generated));

        // What an older copy wrote reads the same, too.
        var read = JsonSerializer.Deserialize(reflected, SharingJson.Options.GetTypeInfo(type));
        Assert.Equal(Encoding.UTF8.GetString(reflected),
            Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(read, SharingJson.Options.GetTypeInfo(type))));
    }

    [Fact]
    public void The_user_id_of_the_pairing_device_stays_on_it()
    {
        var json = Encoding.UTF8.GetString(SharingJson.Write(new SharingResponse
        {
            Pc = new SharedPc("DESK", "abc", [new SharedUps("k", "Desk", Device with { UserId = null })]),
        }));
        // The account stays on the device connected to it, and the brand and model are worked out, not sent.
        Assert.Contains("\"Device\":{\"DriverId\":\"ecoflow-ble\",\"Address\":\"E4:B3:23:8E:7B:3E\",\"Name\":\"R631ZE1AWH5K2080\",\"Model\":\"River 3 Plus\"}", json);
    }

    [Fact]
    public void The_ecoflow_sign_in_sends_what_it_did()
    {
        var before = JsonSerializer.Serialize(new
        {
            email = "me@example.com",
            password = Convert.ToBase64String("pä ss"u8.ToArray()),
            scene = "IOT_APP",
            appVersion = "1.0.0",
            oauth = new { bundleId = "com.ef.EcoFlow" },
            userType = "ECOFLOW",
        });
        Assert.Equal(before, EcoFlowAccount.RequestJson("  me@example.com ", "pä ss"));
    }
}
