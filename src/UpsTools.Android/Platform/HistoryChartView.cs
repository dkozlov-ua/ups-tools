using System.Globalization;
using Android.Content;
using Android.Graphics;
using Android.Views;
using UpsTools.Core;
using UpsTools.Core.History;
using Color = Android.Graphics.Color;
using View = Android.Views.View;

namespace UpsTools.Droid.Platform;

/// <summary>
/// The History chart of one UPS (<see cref="HistoryChart"/>, worked out in Core, as on Windows): the charge and the
/// load over <see cref="Range"/>, outages shaded, stretches without readings greyed, events marked. Touching it
/// shows the reading there.
/// </summary>
public sealed class HistoryChartView : View
{
    public static readonly Color BatteryColor = Color.Rgb(0x16, 0xA3, 0x4A);
    public static readonly Color LoadColor = Color.Rgb(0x3B, 0x82, 0xF6);
    public static readonly Color OutageColor = Color.Argb(0x48, 0xF5, 0x9E, 0x0B);
    public static readonly Color MissingColor = Color.Argb(0x38, 0x80, 0x80, 0x80);

    private readonly Paint _fill = new(PaintFlags.AntiAlias);
    private readonly Paint _stroke = new(PaintFlags.AntiAlias);
    private readonly Paint _text = new(PaintFlags.AntiAlias);
    private float? _touch;

    public HistoryChartView(Context context) : base(context)
    {
        _stroke.SetStyle(Paint.Style.Stroke);
        _stroke.StrokeJoin = Paint.Join.Round;
        _stroke.StrokeCap = Paint.Cap.Round;
        _text.TextSize = Ui.Dp(context, 11);
    }

    public UpsHistory? History { get; set; }

    public TimeSpan Range { get; set; } = TimeSpan.FromHours(24);

    public static Color EventColor(UpsEventKind kind) => kind switch
    {
        UpsEventKind.GridLost => Color.Rgb(0xF5, 0x9E, 0x0B),
        UpsEventKind.BatteryLow or UpsEventKind.UpsUnreachable => Color.Rgb(0xDC, 0x26, 0x26),
        _ => Color.Rgb(0x16, 0xA3, 0x4A),
    };

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        _touch = e.Action is MotionEventActions.Up or MotionEventActions.Cancel ? null : e.GetX();
        Parent?.RequestDisallowInterceptTouchEvent(_touch is not null); // the screen doesn't scroll while reading the chart
        Invalidate();
        return true;
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        var c = Context!;
        var to = DateTimeOffset.Now;
        var from = to - Range;
        var points = History?.Points(from) ?? [];
        var chart = HistoryChart.Build(points, History?.Events(from) ?? [], from, to);

        float dp(float v) => Ui.Dp(c, v);
        var plot = new RectF(dp(36), dp(10), Width - dp(48), Height - dp(22));
        float X(double x) => plot.Left + (float)x * plot.Width();
        float Y(double y) => plot.Bottom - (float)y * plot.Height();

        _fill.Color = MissingColor;
        foreach (var span in chart.Missing) canvas.DrawRect(X(span.From), plot.Top, Math.Max(X(span.To), X(span.From) + 1), plot.Bottom, _fill);
        _fill.Color = OutageColor;
        foreach (var span in chart.Outages) canvas.DrawRect(X(span.From), plot.Top, Math.Max(X(span.To), X(span.From) + 1), plot.Bottom, _fill);

        _stroke.Color = Ui.Divider(c);
        _stroke.StrokeWidth = dp(1);
        _stroke.SetPathEffect(null);
        var secondary = Ui.Secondary(c);
        for (var i = 0; i < chart.BatteryTicks.Count; i++)
        {
            var y = Y(chart.BatteryTicks[i].At);
            canvas.DrawLine(plot.Left, y, plot.Right, y, _stroke);
            _text.Color = BatteryColor;
            _text.TextAlign = Paint.Align.Right;
            canvas.DrawText(chart.BatteryTicks[i].Label, plot.Left - dp(4), y + dp(4), _text);
            _text.Color = LoadColor;
            _text.TextAlign = Paint.Align.Left;
            canvas.DrawText(chart.LoadTicks[i].Label, plot.Right + dp(4), y + dp(4), _text);
        }
        _text.Color = secondary;
        _text.TextAlign = Paint.Align.Center;
        var lastLabel = float.MinValue;
        foreach (var tick in chart.TimeTicks)
        {
            var x = X(tick.At);
            var half = _text.MeasureText(tick.Label) / 2;
            if (x - half < lastLabel + dp(6)) continue; // a narrow screen shows every other label
            canvas.DrawText(tick.Label, Math.Clamp(x, half, Width - half), plot.Bottom + dp(15), _text);
            lastLabel = x + half;
        }

        DrawLines(canvas, chart.Load, LoadColor, dp(1.5f), X, Y);
        DrawLines(canvas, chart.Battery, BatteryColor, dp(2), X, Y);

        foreach (var e in chart.Events)
        {
            var color = EventColor(e.Kind);
            _stroke.Color = color;
            _stroke.StrokeWidth = dp(1);
            _stroke.SetPathEffect(new DashPathEffect([dp(3), dp(3)], 0));
            canvas.DrawLine(X(e.X), plot.Top, X(e.X), plot.Bottom, _stroke);
            _fill.Color = color;
            canvas.DrawCircle(X(e.X), plot.Top, dp(3.5f), _fill);
        }
        _stroke.SetPathEffect(null);

        if (chart.IsEmpty)
        {
            _text.Color = secondary;
            _text.TextSize = dp(13);
            canvas.DrawText("No readings in this range yet", plot.CenterX(), plot.CenterY(), _text);
            _text.TextSize = dp(11);
        }
        else if (_touch is { } touch && touch >= plot.Left && touch <= plot.Right)
        {
            DrawReading(canvas, points, from, to, plot, touch);
        }
    }

    private void DrawLines(Canvas canvas, IReadOnlyList<IReadOnlyList<ChartPoint>> lines, Color color, float width,
        Func<double, float> x, Func<double, float> y)
    {
        _stroke.Color = color;
        _stroke.StrokeWidth = width;
        foreach (var line in lines)
        {
            using var path = new Android.Graphics.Path();
            path.MoveTo(x(line[0].X), y(line[0].Y));
            if (line.Count == 1) path.LineTo(x(line[0].X) + 1, y(line[0].Y));
            for (var i = 1; i < line.Count; i++) path.LineTo(x(line[i].X), y(line[i].Y));
            canvas.DrawPath(path, _stroke);
        }
    }

    /// <summary>The reading nearest the finger, with a line down to it and its values above the chart.</summary>
    private void DrawReading(Canvas canvas, IReadOnlyList<HistoryPoint> points, DateTimeOffset from, DateTimeOffset to, RectF plot, float touch)
    {
        var c = Context!;
        var time = from + (to - from) * ((touch - plot.Left) / plot.Width());
        var nearest = points.MinBy(p => Math.Abs((p.Time - time).Ticks));
        if (nearest is null) return;
        var x = plot.Left + (float)((nearest.Time - from) / (to - from)) * plot.Width();
        _stroke.Color = Ui.Primary(c);
        _stroke.StrokeWidth = Ui.Dp(c, 1);
        canvas.DrawLine(x, plot.Top, x, plot.Bottom, _stroke);

        var parts = new[]
        {
            nearest.Time.ToLocalTime().ToString((to - from).TotalDays > 1 ? "ddd HH:mm" : "HH:mm", CultureInfo.CurrentCulture),
            nearest.BatteryPercent is { } b ? $"{b} %" : null,
            nearest.LoadW is { } w ? UpsFormat.Watts(w) : null,
            nearest.Grid switch { true => "grid", false => "on battery", _ => null },
        };
        var label = string.Join(" · ", parts.OfType<string>());
        _text.TextSize = Ui.Dp(c, 12);
        _text.TextAlign = Paint.Align.Left;
        var width = _text.MeasureText(label) + Ui.Dp(c, 12);
        var left = Math.Clamp(x + Ui.Dp(c, 8), plot.Left, plot.Right - width);
        var box = new RectF(left, plot.Top + Ui.Dp(c, 4), left + width, plot.Top + Ui.Dp(c, 26));
        _fill.Color = Ui.CardColor(c);
        canvas.DrawRoundRect(box, Ui.Dp(c, 6), Ui.Dp(c, 6), _fill);
        _stroke.Color = Ui.Divider(c);
        canvas.DrawRoundRect(box, Ui.Dp(c, 6), Ui.Dp(c, 6), _stroke);
        _text.Color = Ui.Primary(c);
        canvas.DrawText(label, box.Left + Ui.Dp(c, 6), box.Bottom - Ui.Dp(c, 7), _text);
        _text.TextSize = Ui.Dp(c, 11);
    }
}
