using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UpsTools.AppTests.Support;
using UpsTools.Core;
using UpsTools.Core.History;
using UpsTools.Views;

namespace UpsTools.AppTests;

/// <summary>
/// The History chart, drawn off screen: its lines and spans land where the readings say. With UPSTOOLS_SAVE_RENDER
/// set to a folder, the picture is saved there to look at.
/// </summary>
public class HistoryChartViewTests
{
    private static UpsHistory DayWithAnOutage()
    {
        var history = new UpsHistory(null);
        var start = DateTimeOffset.Now.AddHours(-23);
        var battery = 100.0;
        for (var i = 0; i < 23 * 60; i++)
        {
            var at = start.AddMinutes(i);
            var grid = i is not (>= 600 and < 700);
            battery = grid ? Math.Min(100, battery + 0.5) : Math.Max(10, battery - 0.6);
            history.Record(new UpsStatus { Timestamp = at, GridPresent = grid, BatteryPercent = (int)battery, AcOutputW = 120 + 60 * Math.Sin(i / 90.0) });
        }
        history.Record(UpsEventKind.GridLost, start.AddMinutes(600));
        history.Record(UpsEventKind.GridRestored, start.AddMinutes(700));
        return history;
    }

    private static BitmapSource Render(HistoryChartView view, int width, int height)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen()) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(view);
        bitmap.Freeze();
        return bitmap;
    }

    private static int Count(BitmapSource bitmap, Func<Color, bool> match)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (match(Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]))) count++;
        return count;
    }

    private static bool Near(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) < 60;

    [Fact]
    public async Task A_day_shows_both_lines_and_the_outage()
    {
        var bitmap = await UiThread.Run(() =>
        {
            var view = new HistoryChartView { History = DayWithAnOutage(), Range = TimeSpan.FromHours(24) };
            var rendered = Render(view, 900, 320);
            Save(rendered, "history-24h.png");
            return rendered;
        });

        Assert.True(Count(bitmap, c => Near(c, HistoryChartView.BatteryColor)) > 500, "the battery line");
        Assert.True(Count(bitmap, c => Near(c, HistoryChartView.LoadColor)) > 500, "the load line");
        // The outage's shading over white: a light amber.
        Assert.True(Count(bitmap, c => c.R > 240 && c.G is > 200 and < 245 && c.B < 215) > 2000, "the outage");
    }

    [Fact]
    public async Task Without_readings_it_says_so_instead()
    {
        var bitmap = await UiThread.Run(() => Render(new HistoryChartView { History = new UpsHistory(null) }, 600, 240));
        Assert.True(Count(bitmap, c => Near(c, HistoryChartView.BatteryColor)) < 200, "no line, only the axis labels");
        Assert.True(Count(bitmap, c => c.R < 200) > 50, "the message and the axes");
    }

    private static void Save(BitmapSource bitmap, string name)
    {
        if (Environment.GetEnvironmentVariable("UPSTOOLS_SAVE_RENDER") is not { Length: > 0 } folder) return;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name));
        encoder.Save(file);
    }
}
