using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using UpsTools.Services;

namespace UpsTools.Views;

public partial class ConfigurationView : UserControl
{
    private const string ProjectUrl = "https://github.com/dkozlov-ua/ups-tools";

    public ConfigurationView() => InitializeComponent();

    private AppController Controller => (AppController)DataContext;

    private async void FindUps_Click(object sender, RoutedEventArgs e)
    {
        var controller = Controller;
        var owner = Window.GetWindow(this)!;

        // The dialog needs the Bluetooth link to itself.
        await controller.StopMonitoringAsync();

        var dialog = new PairWindow(controller.Drivers, controller.Log) { Owner = owner };
        if (dialog.ShowDialog() == true && dialog.Result is { } paired)
            controller.Pair(paired);
        else
            controller.StartMonitoring();
    }

    private async void Forget_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(Window.GetWindow(this)!, "Stop monitoring this UPS and forget it?", "UPS Tools",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes) await Controller.ForgetAsync();
    }

    private void UpdatePeriod_LostFocus(object sender, RoutedEventArgs e) => Controller.Configuration.NormalizeUpdatePeriod();

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

    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
}
