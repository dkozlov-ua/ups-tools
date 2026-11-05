using UpsTools.AppTests.Support;
using UpsTools.Services;
using Drawing = System.Drawing;

namespace UpsTools.AppTests;

/// <summary>The tray icon's colours and the notification images.</summary>
public class TrayArtTests
{
    private static readonly Drawing.Color TileDark = Drawing.Color.FromArgb(0x0E, 0x74, 0x90);
    private static readonly Drawing.Color TileLight = Drawing.Color.FromArgb(0x34, 0xD3, 0x99);

    /// <summary>One pixel per colour: the tile's two ends, the white bolt, black, and a transparent one.</summary>
    private static Drawing.Bitmap Swatch()
    {
        var bitmap = new Drawing.Bitmap(5, 1, Drawing.Imaging.PixelFormat.Format32bppArgb);
        bitmap.SetPixel(0, 0, TileDark);
        bitmap.SetPixel(1, 0, TileLight);
        bitmap.SetPixel(2, 0, Drawing.Color.White);
        bitmap.SetPixel(3, 0, Drawing.Color.Black);
        bitmap.SetPixel(4, 0, Drawing.Color.FromArgb(0, 0x12, 0x34, 0x56));
        return bitmap;
    }

    private static int Rgb(Drawing.Color c) => c.ToArgb() & 0xFFFFFF;

    /// <summary>The average colour of the icon's tile: its opaque pixels that aren't the white bolt.</summary>
    private static Drawing.Color TileColour(Drawing.Icon icon)
    {
        using var bitmap = icon.ToBitmap();
        long r = 0, g = 0, b = 0, n = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.A < 250 || Math.Min(c.R, Math.Min(c.G, c.B)) > 200) continue;
            (r, g, b, n) = (r + c.R, g + c.G, b + c.B, n + 1);
        }
        Assert.True(n > 0);
        return Drawing.Color.FromArgb((int)(r / n), (int)(g / n), (int)(b / n));
    }

    [Theory]
    [InlineData(TrayState.GridOnline, 0x15803D, 0x4ADE80)]
    [InlineData(TrayState.OnBattery, 0xCA8A04, 0xFACC15)]
    [InlineData(TrayState.Problem, 0xB91C1C, 0xF87171)]
    [InlineData(TrayState.Neutral, 0x4B5563, 0x9CA3AF)]
    public void The_tile_takes_the_states_colours_and_the_bolt_stays_white(TrayState state, int dark, int light)
    {
        using var bitmap = Swatch();
        StatusArt.Recolor(bitmap, state);

        Assert.Equal(dark, Rgb(bitmap.GetPixel(0, 0)));
        Assert.Equal(light, Rgb(bitmap.GetPixel(1, 0)));
        Assert.Equal(0xFFFFFF, Rgb(bitmap.GetPixel(2, 0)));
        Assert.Equal(0x000000, Rgb(bitmap.GetPixel(3, 0)));
        Assert.Equal(Drawing.Color.FromArgb(0, 0x12, 0x34, 0x56), bitmap.GetPixel(4, 0)); // untouched
        Assert.Equal(255, bitmap.GetPixel(0, 0).A);
    }

    [Fact]
    public void Shades_between_the_ends_stay_in_order()
    {
        using var bitmap = new Drawing.Bitmap(3, 1, Drawing.Imaging.PixelFormat.Format32bppArgb);
        bitmap.SetPixel(0, 0, Drawing.Color.FromArgb(0x10, 0x10, 0x10));
        bitmap.SetPixel(1, 0, Drawing.Color.FromArgb(0x21, 0xA3, 0x95)); // between the tile's ends
        bitmap.SetPixel(2, 0, Drawing.Color.FromArgb(0xE0, 0xF0, 0xE0));
        StatusArt.Recolor(bitmap, TrayState.GridOnline);

        var g = Enumerable.Range(0, 3).Select(x => bitmap.GetPixel(x, 0).G).ToArray();
        Assert.True(g[0] < 0x80 && 0x80 < g[1] && g[1] < 0xDE && 0xDE < g[2], string.Join(",", g));
    }

    [Theory]
    [InlineData(TrayState.GridOnline, 0x16A34A)]
    [InlineData(TrayState.OnBattery, 0xF59E0B)]
    [InlineData(TrayState.Problem, 0xDC2626)]
    public async Task Notification_images_have_a_status_dot_in_the_corner(TrayState state, int dot)
    {
        using var bitmap = await UiThread.Run(() => StatusArt.Render(128, state));

        Assert.Equal((128, 128), (bitmap.Width, bitmap.Height));
        Assert.Equal(dot, Rgb(bitmap.GetPixel(100, 100)));
        Assert.Equal(255, bitmap.GetPixel(64, 64).A); // the icon itself
    }

    [Fact]
    public async Task The_neutral_image_has_no_dot()
    {
        using var neutral = await UiThread.Run(() => StatusArt.Render(64, TrayState.Neutral));
        using var plain = new Drawing.Bitmap(64, 64);
        StatusArt.AddDot(plain, TrayState.Neutral, 0.42f);
        Assert.Equal(0, plain.GetPixel(50, 50).A);
        Assert.NotEqual(0xDC2626, Rgb(neutral.GetPixel(50, 50)));
    }

    [Fact]
    public async Task The_tray_icon_is_recoloured_per_state_and_shows_the_status()
    {
        await UiThread.Run(() =>
        {
            var opened = 0;
            using var tray = new TrayIcon(() => opened++, () => { });

            Assert.Equal((TrayState.Neutral, "UPS Tools", "Starting…"), tray.Current);
            var centres = tray.Icons.ToDictionary(i => i.Key, i => TileColour(i.Value));
            Assert.Equal(4, centres.Values.Select(Rgb).Distinct().Count());
            Assert.True(centres[TrayState.GridOnline].G > centres[TrayState.GridOnline].R);
            Assert.True(centres[TrayState.Problem].R > centres[TrayState.Problem].G);
            Assert.True(centres[TrayState.OnBattery].R > 0xA0 && centres[TrayState.OnBattery].G > 0x70);

            tray.Update(TrayState.OnBattery, "On battery · 80 % · 2 h 10 m left");
            Assert.Equal((TrayState.OnBattery, "UPS Tools — On battery · 80 % · 2 h 10 m left", "On battery · 80 % · 2 h 10 m left"), tray.Current);

            var longStatus = new string('x', 200);
            tray.Update(TrayState.Problem, longStatus);
            Assert.Equal(127, tray.Current.Tooltip.Length); // Windows' limit
            Assert.Equal(longStatus, tray.Current.Status);
        });
    }
}
