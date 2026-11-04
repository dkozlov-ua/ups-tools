using System.IO;
using Drawing = System.Drawing;

namespace UpsTools.Services;

public enum TrayState
{
    Neutral,
    GridOnline,
    OnBattery,
    Problem,
}

/// <summary>The app icon, optionally with a coloured status dot in the lower right corner.</summary>
internal static class StatusArt
{
    public static Stream OpenAppIcon() =>
        System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;

    /// <summary>The app icon at <paramref name="size"/> pixels, scaled from its largest frame.</summary>
    public static Drawing.Bitmap Render(int size, TrayState state)
    {
        using var stream = OpenAppIcon();
        using var icon = new Drawing.Icon(stream, 256, 256);
        using var source = icon.ToBitmap();
        var bitmap = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(source, 0, 0, size, size);
        }
        AddDot(bitmap, state, 0.42f);
        return bitmap;
    }

    /// <summary>Draws the status dot for <paramref name="state"/>; nothing for <see cref="TrayState.Neutral"/>.</summary>
    public static void AddDot(Drawing.Bitmap bitmap, TrayState state, float fraction)
    {
        var color = state switch
        {
            TrayState.GridOnline => Drawing.Color.FromArgb(0x16, 0xA3, 0x4A),
            TrayState.OnBattery => Drawing.Color.FromArgb(0xF5, 0x9E, 0x0B),
            TrayState.Problem => Drawing.Color.FromArgb(0xDC, 0x26, 0x26),
            _ => Drawing.Color.Empty,
        };
        if (color == Drawing.Color.Empty) return;

        using var g = Drawing.Graphics.FromImage(bitmap);
        g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var d = bitmap.Width * fraction;
        var pen = Math.Max(1f, bitmap.Width / 16f);
        // The rim is drawn centred on the edge: keep its outer half inside the bitmap.
        var size = d - pen / 2;
        var rect = new Drawing.RectangleF(bitmap.Width - d, bitmap.Height - d, size, size);
        using var fill = new Drawing.SolidBrush(color);
        using var border = new Drawing.Pen(Drawing.Color.White, pen);
        g.FillEllipse(fill, rect);
        g.DrawEllipse(border, rect);
    }
}
