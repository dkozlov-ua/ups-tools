using System.IO;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;

namespace UpsTools.Services;

public enum TrayState
{
    Neutral,
    GridOnline,
    OnBattery,
    Problem,
}

/// <summary>The app icon, recoloured or with a coloured status dot to show a <see cref="TrayState"/>.</summary>
internal static class StatusArt
{
    // The app icon's tile is a gradient between these two colours (see tools/make-icon.cs), with a white bolt.
    private static readonly double TileLight = Luma(0x34, 0xD3, 0x99);
    private static readonly double TileDark = Luma(0x0E, 0x74, 0x90);

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

    /// <summary>
    /// Recolours the app icon's tile for <paramref name="state"/>: green, yellow, red, or grey for
    /// <see cref="TrayState.Neutral"/>. The bolt stays white, and the tile keeps its gradient and shading.
    /// </summary>
    /// <remarks>
    /// A gradient map: each pixel's brightness picks a colour on black → dark → light → white, where the tile's
    /// own dark and light ends map to the new dark and light colours.
    /// </remarks>
    public static void Recolor(Drawing.Bitmap bitmap, TrayState state)
    {
        var (light, dark) = state switch
        {
            TrayState.GridOnline => (Drawing.Color.FromArgb(0x4A, 0xDE, 0x80), Drawing.Color.FromArgb(0x15, 0x80, 0x3D)),
            TrayState.OnBattery => (Drawing.Color.FromArgb(0xFA, 0xCC, 0x15), Drawing.Color.FromArgb(0xCA, 0x8A, 0x04)),
            TrayState.Problem => (Drawing.Color.FromArgb(0xF8, 0x71, 0x71), Drawing.Color.FromArgb(0xB9, 0x1C, 0x1C)),
            _ => (Drawing.Color.FromArgb(0x9C, 0xA3, 0xAF), Drawing.Color.FromArgb(0x4B, 0x55, 0x63)),
        };

        var rect = new Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, Drawing.Imaging.ImageLockMode.ReadWrite, Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            for (var i = 0; i < pixels.Length; i += 4) // BGRA
            {
                if (pixels[i + 3] == 0) continue;
                var y = Luma(pixels[i + 2], pixels[i + 1], pixels[i]);
                var (from, to, t) =
                    y < TileDark ? (Drawing.Color.Black, dark, y / TileDark)
                    : y < TileLight ? (dark, light, (y - TileDark) / (TileLight - TileDark))
                    : (light, Drawing.Color.White, (y - TileLight) / (1 - TileLight));
                pixels[i + 2] = Lerp(from.R, to.R, t);
                pixels[i + 1] = Lerp(from.G, to.G, t);
                pixels[i] = Lerp(from.B, to.B, t);
            }
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
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

    private static double Luma(int r, int g, int b) => (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;

    private static byte Lerp(int from, int to, double t) =>
        (byte)Math.Round(from + (to - from) * Math.Clamp(t, 0, 1));
}
