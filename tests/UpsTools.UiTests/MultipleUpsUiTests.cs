using System.Text.Json.Nodes;
using System.Windows.Automation;
using UpsTools.UiTests.Support;

namespace UpsTools.UiTests;

/// <summary>Two simulated UPSes, monitored at once, each with its own scripts and settings.</summary>
public sealed class MultipleUpsUiTests : IDisposable
{
    private readonly AppUnderTest _app = new();

    public void Dispose() => _app.Dispose();

    private static JsonArray Scripts(string path) => new(new JsonObject { ["Path"] = path });

    [Fact]
    public void Both_are_on_the_dashboard_and_each_runs_its_own_scripts()
    {
        var desk = _app.Script("desk.ps1");
        var office = _app.Script("office.ps1");
        _app.SimulateSecond(new { GridPresent = true, BatteryPercent = 40, Serial = "SIM0002", AcOutputW = 60 });
        _app.Seed(s => s["Scripts"]!["GridLost"] = Scripts(desk), second: true, editSecond: s =>
        {
            s["Label"] = "Office";
            s["Scripts"]!["GridLost"] = Scripts(office);
        });
        var window = _app.Start()!;

        // Each at a glance; the first one in full.
        var tiles = window.ById("UpsTiles");
        Ui.Until(() => tiles.AllById("UpsSummary").Select(t => t.Text()).SequenceEqual(
            ["Grid online · 80 % · 120 W load", "Grid online · 40 % · 60 W load"]), "both UPSes online");
        window.HasText("DeviceTitle", "Bluetti Simulator");
        window.HasText("BatteryValue", "80 %");

        tiles.ByName("Office").Select();
        window.HasText("DeviceTitle", "Office");
        window.HasText("DeviceSubtitle", "Bluetti Simulator · Serial SIM0002 · 00:00:00:00:00:02");
        window.HasText("BatteryValue", "40 %");

        // The tabs follow the UPS picked.
        var scripts = _app.Tab("Scripts");
        scripts.HasText("ScriptsTitle", "Scripts of Office");
        scripts.ByName("office.ps1");
        Assert.Null(scripts.TryByName("desk.ps1"));

        // The office's outage runs the office's script only.
        _app.SimulateSecond(new { GridPresent = false, BatteryPercent = 39, Serial = "SIM0002", AcOutputW = 60 });
        Ui.Until(() => _app.Runs("office.ps1") == 1, "the office's script");
        Ui.Until(() => _app.Log().Contains("Office: Grid lost"), "the event in the log");
        Thread.Sleep(500);
        Assert.Equal(0, _app.Runs("desk.ps1"));
    }

    [Fact]
    public void A_second_ups_is_added_from_configuration_and_named()
    {
        _app.SimulateSecond(new { GridPresent = true, BatteryPercent = 40 });
        _app.Seed();
        _app.Start();
        var window = _app.Tab("Configuration");
        window.HasText("UpsSettingsTitle", "Bluetti Simulator");

        window.ByName("Add UPS…").InvokeAsync();
        var dialog = Ui.WindowOf(_app.Pid, "Find UPS");
        dialog.HasText("StatusText", "Found 2 devices. Select yours and click Pair.");
        var items = Ui.WaitFor(() =>
        {
            var found = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            return found.Count == 2 ? found : null;
        }, "both devices");
        items[1].Select();
        var pair = dialog.ById("PairButton");
        Ui.Until(() => pair.Current.IsEnabled, "Pair to be enabled");
        pair.Invoke();
        Ui.Until(() => dialog.Current.IsOffscreen, "the dialog to close", TimeSpan.FromSeconds(30));

        // Two of the same model: told apart by what they call themselves.
        Ui.Until(() => window.AllById("PairedTitle").Select(e => e.Text()).Where(t => t.StartsWith("Bluetti")).SequenceEqual(
            ["Bluetti Simulator (SIMULATOR0001)", "Bluetti Simulator (SIMULATOR0002)"]), "both in the list");
        Ui.Until(() => _app.UpsCount() == 2, "the second UPS to be saved");
        window.HasText("UpsSettingsTitle", "Bluetti Simulator (SIMULATOR0002)");

        // Naming it.
        var name = window.ByName("UPS name");
        name.SetFocus();
        name.SetValue("Office");
        window.ByName("Close to tray").SetFocus();
        window.HasText("UpsSettingsTitle", "Office");
        Ui.Until(() => (string?)_app.Ups(1)["Label"] == "Office", "the name to be saved");
        Ui.Until(() => window.AllById("PairedTitle").Any(e => e.Text() == "Bluetti Simulator"), "the first one to lose its suffix");

        // Its notification switches are its own: all on for a new UPS (the first one's are seeded off).
        var gridLost = window.ByName("Grid lost");
        Assert.True(gridLost.IsChecked());
        window.ByName("UPS reachable again").SetChecked(false);
        Ui.Until(() => (bool?)_app.Ups(1)["Notifications"]?["UpsReachable"] == false, "the second one's switch to be saved");

        // The picker switches the settings shown.
        var picker = Ui.WaitFor(() => _app.MainWindow().Find(new PropertyCondition(AutomationElement.AutomationIdProperty, "UpsPicker")), "the UPS picker");
        ((ExpandCollapsePattern)picker.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        Ui.WaitFor(() => picker.Find(new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.NameProperty, "Bluetti Simulator"))), "the first UPS in the picker").Select();
        window.HasText("UpsSettingsTitle", "Bluetti Simulator");
        Assert.False(window.ByName("Grid lost").IsChecked());
    }
}
