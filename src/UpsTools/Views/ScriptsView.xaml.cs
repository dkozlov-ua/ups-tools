using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using UpsTools.Core;
using UpsTools.ViewModels;

namespace UpsTools.Views;

public partial class ScriptsView : UserControl
{
    private const string RowName = "ScriptRow";
    private const string GripName = "Grip";

    private Point _pressedAt;
    private ScriptItemViewModel? _pressed;
    private InsertionAdorner? _insertion;

    public ScriptsView() => InitializeComponent();

    private AppController Controller => (AppController)DataContext;

    private EventScriptsViewModel? GroupOf(ScriptItemViewModel item) =>
        Controller.Selected?.EventScripts.FirstOrDefault(g => g.Items.Contains(item));

    private void AddScript_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EventScriptsViewModel group) return;

        var dialog = new OpenFileDialog
        {
            Title = $"Script to run on \"{group.Title}\"",
            Filter = "Scripts and programs (*.ahk;*.bat;*.cmd;*.ps1;*.exe)|*.ahk;*.bat;*.cmd;*.ps1;*.exe|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var file in dialog.FileNames) group.Add(file);
    }

    private void AddAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void AddPower_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: EventScriptsViewModel group, Tag: PowerAction power }) group.Add(power);
    }

    /// <summary>A number box shows the value in effect once it loses focus (its Tag is its <see cref="NumberSetting"/>).</summary>
    private void Number_LostFocus(object sender, RoutedEventArgs e) => ((NumberSetting)((FrameworkElement)sender).Tag).Normalize();

    private async void RemoveScript_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ScriptItemViewModel item) return;
        if (GroupOf(item) is not { } group) return;
        if (await group.RemoveAsync(item) is { } problem)
            MessageBox.Show(Window.GetWindow(this)!,
                $"{item.FileName} runs as administrator, and its task couldn't be deleted: {problem}.\n\nThe script stays in the list.",
                "UPS Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ScriptItemViewModel item) await item.RepairAsync();
    }

    private void RunScript_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ScriptItemViewModel item) return;
        if (!Confirm($"Run {item.FileName} now?")) return;
        Controller.RunScript(item.Model, "Manual run");
    }

    private void RunEvent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EventScriptsViewModel group) return;
        var count = group.EnabledActions.Count();
        if (count == 0)
        {
            MessageBox.Show(Window.GetWindow(this)!, $"\"{group.Title}\" has no enabled scripts.", "UPS Tools",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!Confirm($"Run the {count} enabled script(s) of \"{group.Title}\" now?")) return;
        Controller.RunScripts(group, $"Manual run of \"{group.Title}\"");
    }

    // Scripts may hibernate the PC or put a NAS to sleep, so a stray click must not run them.
    private bool Confirm(string question) =>
        MessageBox.Show(Window.GetWindow(this)!, question, "UPS Tools", MessageBoxButton.YesNo, MessageBoxImage.Question)
        == MessageBoxResult.Yes;

    // ---- Reordering: drag the handle, Alt+Up / Alt+Down, or the row's context menu ----

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveBy((sender as FrameworkElement)?.DataContext, -1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveBy((sender as FrameworkElement)?.DataContext, +1);

    private void Row_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.System || Keyboard.Modifiers != ModifierKeys.Alt) return;
        var by = e.SystemKey switch { Key.Up => -1, Key.Down => +1, _ => 0 };
        if (by == 0) return;
        e.Handled = true;
        MoveBy((sender as FrameworkElement)?.DataContext, by);
    }

    private void MoveBy(object? dataContext, int by)
    {
        if (dataContext is not ScriptItemViewModel item || GroupOf(item) is not { } group) return;
        group.Move(item, group.Items.IndexOf(item) + by);
        FocusGrip(item);
    }

    private void Grip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = (sender as FrameworkElement)?.DataContext as ScriptItemViewModel;
        _pressedAt = e.GetPosition(this);
        if (sender is UIElement grip)
        {
            grip.Focus();
            // The handle is narrow: without the capture, a quick drag leaves it before it counts as one.
            grip.CaptureMouse();
        }
    }

    private void Grip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndPress(sender);

    private void EndPress(object sender)
    {
        _pressed = null;
        if (sender is UIElement { IsMouseCaptured: true } grip) grip.ReleaseMouseCapture();
    }

    private void Grip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed is not { } item) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndPress(sender);
            return;
        }

        var moved = e.GetPosition(this) - _pressedAt;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        EndPress(sender);
        var row = RowOf(sender as DependencyObject);
        if (row is not null) row.Opacity = 0.5;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(ScriptItemViewModel), item), DragDropEffects.Move);
        }
        finally
        {
            if (row is not null) row.Opacity = 1;
            ShowInsertion(null, false);
        }
    }

    private void View_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true; // the Arguments box would otherwise take the drag as text
        ScrollNearEdges(e);
        if (DropTarget(e) is { } target)
        {
            e.Effects = DragDropEffects.Move;
            ShowInsertion(target.Index == target.From ? null : target.Row, target.After);
        }
        else
        {
            e.Effects = DragDropEffects.None;
            ShowInsertion(null, false);
        }
    }

    private void View_PreviewDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ShowInsertion(null, false);
        if (DropTarget(e) is not { } target) return;
        target.Group.Move(target.Item, target.Index);
        FocusGrip(target.Item);
    }

    private sealed record Target(EventScriptsViewModel Group, ScriptItemViewModel Item, FrameworkElement Row, bool After, int From, int Index);

    /// <summary>Where the dragged script would go: before or after the row under the mouse, within its own event.</summary>
    private Target? DropTarget(DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ScriptItemViewModel)) is not ScriptItemViewModel item) return null;
        if (RowOf(e.OriginalSource as DependencyObject) is not { DataContext: ScriptItemViewModel over } row) return null;
        if (GroupOf(item) is not { } group || !group.Items.Contains(over)) return null;

        var after = e.GetPosition(row).Y > row.ActualHeight / 2;
        var from = group.Items.IndexOf(item);
        var index = group.Items.IndexOf(over) + (after ? 1 : 0);
        if (index > from) index--;
        return new Target(group, item, row, after, from, index);
    }

    private void ScrollNearEdges(DragEventArgs e)
    {
        const double zone = 40, step = 14;
        var y = e.GetPosition(Scroller).Y;
        if (y < zone) Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - step);
        else if (y > Scroller.ActualHeight - zone) Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + step);
    }

    private void ShowInsertion(FrameworkElement? row, bool after)
    {
        if (_insertion is not null && (_insertion.AdornedElement != row || _insertion.After != after))
        {
            AdornerLayer.GetAdornerLayer(_insertion.AdornedElement)?.Remove(_insertion);
            _insertion = null;
        }
        if (row is null || _insertion is not null || AdornerLayer.GetAdornerLayer(row) is not { } layer) return;

        _insertion = new InsertionAdorner(row, after, (Brush)FindResource("AccentFillColorDefaultBrush"));
        layer.Add(_insertion);
    }

    private void FocusGrip(ScriptItemViewModel item) =>
        Dispatcher.InvokeAsync(() =>
        {
            var row = Descendants(this).OfType<FrameworkElement>()
                .FirstOrDefault(fe => fe.Name == RowName && fe.DataContext == item);
            if (row is not null && Descendants(row).OfType<FrameworkElement>().FirstOrDefault(fe => fe.Name == GripName) is { } grip)
                grip.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);

    private static FrameworkElement? RowOf(DependencyObject? element)
    {
        for (var d = element; d is not null;
             d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is FrameworkElement { Name: RowName } row) return row;
        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    /// <summary>The line that shows where a dragged script will land.</summary>
    private sealed class InsertionAdorner : Adorner
    {
        private readonly Brush _brush;

        public InsertionAdorner(UIElement row, bool after, Brush brush) : base(row)
        {
            After = after;
            _brush = brush;
            IsHitTestVisible = false;
        }

        public bool After { get; }

        protected override void OnRender(DrawingContext dc)
        {
            var size = AdornedElement.RenderSize;
            var y = After ? size.Height : 0;
            dc.DrawRoundedRectangle(_brush, null, new Rect(0, y - 1.5, size.Width, 3), 1.5, 1.5);
        }
    }
}
