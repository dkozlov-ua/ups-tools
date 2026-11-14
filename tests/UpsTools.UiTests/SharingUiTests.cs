using System.Text.Json.Nodes;
using System.Windows.Automation;
using UpsTools.UiTests.Support;

namespace UpsTools.UiTests;

/// <summary>
/// Copies of the app sharing on the network (here: on this PC only) with the same code and the same simulated UPS:
/// only one connects to it, the others read it through that one, and find it on their own.
/// </summary>
public sealed class SharingUiTests : IDisposable
{
    private const string Code = "7KQ2-M9XD-4FTB-R1WE";

    // Below the range Windows reserves ports in.
    private readonly int _port = Random.Shared.Next(40000, 47000);
    private readonly AppUnderTest _first = new();
    private readonly AppUnderTest _second = new();

    public void Dispose()
    {
        _second.Dispose();
        _first.Dispose();
    }

    private void Share(JsonObject settings) =>
        settings["Sharing"] = new JsonObject { ["Enabled"] = true, ["Code"] = Code, ["Port"] = _port };

    private static string Pc => Environment.MachineName;

    [Fact]
    public void One_copy_connects_and_the_other_reads_through_it_and_takes_over_when_it_exits()
    {
        // Each copy's simulator reports something else: what a copy shows tells which one it came from.
        _first.Simulate(new { GridPresent = true, BatteryPercent = 80, AcOutputW = 120 });
        _second.Simulate(new { GridPresent = true, BatteryPercent = 33, AcOutputW = 120 });
        var lost = _second.Script("lost.ps1");
        _first.Seed(app: Share);
        _second.Seed(app: Share, edit: s => s["Scripts"]!["GridLost"] = new JsonArray(new JsonObject { ["Path"] = lost }));

        _first.Start("--background");
        Ui.Until(() => _first.Log().Contains("connecting over Bluetooth"), "the first copy to connect");
        var window = _second.Start()!;

        window.HasText("BatteryValue", "80 %", TimeSpan.FromSeconds(20));
        Ui.Until(() => window.ById("DeviceSubtitle").Text().EndsWith($"through {Pc}"), "the route on the dashboard");
        Assert.Contains($"{Pc} is connected to Bluetti Simulator: reading it through that PC", _second.Log());
        Assert.DoesNotContain("connecting over Bluetooth", _second.Log());

        var configuration = _second.Tab("Configuration");
        Ui.Until(() => configuration.ById("Neighbours").FindAll(TreeScope.Descendants, Condition.TrueCondition)
            .Cast<AutomationElement>().Any(e => e.Current.Name == $"{Pc}: connected to Bluetti Simulator"), "the other PC in the list");
        Ui.Until(() => configuration.AllById("PairedDetail").Any(e => e.Text().EndsWith($"through {Pc}")), "the route in the list");

        // The outage reaches the second copy through the first, which runs its own script.
        _first.Simulate(new { GridPresent = false, BatteryPercent = 79, AcOutputW = 120 });
        Ui.Until(() => _second.Runs("lost.ps1") == 1, "the second copy's Grid lost script");

        // The first goes away: the second connects itself.
        _first.Dispose();
        _second.Tab("Dashboard").HasText("BatteryValue", "33 %", TimeSpan.FromSeconds(20));
        Assert.Contains("No other PC sharing on the network is connected to Bluetti Simulator: connecting over Bluetooth", _second.Log());
    }

    [Fact]
    public void A_ups_another_copy_is_connected_to_is_offered_in_add_ups()
    {
        _first.Simulate(new { GridPresent = true, BatteryPercent = 80, AcOutputW = 120 });
        _second.Simulate(new { GridPresent = true, BatteryPercent = 33, AcOutputW = 120 });
        _first.Seed(app: Share, edit: s => s["Label"] = "Desk");
        _second.Seed(paired: false, app: Share);

        _first.Start("--background");
        Ui.Until(() => _first.Log().Contains("connecting over Bluetooth"), "the first copy to connect");
        _second.Start();
        var window = _second.Tab("Configuration");
        Ui.Until(() => _second.Log().Contains($"Found {Pc} sharing on the network"), "the first copy to be found");

        window.ByName("Add UPS…").InvokeAsync();
        var dialog = Ui.WindowOf(_second.Pid, "Find UPS");
        var item = Ui.WaitFor(() => dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().FirstOrDefault(i => i.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, $"on {Pc}")) is not null), "the UPS on the network");
        item.Select();
        var pair = dialog.ById("PairButton");
        Ui.Until(() => pair.Current.IsEnabled, "Pair to be enabled");
        pair.Invoke();
        Ui.Until(() => dialog.Current.IsOffscreen, "the dialog to close");

        // Paired with the first copy's name for it, and read through it.
        Ui.Until(() => _second.UpsCount() == 1 && (string?)_second.Ups(0)["Label"] == "Desk", "the UPS to be saved");
        _second.Tab("Dashboard").HasText("BatteryValue", "80 %", TimeSpan.FromSeconds(20));
        Assert.DoesNotContain("connecting over Bluetooth", _second.Log());
    }
}
