using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UpsTools.Services;
using UpsTools.ViewModels;

namespace UpsTools.Views;

public partial class ConfigurationView : UserControl
{
    private const string ProjectUrl = "https://github.com/dkozlov-ua/ups-tools";

    public ConfigurationView() => InitializeComponent();

    private AppController Controller => (AppController)DataContext;

    private async void AddUps_Click(object sender, RoutedEventArgs e)
    {
        var controller = Controller;
        var owner = Window.GetWindow(this)!;

        // The dialog needs the Bluetooth links to itself: a UPS accepts one connection at a time.
        await controller.StopMonitoringAsync();

        var dialog = new PairWindow(controller.Drivers, controller.Log, controller.KnownUserId) { Owner = owner };
        if (dialog.ShowDialog() == true && dialog.Result is { } paired)
            controller.Pair(paired);
        else
            controller.StartMonitoring();
    }

    private async void Forget_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not UpsUnit unit) return;
        var scripts = unit.Settings.Scripts.Values.Sum(list => list.Count);
        var question = scripts == 0
            ? $"Stop monitoring {unit.Title} and forget it?"
            : $"Stop monitoring {unit.Title} and forget it, with its {(scripts == 1 ? "script" : $"{scripts} scripts")}?";
        var result = MessageBox.Show(Window.GetWindow(this)!, question, "UPS Tools", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes) await Controller.ForgetAsync(unit);
    }

    /// <summary>Enter applies the name typed so far.</summary>
    private void Name_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    /// <summary>A number box shows the value in effect once it loses focus (its Tag is its <see cref="NumberSetting"/>).</summary>
    private void Number_LostFocus(object sender, RoutedEventArgs e) => ((NumberSetting)((FrameworkElement)sender).Tag).Normalize();

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Controller.SettingsFolder);
        Open(Controller.SettingsFolder);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Controller.Log.FilePath)) Open(Controller.Log.FilePath);
    }

    private void TestNotification_Click(object sender, RoutedEventArgs e) => Controller.SendTestNotification();

    private void WindowsNotificationSettings_Click(object sender, RoutedEventArgs e) => Open("ms-settings:notifications");

    private void OpenGitHub_Click(object sender, RoutedEventArgs e) => Open(ProjectUrl);

    private async void Update_Click(object sender, RoutedEventArgs e) => await Controller.Configuration.Update.ActAsync();

    private void WhatsNew_Click(object sender, RoutedEventArgs e)
    {
        if (Controller.Configuration.Update.ReleasePage is { Length: > 0 } page) Open(page);
    }

    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
}
