using UpsTools.Core.Bluetti;

namespace UpsTools.Tests.Support;

internal static class TestData
{
    public static byte[] Hex(string hex) => Convert.FromHexString(hex);

    /// <summary>Raw register dumps captured from a real Elite 100 V2 on grid power (2026-11-03).</summary>
    public static readonly IReadOnlyDictionary<RegisterBlock, byte[]> RealEl100V2OnGrid = new Dictionary<RegisterBlock, byte[]>
    {
        [new(102, 18)] = Hex("00640000176a176a00000001000100004c453031563000320000000064775d6d024c0000"),
        [new(140, 8)] = Hex("00000000009d000000000000009d0000"),
        [new(1314, 2)] = Hex("093e0006"),
        [new(1511, 1)] = Hex("0939"),
    };

    public static BluettiModel El100V2 => BluettiModels.FromName("EL100V2")!;
}

internal static class Wait
{
    /// <summary>Waits until <paramref name="condition"/> holds; fails the test after <paramref name="seconds"/>.</summary>
    public static async Task Until(Func<bool> condition, double seconds = 10, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what ?? "the condition"}");
            await Task.Delay(10);
        }
    }
}
