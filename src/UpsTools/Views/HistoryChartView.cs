using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UpsTools.Core;
using UpsTools.Core.History;

namespace UpsTools.Views;

/// <summary>
/// The History chart of one UPS (<see cref="HistoryChart"/>, worked out in Core): the charge and the load over
/// <see cref="Range"/>, outages shaded, stretches without readings greyed, events marked; pointing shows the reading
/// there. It redraws as points come and once a minute, so that the range keeps sliding.
/// </summary>
public sealed class HistoryChartView : FrameworkElement
{
    public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(nameof(History), typeof(UpsHistory),
        typeof(HistoryChartView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnHistoryChanged));

    public static readonly DependencyProperty RangeProperty = DependencyProperty.Register(nameof(Range), typeof(TimeSpan),
        typeof(HistoryChartView), new FrameworkPropertyMetadata(TimeSpan.FromHours(24), FrameworkPropertyMetadataOptions.AffectsRender));

    // The series' colours: the same in both themes, readable on either.
    internal static readonly Color BatteryColor = Color.FromRgb(0x16, 0xA3, 0x4A);
    internal static readonly Color LoadColor = Color.FromRgb(0x25, 0x63, 0xEB);
    internal static readonly Color OutageColor = Color.FromArgb(0x40, 0xF5, 0x9E, 0x0B);
    internal static readonly Color MissingColor = Color.FromArgb(0x30, 0x80, 0x80, 0x80);

    private readonly DispatcherTimer _minute = new() { Interval = TimeSpan.FromMinutes(1) };
    private Point? _pointer;

    public HistoryChartView()
    {
        _minute.Tick += (_, _) => InvalidateVisual();
        Loaded += (_, _) => _minute.Start();
        Unloaded += (_, _) => _minute.Stop();
        MinHeight = 220;
    }

    public UpsHistory? History
    {
        get => (UpsHistory?)GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
    }

    public TimeSpan Range
    {
        get => (TimeSpan)GetValue(RangeProperty);
        set => SetValue(RangeProperty, value);
    }

    private static void OnHistoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (HistoryChartView)d;
        if (e.OldValue is UpsHistory old) old.Changed -= view.OnRecorded;
        if (e.NewValue is UpsHistory history) history.Changed += view.OnRecorded;
    }

    private void OnRecorded() => Dispatcher.InvokeAsync(InvalidateVisual, DispatcherPriority.Background);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _pointer = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _pointer = null;
        InvalidateVisual();
    }

    private Brush Resource(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private FormattedText Text(string text, Brush brush, double size = 11) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        var to = DateTimeOffset.Now;
        var from = to - Range;
        var history = History;
        var points = history?.Points(from) ?? [];
        var chart = HistoryChart.Build(points, history?.Events(from) ?? [], from, to);

        var text = Resource("TextFillColorSecondaryBrush", Colors.Gray);
        var line = Resource("DividerStrokeColorDefaultBrush", Color.FromArgb(0x30, 0x80, 0x80, 0x80));
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // takes the pointer everywhere

        var plot = new Rect(44, 10, Math.Max(10, ActualWidth - 44 - 60), Math.Max(10, ActualHeight - 10 - 24));
        Point At(double x, double y) => new(plot.Left + x * plot.Width, plot.Bottom - y * plot.Height);

        foreach (var span in chart.Missing) dc.DrawRectangle(new SolidColorBrush(MissingColor), null, Span(span));
        foreach (var span in chart.Outages) dc.DrawRectangle(new SolidColorBrush(OutageColor), null, Span(span));
        Rect Span(ChartSpan s) => new(At(s.From, 1), At(Math.Max(s.To, s.From + 1 / plot.Width), 0));

        var pen = new Pen(line, 1);
        for (var i = 0; i < chart.BatteryTicks.Count; i++)
        {
            var y = At(0, chart.BatteryTicks[i].At).Y;
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
            var left = Text(chart.BatteryTicks[i].Label, new SolidColorBrush(BatteryColor));
            dc.DrawText(left, new Point(plot.Left - 6 - left.Width, y - left.Height / 2));
            var right = Text(chart.LoadTicks[i].Label, new SolidColorBrush(LoadColor));
            dc.DrawText(right, new Point(plot.Right + 6, y - right.Height / 2));
        }
        foreach (var tick in chart.TimeTicks)
        {
            var x = At(tick.At, 0).X;
            dc.DrawLine(pen, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 4));
            var label = Text(tick.Label, text);
            dc.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, 0, ActualWidth - label.Width), plot.Bottom + 6));
        }

        DrawLines(dc, chart.Load, new Pen(new SolidColorBrush(LoadColor), 1.5), At);
        DrawLines(dc, chart.Battery, new Pen(new SolidColorBrush(BatteryColor), 2), At);

        foreach (var e in chart.Events)
        {
            var x = At(e.X, 0).X;
            var color = EventColor(e.Kind);
            dc.DrawLine(new Pen(new SolidColorBrush(color), 1) { DashStyle = DashStyles.Dash }, new Point(x, plot.Top), new Point(x, plot.Bottom));
            dc.DrawEllipse(new SolidColorBrush(color), null, new Point(x, plot.Top), 3.5, 3.5);
        }

        if (chart.IsEmpty)
        {
            var empty = Text(history is null ? "No UPS paired" : "No readings in this range yet", text, 13);
            dc.DrawText(empty, new Point(plot.Left + (plot.Width - empty.Width) / 2, plot.Top + (plot.Height - empty.Height) / 2));
        }
        else if (_pointer is { } p && plot.Contains(p))
        {
            DrawReading(dc, points, from, to, plot, p, text);
        }
    }

    private static void DrawLines(DrawingContext dc, IReadOnlyList<IReadOnlyList<ChartPoint>> lines, Pen pen, Func<double, double, Point> at)
    {
        pen.LineJoin = PenLineJoin.Round;
        foreach (var points in lines)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(at(points[0].X, points[0].Y), false, false);
                if (points.Count == 1) g.LineTo(at(points[0].X, points[0].Y) + new Vector(1, 0), true, false);
                for (var i = 1; i < points.Count; i++) g.LineTo(at(points[i].X, points[i].Y), true, true);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
    }

    /// <summary>The reading nearest the pointer, with a line down to it.</summary>
    private void DrawReading(DrawingContext dc, IReadOnlyList<HistoryPoint> points, DateTimeOffset from, DateTimeOffset to, Rect plot,
        Point pointer, Brush text)
    {
        var time = from + (to - from) * ((pointer.X - plot.Left) / plot.Width);
        var nearest = points.MinBy(p => Math.Abs((p.Time - time).Ticks));
        if (nearest is null || Math.Abs((nearest.Time - time).TotalMinutes) > Math.Max(5, (to - from).TotalMinutes / 200)) return;

        var x = plot.Left + (nearest.Time - from) / (to - from) * plot.Width;
        dc.DrawLine(new Pen(text, 1), new Point(x, plot.Top), new Point(x, plot.Bottom));
        var parts = new[]
        {
            nearest.Time.ToLocalTime().ToString((to - from).TotalDays > 1 ? "ddd HH:mm" : "HH:mm", CultureInfo.CurrentCulture),
            nearest.BatteryPercent is { } b ? $"{b} %" : null,
            nearest.LoadW is { } w ? UpsFormat.Watts(w) : null,
            nearest.Grid switch { true => "grid", false => "on battery", _ => null },
        };
        var label = Text(string.Join(" · ", parts.OfType<string>()), Resource("TextFillColorPrimaryBrush", Colors.Black), 12);
        var box = new Rect(Math.Clamp(x + 8, plot.Left, plot.Right - label.Width - 12), plot.Top + 4, label.Width + 12, label.Height + 6);
        dc.DrawRoundedRectangle(Resource("SolidBackgroundFillColorBaseBrush", Colors.White), new Pen(Resource("CardStrokeColorDefaultBrush", Colors.LightGray), 1),
            box, 4, 4);
        dc.DrawText(label, new Point(box.Left + 6, box.Top + 3));
    }

    internal static Color EventColor(UpsEventKind kind) => kind switch
    {
        UpsEventKind.GridLost => Color.FromRgb(0xF5, 0x9E, 0x0B),
        UpsEventKind.BatteryLow or UpsEventKind.UpsUnreachable => Color.FromRgb(0xDC, 0x26, 0x26),
        UpsEventKind.GridRestored or UpsEventKind.UpsReachable => Color.FromRgb(0x16, 0xA3, 0x4A),
        _ => Colors.Gray,
    };
}
