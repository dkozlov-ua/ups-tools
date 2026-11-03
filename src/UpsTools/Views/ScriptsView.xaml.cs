using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UpsTools.ViewModels;

namespace UpsTools.Views;

public partial class ScriptsView : UserControl
{
    public ScriptsView() => InitializeComponent();

    private AppController Controller => (AppController)DataContext;

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

    private void RemoveScript_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ScriptItemViewModel item) return;
        var group = Controller.EventScripts.FirstOrDefault(g => g.Items.Contains(item));
        group?.Remove(item);
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
}
