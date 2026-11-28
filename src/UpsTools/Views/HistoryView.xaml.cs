using System.Windows.Controls;
using System.Windows.Media;
using UpsTools.Core;
using UpsTools.Core.History;

namespace UpsTools.Views;

/// <summary>The History tab: the chart of the selected UPS, over 6 hours, 24 hours or 7 days.</summary>
public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
        foreach (var (label, span) in HistoryChart.Ranges)
        {
            var button = new RadioButton
            {
                Content = label,
                GroupName = "HistoryRange",
                IsChecked = span == Chart.Range,
                Margin = new System.Windows.Thickness(12, 0, 0, 0),
                MinWidth = 0,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "Range" + label.Replace(" ", ""));
            button.Checked += (_, _) => Chart.Range = span;
            Ranges.Children.Add(button);
        }

        BatteryKey.Fill = new SolidColorBrush(HistoryChartView.BatteryColor);
        LoadKey.Fill = new SolidColorBrush(HistoryChartView.LoadColor);
        OutageKey.Fill = new SolidColorBrush(HistoryChartView.OutageColor);
        MissingKey.Fill = new SolidColorBrush(HistoryChartView.MissingColor);
        EventKey.Fill = new SolidColorBrush(HistoryChartView.EventColor(UpsEventKind.GridLost));
    }
}
