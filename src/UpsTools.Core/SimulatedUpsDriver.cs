using System.Text.Json;

namespace UpsTools.Core;

/// <summary>
/// UPSes that exist only as JSON files, for trying UPS Tools without hardware and for its UI tests.
/// With the environment variable <c>UPSTOOLS_SIMULATOR</c> set to a file's path, or to several separated by
/// <c>;</c> (one UPS each), it is the only driver.
/// </summary>
/// <remarks>
/// Every poll reads the file again, so editing it changes the UPS. The file holds a
/// <see cref="SimulatedReading"/>, e.g. <c>{"GridPresent": false, "BatteryPercent": 80, "AcOutputW": 120}</c>;
/// <c>"Fail": "message"</c> makes polls fail. A missing file is a UPS that doesn't answer and can't be found.
/// The first file's UPS is <see cref="Address"/> and <see cref="DeviceName"/>; the second one's ends in 02, and so on.
/// </remarks>
public sealed class SimulatedUpsDriver : IUpsDriver
{
    public const string EnvironmentVariable = "UPSTOOLS_SIMULATOR";
    public const string DriverId = "simulated";
    public const string Address = "00:00:00:00:00:01";
    public const string ModelName = "Simulator";
    public const string DeviceName = "SIMULATOR0001";

    private static readonly TimeSpan ScanTime = TimeSpan.FromMilliseconds(300);

    /// <param name="path">The file of each UPS, separated by <c>;</c>.</param>
    public SimulatedUpsDriver(string path) =>
        Paths = path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The first UPS's file.</summary>
    public string Path => Paths[0];

    public IReadOnlyList<string> Paths { get; }

    /// <summary>The address of the UPS of the file at <paramref name="index"/> (from 0).</summary>
    public static string AddressOf(int index) => $"00:00:00:00:00:{index + 1:X2}";

    public static string NameOf(int index) => $"SIMULATOR{index + 1:0000}";

    public string Id => DriverId;
    public string DisplayName => "Simulated UPS";

    /// <summary>The simulator, when <c>UPSTOOLS_SIMULATOR</c> names its file.</summary>
    public static SimulatedUpsDriver? FromEnvironment() =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } file ? new SimulatedUpsDriver(file) : null;

    public async Task DiscoverAsync(Action<DiscoveredUps> onFound, TimeSpan duration, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration < ScanTime ? duration : ScanTime, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        for (var i = 0; i < Paths.Count; i++)
            if (File.Exists(Paths[i])) onFound(new DiscoveredUps(DriverId, AddressOf(i), NameOf(i), ModelName, -42));
    }

    /// <summary>A UPS of no file (e.g. one more than <c>UPSTOOLS_SIMULATOR</c> names now) doesn't answer.</summary>
    public IUpsConnection CreateConnection(PairedUps device)
    {
        var index = Enumerable.Range(0, Paths.Count).FirstOrDefault(i => AddressOf(i) == device.Address, -1);
        return new Connection(index >= 0 ? Paths[index] : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-such-simulated-ups.json"));
    }

    /// <summary>What the file says. Null values are readings the UPS doesn't report.</summary>
    public sealed record SimulatedReading
    {
        /// <summary>Polls fail with this message.</summary>
        public string? Fail { get; init; }

        /// <summary>Each poll takes this long.</summary>
        public int DelayMs { get; init; }

        public string? Serial { get; init; } = "SIM0001";
        public bool? GridPresent { get; init; } = true;
        public int? BatteryPercent { get; init; } = 100;
        public double? CapacityWh { get; init; } = 1024;
        public double? AcInputVoltage { get; init; }
        public double? AcOutputVoltage { get; init; }
        public double? AcInputW { get; init; }
        public double? DcInputW { get; init; }
        public double? AcOutputW { get; init; }
        public double? DcOutputW { get; init; }

        public UpsStatus ToStatus(DateTimeOffset timestamp) => new()
        {
            Timestamp = timestamp,
            Model = ModelName,
            Serial = Serial,
            GridPresent = GridPresent,
            BatteryPercent = BatteryPercent,
            CapacityWh = CapacityWh,
            AcInputVoltage = AcInputVoltage,
            AcOutputVoltage = AcOutputVoltage,
            AcInputW = AcInputW,
            DcInputW = DcInputW,
            AcOutputW = AcOutputW,
            DcOutputW = DcOutputW,
        };
    }

    private sealed class Connection(string path) : IUpsConnection
    {
        public async Task<UpsStatus> ReadAsync(CancellationToken cancellationToken)
        {
            var reading = Read(path);
            if (reading.DelayMs > 0) await Task.Delay(reading.DelayMs, cancellationToken);
            if (reading.Fail is { } message) throw new IOException(message);
            return reading.ToStatus(DateTimeOffset.Now);
        }

        public Task ResetAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static SimulatedReading Read(string path)
    {
        string json;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new IOException("Simulated UPS not found");
        }
        return JsonSerializer.Deserialize<SimulatedReading>(json) ?? throw new IOException("The simulator file is empty");
    }
}
