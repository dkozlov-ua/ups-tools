using System.Text.Json;

namespace UpsTools.Core;

/// <summary>
/// A UPS that exists only as a JSON file, for trying UPS Tools without hardware and for its UI tests.
/// With the environment variable <c>UPSTOOLS_SIMULATOR</c> set to the file's path, it is the only driver.
/// </summary>
/// <remarks>
/// Every poll reads the file again, so editing it changes the UPS. The file holds a
/// <see cref="SimulatedReading"/>, e.g. <c>{"GridPresent": false, "BatteryPercent": 80, "AcOutputW": 120}</c>;
/// <c>"Fail": "message"</c> makes polls fail. A missing file is a UPS that doesn't answer and can't be found.
/// </remarks>
public sealed class SimulatedUpsDriver(string path) : IUpsDriver
{
    public const string EnvironmentVariable = "UPSTOOLS_SIMULATOR";
    public const string DriverId = "simulated";
    public const string Address = "00:00:00:00:00:01";
    public const string ModelName = "Simulator";
    public const string DeviceName = "SIMULATOR0001";

    private static readonly TimeSpan ScanTime = TimeSpan.FromMilliseconds(300);

    public string Path { get; } = path;
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
        if (File.Exists(Path)) onFound(new DiscoveredUps(DriverId, Address, DeviceName, ModelName, -42));
    }

    public IUpsConnection CreateConnection(PairedUps device) => new Connection(Path);

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
        public double? ReportedRuntimeMinutes { get; init; }

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
            ReportedRuntime = ReportedRuntimeMinutes is { } m ? TimeSpan.FromMinutes(m) : null,
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
