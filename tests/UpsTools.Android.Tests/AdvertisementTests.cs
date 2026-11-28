using System.Text;
using UpsTools.Core;
using UpsTools.Core.Bluetti;
using UpsTools.Core.EcoFlow;
using UpsTools.Droid.Tests.Support;

namespace UpsTools.Droid.Tests;

/// <summary>Advertisements as Android hands them over (raw AD structures), read into what the drivers find.</summary>
public class AdvertisementTests
{
    private static byte[] Ad(byte type, byte[] data) => [(byte)(data.Length + 1), type, .. data];

    private static async Task<List<DiscoveredUps>> Discover(IUpsDriver driver)
    {
        var found = new List<DiscoveredUps>();
        await driver.DiscoverAsync(found.Add, TimeSpan.Zero, default);
        return found;
    }

    [Fact]
    public async Task A_bluetti_is_found_by_its_name()
    {
        byte[] raw = [.. Ad(0x01, [0x06]), .. Ad(0x09, Encoding.ASCII.GetBytes("EL100V22527008220279")), 0, 0, 0];
        var found = await Discover(new BluettiBleDriver(ble: new FakeBle((0x0C4EA0E081E6, -61, raw))));

        var ups = Assert.Single(found);
        Assert.Equal(("EL100V2", "EL100V22527008220279", "0C:4E:A0:E0:81:E6", (short?)-61), (ups.Model, ups.Name, ups.Address, ups.Rssi));
    }

    [Fact]
    public async Task An_ecoflow_is_found_by_its_manufacturer_data()
    {
        // The manufacturer data (company 0xB5B5, little-endian first) of a real River 3 Plus.
        byte[] raw = Ad(0xFF, [0xB5, 0xB5, .. Convert.FromHexString("13523633315A4531415748354B3230383060000100003E42")]);
        var found = await Discover(new EcoFlowBleDriver(ble: new FakeBle((1, -70, raw))));

        var ups = Assert.Single(found);
        Assert.Equal(("River 3 Plus", "R631ZE1AWH5K2080"), (ups.Model, ups.Name));
    }

    [Fact]
    public void The_complete_name_wins_over_the_shortened_one()
    {
        var ad = AdvertisementParser.Parse(1, 0, [.. Ad(0x08, "EL1"u8.ToArray()), .. Ad(0x09, "EL100V2X"u8.ToArray())]);
        Assert.Equal("EL100V2X", ad.LocalName);

        Assert.Equal("EL1", AdvertisementParser.Parse(1, 0, Ad(0x08, "EL1"u8.ToArray())).LocalName);
    }

    [Fact]
    public void A_broken_structure_ends_the_reading_without_failing()
    {
        byte[] raw = [.. Ad(0x09, "EL10"u8.ToArray()), 0x09, 0xFF, 0x01];
        var ad = AdvertisementParser.Parse(1, 0, raw);
        Assert.Equal("EL10", ad.LocalName);
        Assert.Empty(ad.ManufacturerData);

        Assert.Null(AdvertisementParser.Parse(1, 0, []).LocalName);
    }

    [Fact]
    public void Each_manufacturer_section_is_kept_with_its_company()
    {
        var ad = AdvertisementParser.Parse(1, 0, [.. Ad(0xFF, [0x34, 0x12, 7, 8]), .. Ad(0xFF, [0xB5, 0xB5, 9])]);
        Assert.Equal([7, 8], Assert.Single(ad.ManufacturerDataOf(0x1234)));
        Assert.Equal([9], Assert.Single(ad.ManufacturerDataOf(0xB5B5)));
    }
}
