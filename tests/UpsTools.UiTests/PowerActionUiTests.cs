using System.Text.Json.Nodes;
using System.Windows.Automation;
using UpsTools.UiTests.Support;

namespace UpsTools.UiTests;

/// <summary>
/// The built-in Sleep / Hibernate / Shut down actions and their countdown window. With the simulated UPS the PC
/// stays on: the app logs what it would have done.
/// </summary>
public sealed class PowerActionUiTests : IDisposable
{
    private readonly AppUnderTest _app = new();

    public void Dispose() => _app.Dispose();

    private static JsonObject Power(string power, int countdown) => new() { ["Power"] = power, ["CountdownSeconds"] = countdown };

    private AutomationElement Countdown(string title) => Ui.WindowOf(_app.Pid, $"{title} — UPS Tools");

    private bool CountdownShown(string title) =>
        AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, _app.Pid),
            new PropertyCondition(AutomationElement.NameProperty, $"{title} — UPS Tools"))) is not null;

    [Fact]
    public void An_action_is_added_from_the_menu_and_its_countdown_saved()
    {
        _app.Seed();
        _app.Start();
        var window = _app.Tab("Scripts");

        window.ByName("Add an action to Battery low").InvokeAsync();
        Ui.WaitFor(() => AutomationElement.RootElement.Find(new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
            new PropertyCondition(AutomationElement.NameProperty, "Hibernate"))), "the action menu").Invoke();

        Ui.Until(() => (string?)_app.Settings()["Scripts"]?["BatteryLow"]?[0]?["Power"] == "Hibernate", "Hibernate to be saved");
        Assert.Equal(60, (int)_app.Settings()["Scripts"]!["BatteryLow"]![0]!["CountdownSeconds"]!);
        Assert.Contains(window.AllById("ScriptName"), n => n.Text() == "Hibernate");
        Assert.Null(window.TryByName("Arguments for Hibernate")); // no arguments or options: a countdown instead
        Assert.Null(window.TryByName("Wait until Hibernate finishes"));

        var countdown = window.ByName("Countdown before Hibernate in seconds");
        Assert.Equal("60", countdown.Value());
        countdown.SetFocus();
        countdown.SetValue("900");
        Ui.Until(() => (int)_app.Settings()["Scripts"]!["BatteryLow"]![0]!["CountdownSeconds"]! == 600, "the countdown to be saved, at most 600 s");
        window.ByName("Run the Battery low scripts now").SetFocus();
        Ui.Until(() => countdown.Value() == "600", "the box to show what applies");
    }

    [Fact]
    public void Run_now_counts_down_and_can_be_cancelled_or_done_at_once()
    {
        _app.Seed(s => s["Scripts"]!["GridLost"] = new JsonArray(Power("Hibernate", 120)));
        _app.Start();
        var window = _app.Tab("Scripts");

        window.ByName("Run Hibernate now").InvokeAsync();
        Assert.Equal("Run Hibernate now?", Ui.Answer(_app.Pid, "Yes"));
        var countdown = Countdown("Hibernate");
        Assert.Matches(@"^Hibernating the PC in 1(19|20) s$", countdown.ById("CountdownText").Text());
        Assert.StartsWith("Manual run. Hibernates the PC", countdown.ById("CountdownReason").Text());
        countdown.ByName("Cancel").Invoke();

        Ui.Until(() => !CountdownShown("Hibernate"), "the countdown to close");
        Ui.Until(() => _app.Log().Contains("Manual run: Hibernate was cancelled"), "the cancel to be logged");
        Assert.DoesNotContain("Simulated UPS: Hibernating the PC skipped", _app.Log());

        window.ByName("Run Hibernate now").InvokeAsync();
        Ui.Answer(_app.Pid, "Yes");
        Countdown("Hibernate").ByName("Hibernate now").Invoke();
        Ui.Until(() => _app.Log().Contains("Simulated UPS: Hibernating the PC skipped"), "the hibernation");
        Assert.False(CountdownShown("Hibernate"));
    }

    [Fact]
    public void Battery_low_shuts_down_when_the_countdown_runs_out()
    {
        var after = _app.Script("after.ps1");
        _app.Seed(s => s["Scripts"]!["BatteryLow"] = new JsonArray(Power("ShutDown", 3), new JsonObject { ["Path"] = after }));
        _app.Start();
        _app.Tab("Dashboard").HasText("GridValue", "Online");

        _app.Simulate(new { GridPresent = false, BatteryPercent = 10, AcInputW = 0, AcOutputW = 120 });

        Countdown("Shut down").HasText("CountdownReason", "Battery low. Shuts down the PC. Apps are closed even if they have unsaved work.");
        Ui.Until(() => _app.Log().Contains("Simulated UPS: Shutting down the PC skipped"), "the shutdown", TimeSpan.FromSeconds(20));
        Ui.Until(() => !CountdownShown("Shut down"), "the countdown to close");
        Ui.Until(() => _app.Runs("after.ps1") == 1, "the script after it");
    }

    [Fact]
    public void Grid_restored_during_the_countdown_closes_it()
    {
        _app.Seed(s => s["Scripts"]!["BatteryLow"] = new JsonArray(Power("Sleep", 300)));
        _app.Start();
        _app.Tab("Dashboard").HasText("GridValue", "Online");

        _app.Simulate(new { GridPresent = false, BatteryPercent = 10, AcInputW = 0, AcOutputW = 120 });
        Countdown("Sleep");

        _app.Simulate(new { GridPresent = true, BatteryPercent = 10, AcInputW = 150, AcOutputW = 120 });
        Ui.Until(() => !CountdownShown("Sleep"), "the countdown to close");
        Ui.Until(() => _app.Log().Contains("Grid restored: stopped the Battery low scripts; Sleep won't run"), "the log");
        Assert.DoesNotContain("Simulated UPS: Putting the PC to sleep skipped", _app.Log());
    }
}
