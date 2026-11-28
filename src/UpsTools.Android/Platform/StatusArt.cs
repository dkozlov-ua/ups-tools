using Android.Graphics;
using UpsTools.Services;
using Color = Android.Graphics.Color;
using Path = Android.Graphics.Path;

namespace UpsTools.Droid.Platform;

/// <summary>
/// The app icon's tile recoloured for a <see cref="TrayState"/>, as the tray icon on Windows (StatusArt.Recolor there):
/// green on the grid, yellow on battery, red for a problem, grey otherwise; the bolt stays white. Android draws the
/// status bar's icons in one colour, so it shows in the notification itself (its large icon).
/// </summary>
internal static class StatusArt
{
    private static readonly Dictionary<(TrayState, int), Bitmap> Cache = [];

    /// <summary>The light and dark ends of the tile's gradient: the ones the tray uses on Windows.</summary>
    private static (Color Light, Color Dark) Colors(TrayState state) => state switch
    {
        TrayState.GridOnline => (Color.Rgb(0x4A, 0xDE, 0x80), Color.Rgb(0x15, 0x80, 0x3D)),
        TrayState.OnBattery => (Color.Rgb(0xFA, 0xCC, 0x15), Color.Rgb(0xCA, 0x8A, 0x04)),
        TrayState.Problem => (Color.Rgb(0xF8, 0x71, 0x71), Color.Rgb(0xB9, 0x1C, 0x1C)),
        _ => (Color.Rgb(0x9C, 0xA3, 0xAF), Color.Rgb(0x4B, 0x55, 0x63)),
    };

    /// <summary>The bolt of tools/make-icon.cs, on its 256-unit grid.</summary>
    private static readonly (float X, float Y)[] Bolt =
        [(148, 30), (66, 146), (122, 146), (104, 226), (190, 104), (134, 104), (166, 30)];

    /// <summary>The tile at <paramref name="size"/> pixels; made once per state and size.</summary>
    public static Bitmap Tile(TrayState state, int size)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((state, size), out var cached)) return cached;
            var bitmap = Draw(state, size);
            Cache[(state, size)] = bitmap;
            return bitmap;
        }
    }

    /// <summary>Draws it as tools/make-icon.cs draws the app icon: gradient tile, gloss, rim, shadowed white bolt.</summary>
    private static Bitmap Draw(TrayState state, int size)
    {
        var bitmap = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bitmap);
        var s = size / 256f;
        var inset = 10 * s;
        var tile = new RectF(inset, inset, size - inset, size - inset);
        var radius = 58 * s;
        var (light, dark) = Colors(state);

        using (var fill = new Paint(PaintFlags.AntiAlias))
        {
            fill.SetShader(new LinearGradient(0, 0, size, size, light, dark, Shader.TileMode.Clamp!));
            canvas.DrawRoundRect(tile, radius, radius, fill);
        }

        // Soft gloss on the upper half.
        using (var clip = new Path())
        using (var gloss = new Paint(PaintFlags.AntiAlias))
        {
            clip.AddRoundRect(tile, radius, radius, Path.Direction.Cw!);
            canvas.Save();
            canvas.ClipPath(clip);
            var bottom = tile.Top + tile.Height() * 0.55f;
            gloss.SetShader(new LinearGradient(0, tile.Top, 0, bottom, Color.Argb(70, 255, 255, 255), Color.Argb(0, 255, 255, 255),
                Shader.TileMode.Clamp!));
            canvas.DrawRect(tile.Left, tile.Top, tile.Right, bottom, gloss);
            canvas.Restore();
        }

        using (var rim = new Paint(PaintFlags.AntiAlias))
        {
            rim.SetStyle(Paint.Style.Stroke);
            rim.StrokeWidth = Math.Max(1, 3 * s);
            rim.Color = Color.Argb(60, 0, 0, 0);
            canvas.DrawRoundRect(tile, radius, radius, rim);
        }

        using (var shadow = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(70, 0, 40, 50) })
            canvas.DrawPath(BoltPath(s, 6 * s), shadow);
        using (var white = new Paint(PaintFlags.AntiAlias) { Color = Color.White })
            canvas.DrawPath(BoltPath(s, 0), white);
        return bitmap;
    }

    private static Path BoltPath(float scale, float dy)
    {
        var path = new Path();
        path.MoveTo(Bolt[0].X * scale, Bolt[0].Y * scale + dy);
        foreach (var (x, y) in Bolt.Skip(1)) path.LineTo(x * scale, y * scale + dy);
        path.Close();
        return path;
    }
}
