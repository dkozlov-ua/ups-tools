using System.Text.Json.Nodes;
using System.Windows.Automation;
using UpsTools.UiTests.Support;
using Point = System.Windows.Point;

namespace UpsTools.UiTests;

/// <summary>The Scripts tab, driven like a user would: the file dialog, checkboxes, dragging, shortcuts, confirmations.</summary>
public sealed class ScriptsUiTests : IDisposable
{
    private readonly AppUnderTest _app = new();

    public void Dispose() => _app.Dispose();

    private AutomationElement StartWith(params (string Event, string Name, bool Wait)[] scripts)
    {
        var paths = scripts.Select(s => (s.Event, Path: _app.Script(s.Name), s.Wait)).ToList();
        _app.Seed(settings =>
        {
            foreach (var group in paths.GroupBy(p => p.Event))
                settings["Scripts"]![group.Key] = new JsonArray([.. group.Select(p => (JsonNode)new JsonObject
                {
                    ["Path"] = p.Path, ["WaitForExit"] = p.Wait,
                })]);
        });
        _app.Start();
        return _app.Tab("Scripts");
    }

    private IReadOnlyList<string> Saved(string kind) =>
        _app.Settings()["Scripts"]![kind]!.AsArray().Select(s => Path.GetFileName((string)s!["Path"]!)).ToList();

    private void SavedOrder(string kind, params string[] names) =>
        Ui.Until(() => Saved(kind).SequenceEqual(names), $"{kind} to be saved as {string.Join(", ", names)}; it is {string.Join(", ", Saved(kind))}");

    private static IReadOnlyList<string> Shown(AutomationElement window) => window.AllById("ScriptName").Select(e => e.Text()).ToList();

    [Fact]
    public void Each_event_has_its_card_and_an_empty_one_says_so()
    {
        var window = StartWith();
        foreach (var title in new[] { "Grid lost", "Battery low", "Grid restored", "UPS unreachable", "UPS reachable again" })
        {
            window.ByName($"Add a script to {title}");
            window.ByName($"Run the {title} scripts now");
        }
        Assert.Equal(5, window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "No scripts — nothing happens on this event.")).Count);
    }

    [Fact]
    public void Adding_a_script_through_the_file_dialog_saves_it()
    {
        var window = StartWith();
        var script = _app.Script("pause games.ps1");

        window.ByName("Add a script to Grid lost").InvokeAsync();
        var dialog = Ui.WindowOf(_app.Pid, "Script to run on \"Grid lost\"");
        var fileName = Ui.WaitFor(() => dialog.Find(new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.AutomationIdProperty, "1148"))), "the file name box");
        fileName.SetValue(script);
        fileName.SetFocus();
        Input.Keys(Input.Enter);

        window.ByName("Enable pause games.ps1");
        Assert.Equal(["pause games.ps1"], Shown(window));
        SavedOrder("GridLost", "pause games.ps1");
        var saved = _app.Settings()["Scripts"]!["GridLost"]![0]!;
        Assert.Equal(script, (string)saved["Path"]!);
        Assert.True((bool)saved["Enabled"]!);
        Assert.False((bool)saved["WaitForExit"]!);
    }

    [Fact]
    public void Checkboxes_and_arguments_are_saved()
    {
        var window = StartWith(("GridLost", "a.ps1", false));

        window.ByName("Enable a.ps1").SetChecked(false);
        Ui.Until(() => !(bool)_app.Settings()["Scripts"]!["GridLost"]![0]!["Enabled"]!, "Enabled to be saved");

        window.ByName("Wait until a.ps1 finishes").SetChecked(true);
        Ui.Until(() => (bool)_app.Settings()["Scripts"]!["GridLost"]![0]!["WaitForExit"]!, "WaitForExit to be saved");

        var arguments = window.ByName("Arguments for a.ps1");
        arguments.SetFocus();
        arguments.SetValue("  -Force  ");
        window.ByName("Enable a.ps1").SetFocus(); // arguments apply when the box loses focus
        Ui.Until(() => (string?)_app.Settings()["Scripts"]!["GridLost"]![0]!["Arguments"] == "-Force", "the arguments to be saved trimmed");
        Assert.Equal("-Force", arguments.Value());
    }

    [Fact]
    public void Alt_down_and_alt_up_move_the_focused_script()
    {
        var window = StartWith(("GridLost", "a.ps1", false), ("GridLost", "b.ps1", false), ("GridLost", "c.ps1", false));

        window.ByName("Enable a.ps1").SetFocus();
        Input.Keys(Input.Alt, Input.Down);
        SavedOrder("GridLost", "b.ps1", "a.ps1", "c.ps1");
        Ui.Until(() => Shown(window).SequenceEqual(["b.ps1", "a.ps1", "c.ps1"]), "the list to follow");

        Input.Keys(Input.Alt, Input.Down); // focus stays on the moved script
        SavedOrder("GridLost", "b.ps1", "c.ps1", "a.ps1");
        Input.Keys(Input.Alt, Input.Down); // already last
        Input.Keys(Input.Alt, Input.Up);
        Input.Keys(Input.Alt, Input.Up);
        SavedOrder("GridLost", "a.ps1", "b.ps1", "c.ps1");
    }

    [Fact]
    public void The_context_menu_moves_a_script()
    {
        var window = StartWith(("GridRestored", "a.ps1", false), ("GridRestored", "b.ps1", false));

        // On the script's name: the Arguments box has a menu of its own.
        Input.Click(Ui.WaitFor(() => window.AllById("ScriptName").FirstOrDefault(n => n.Text() == "b.ps1"), "b.ps1").Centre(), right: true);
        Ui.WaitFor(() => AutomationElement.RootElement.Find(new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
            new PropertyCondition(AutomationElement.NameProperty, "Move up"))), "the context menu").Invoke();

        SavedOrder("GridRestored", "b.ps1", "a.ps1");
    }

    [Fact]
    public void Dragging_a_script_by_its_handle_reorders_it()
    {
        var window = StartWith(("GridLost", "a.ps1", false), ("GridLost", "b.ps1", false), ("GridLost", "c.ps1", false));
        window.ByName("Enable a.ps1").SetFocus();

        // The handle is the glyph left of the Enabled checkbox.
        Point Handle(string name)
        {
            var r = window.ByName($"Enable {name}").Current.BoundingRectangle;
            return new Point(r.Left - 12, r.Top + r.Height / 2);
        }
        // "Wait until it finishes" is on the row's second line.
        Point LowerHalf(string name) => window.ByName($"Wait until {name} finishes").Centre();

        Input.Drag(Handle("a.ps1"), LowerHalf("c.ps1"));
        SavedOrder("GridLost", "b.ps1", "c.ps1", "a.ps1");

        // Pressed near the handle's edge and moved off it sideways at once, on to the empty part of the row.
        var b = window.ByName("Enable b.ps1").Current.BoundingRectangle;
        Input.Drag(Handle("c.ps1"), new Point(b.Right + 250, b.Top + b.Height / 4));
        SavedOrder("GridLost", "c.ps1", "b.ps1", "a.ps1");
    }

    [Fact]
    public void Dropping_on_another_events_card_does_nothing()
    {
        var window = StartWith(("GridLost", "a.ps1", false), ("GridLost", "b.ps1", false), ("GridRestored", "x.ps1", false));
        var a = window.ByName("Enable a.ps1").Current.BoundingRectangle;
        var x = window.ByName("Enable x.ps1").Current.BoundingRectangle;

        Input.Drag(new Point(a.Left - 12, a.Top + a.Height / 2), new Point(x.Right + 200, x.Top + x.Height / 2));
        Thread.Sleep(500);

        Assert.Equal(["a.ps1", "b.ps1"], Saved("GridLost"));
        Assert.Equal(["x.ps1"], Saved("GridRestored"));
    }

    [Fact]
    public void Run_now_asks_first_and_runs_the_script_on_yes()
    {
        var window = StartWith(("GridLost", "a.ps1", false));

        window.ByName("Run a.ps1 now").InvokeAsync();
        Assert.Equal("Run a.ps1 now?", Ui.Answer(_app.Pid, "No"));
        Thread.Sleep(1000);
        Assert.Equal(0, _app.Runs("a.ps1"));

        window.ByName("Run a.ps1 now").InvokeAsync();
        Ui.Answer(_app.Pid, "Yes");
        Ui.Until(() => _app.Runs("a.ps1") == 1, "the script to run");
        Ui.Until(() => _app.Log().Contains("Manual run: started a.ps1"), "the log");
    }

    [Fact]
    public void Run_now_on_an_event_runs_its_enabled_scripts()
    {
        var window = StartWith(("UpsUnreachable", "a.ps1", true), ("UpsUnreachable", "b.ps1", false), ("UpsUnreachable", "c.ps1", false));
        window.ByName("Enable c.ps1").SetChecked(false);

        window.ByName("Run the UPS unreachable scripts now").InvokeAsync();
        Assert.Equal("Run the 2 enabled script(s) of \"UPS unreachable\" now?", Ui.Answer(_app.Pid, "Yes"));

        Ui.Until(() => _app.Runs("a.ps1") == 1 && _app.Runs("b.ps1") == 1, "the enabled scripts to run");
        Thread.Sleep(500);
        Assert.Equal(0, _app.Runs("c.ps1"));
    }

    [Fact]
    public void Run_now_without_enabled_scripts_says_there_are_none()
    {
        var window = StartWith();
        window.ByName("Run the Grid restored scripts now").InvokeAsync();
        Assert.Equal("\"Grid restored\" has no enabled scripts.", Ui.Answer(_app.Pid, "OK"));
    }

    [Fact]
    public void Removing_a_script_saves_the_rest()
    {
        var window = StartWith(("GridLost", "a.ps1", false), ("GridLost", "b.ps1", false));

        window.ByName("Remove a.ps1").Invoke();

        SavedOrder("GridLost", "b.ps1");
        Ui.Until(() => window.TryByName("Enable a.ps1") is null, "the row to go");
        Assert.Equal(["b.ps1"], Shown(window));
    }

    [Fact]
    public void A_missing_file_and_a_missing_administrator_task_are_shown()
    {
        _app.Seed(s => s["Scripts"]!["GridLost"] = new JsonArray(new JsonObject
        {
            ["Path"] = Path.Combine(_app.Work, "gone.ps1"), ["RunElevated"] = true,
        }));
        _app.Start();
        var window = _app.Tab("Scripts");

        Ui.WaitFor(() => window.Find(new PropertyCondition(AutomationElement.HelpTextProperty, "File not found")), "the missing file mark");
        Ui.WaitFor(() => window.Find(new PropertyCondition(AutomationElement.HelpTextProperty, "Runs as administrator")), "the shield");
        window.HasText("ElevationNote", "Can't run as administrator: its administrator task is missing.");
        window.ByName("Repair gone.ps1");
        Assert.True(window.ByName("Run gone.ps1 as administrator").IsChecked());
    }

    [AdminFact]
    public void Run_as_administrator_creates_a_task_that_runs_the_script_elevated()
    {
        var window = StartWith(("GridLost", "a.ps1", false));
        var elevated = window.ByName("Run a.ps1 as administrator");

        elevated.SetChecked(true);
        Ui.Until(() => _app.Tasks().Count == 1, "the administrator task");
        Ui.Until(() => (bool)_app.Settings()["Scripts"]!["GridLost"]![0]!["RunElevated"]!, "RunElevated to be saved");
        Ui.WaitFor(() => window.Find(new PropertyCondition(AutomationElement.HelpTextProperty, "Runs as administrator")), "the shield");

        window.ByName("Run a.ps1 now").InvokeAsync();
        Ui.Answer(_app.Pid, "Yes");
        Ui.Until(() => _app.Runs("a.ps1") == 1, "the script to run through its task", TimeSpan.FromSeconds(30));
        Assert.Contains("Manual run: started a.ps1 as administrator", _app.Log());

        elevated.SetChecked(false);
        Ui.Until(() => _app.Tasks().Count == 0, "the task to be deleted");
        Ui.Until(() => !(bool)_app.Settings()["Scripts"]!["GridLost"]![0]!["RunElevated"]!, "RunElevated off to be saved");
    }

    [AdminFact]
    public void Repair_creates_a_missing_administrator_task()
    {
        var script = _app.Script("a.ps1");
        _app.Seed(s => s["Scripts"]!["GridLost"] = new JsonArray(new JsonObject { ["Path"] = script, ["RunElevated"] = true }));
        _app.Start();
        var window = _app.Tab("Scripts");
        window.HasText("ElevationNote", "Can't run as administrator: its administrator task is missing.");

        window.ByName("Repair a.ps1").Invoke();

        Ui.Until(() => _app.Tasks().Count == 1, "the task");
        Ui.Until(() => window.AllById("ElevationNote").All(n => n.Current.IsOffscreen), "the note to go");
    }

    [AdminFact]
    public void Removing_a_script_that_runs_as_administrator_deletes_its_task()
    {
        var window = StartWith(("GridLost", "a.ps1", false));
        window.ByName("Run a.ps1 as administrator").SetChecked(true);
        Ui.Until(() => _app.Tasks().Count == 1, "the administrator task");
        Ui.Until(() => window.ByName("Run a.ps1 as administrator").Current.IsEnabled, "the checkbox to settle");

        window.ByName("Remove a.ps1").Invoke();

        Ui.Until(() => _app.Tasks().Count == 0, "the task to be deleted");
        SavedOrder("GridLost");
    }
}

