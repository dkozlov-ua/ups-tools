using Point = System.Windows.Point;
using System.Windows.Automation;

namespace UpsTools.UiTests.Support;

/// <summary>UI Automation helpers that wait for what they look for.</summary>
internal static class Ui
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public static T WaitFor<T>(Func<T?> probe, string what, TimeSpan? timeout = null) where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        Exception? last = null;
        while (true)
        {
            try
            {
                if (probe() is { } found) return found;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                last = ex; // the tree changed under us: look again
            }
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}", last);
            Thread.Sleep(100);
        }
    }

    public static void Until(Func<bool> condition, string what, TimeSpan? timeout = null) =>
        WaitFor(() => condition() ? "" : null, what, timeout);

    public static AutomationElement? Find(this AutomationElement root, Condition condition) =>
        root.FindFirst(TreeScope.Descendants, condition);

    public static AutomationElement ById(this AutomationElement root, string id, TimeSpan? timeout = null) =>
        WaitFor(() => root.Find(new PropertyCondition(AutomationElement.AutomationIdProperty, id)), $"element #{id}", timeout);

    public static AutomationElement ByName(this AutomationElement root, string name, TimeSpan? timeout = null) =>
        WaitFor(() => root.Find(new PropertyCondition(AutomationElement.NameProperty, name)), $"element '{name}'", timeout);

    public static AutomationElement? TryByName(this AutomationElement root, string name) =>
        root.Find(new PropertyCondition(AutomationElement.NameProperty, name));

    public static IReadOnlyList<AutomationElement> AllById(this AutomationElement root, string id) =>
        root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id)).Cast<AutomationElement>().ToList();

    public static string Text(this AutomationElement element) => element.Current.Name;

    /// <summary>Waits until the element's text is <paramref name="expected"/>.</summary>
    public static void HasText(this AutomationElement root, string id, string expected, TimeSpan? timeout = null)
    {
        string? last = null;
        try
        {
            Until(() => (last = root.ById(id).Text()) == expected, $"#{id} to read '{expected}'", timeout);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"#{id} reads '{last}', not '{expected}'", ex);
        }
    }

    public static void Invoke(this AutomationElement element) =>
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    /// <summary>Invokes without waiting for it to return: for buttons that open a modal dialog.</summary>
    public static void InvokeAsync(this AutomationElement element) =>
        new Thread(() => { try { element.Invoke(); } catch { /* the dialog is checked by the caller */ } }) { IsBackground = true }.Start();

    public static bool IsChecked(this AutomationElement element) =>
        ((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;

    public static void SetChecked(this AutomationElement element, bool on)
    {
        if (element.IsChecked() != on) ((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        Until(() => element.IsChecked() == on, $"'{element.Text()}' to be {(on ? "checked" : "unchecked")}");
    }

    public static string Value(this AutomationElement element) =>
        ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

    public static void SetValue(this AutomationElement element, string value) =>
        ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);

    public static void Select(this AutomationElement element) =>
        ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

    public static Point Centre(this AutomationElement element)
    {
        var r = element.Current.BoundingRectangle;
        return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
    }

    /// <summary>The top-level windows of a process, e.g. a message box or a file dialog.</summary>
    public static AutomationElement WindowOf(int processId, string title, TimeSpan? timeout = null) =>
        WaitFor(() => AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
                new PropertyCondition(AutomationElement.NameProperty, title)))
            ?? FindOwned(processId, title), $"window '{title}'", timeout);

    /// <summary>Dialogs owned by a window show up as its children.</summary>
    private static AutomationElement? FindOwned(int processId, string title)
    {
        foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children,
                     new PropertyCondition(AutomationElement.ProcessIdProperty, processId)))
        {
            var owned = top.FindFirst(TreeScope.Children, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                new PropertyCondition(AutomationElement.NameProperty, title)));
            if (owned is not null) return owned;
        }
        return null;
    }

    /// <summary>A message box of the app: its text, then the button clicked.</summary>
    public static string Answer(int processId, string button, string title = "UPS Tools")
    {
        var box = WaitFor(() => FindMessageBox(processId, title), "a message box");
        var text = string.Join("\n", box.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().Select(t => t.Text()));
        box.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, button)))!.Invoke();
        Until(() => FindMessageBox(processId, title) is null, "the message box to close");
        return text;
    }

    private static AutomationElement? FindMessageBox(int processId, string title)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ClassNameProperty, "#32770"),
            new PropertyCondition(AutomationElement.NameProperty, title));
        return AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(condition,
                   new PropertyCondition(AutomationElement.ProcessIdProperty, processId)))
               ?? AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, processId))
                   .Cast<AutomationElement>().Select(w => w.FindFirst(TreeScope.Children, condition)).FirstOrDefault(w => w is not null);
    }
}

