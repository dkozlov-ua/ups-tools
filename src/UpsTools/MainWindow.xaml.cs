using System.Windows;

namespace UpsTools;

public partial class MainWindow : Window
{
    public MainWindow(AppController controller)
    {
        InitializeComponent();
        DataContext = controller;
    }
}
