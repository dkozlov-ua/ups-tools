// Generates src/UpsTools/Assets/app.ico (and a preview PNG).
// Usage (from the repo root):  dotnet run tools/make-icon.cs
#:package System.Drawing.Common@10.0.0
#:property TargetFramework=net10.0-windows10.0.19041.0
#pragma warning disable CA1416 // Windows-only APIs; this tool only runs on Windows.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var root = Directory.GetCurrentDirectory();
var icoPath = Path.Combine(root, "src", "UpsTools", "Assets", "app.ico");
var previewPath = Path.Combine(root, "tools", "app-icon-preview.png");
Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];
var frames = sizes.Select(s => (Size: s, Bitmap: Render(s))).ToList();
WriteIco(icoPath, frames);

using (var preview = new Bitmap(sizes.Sum() + 10 * sizes.Length + 10, 276))
using (var g = Graphics.FromImage(preview))
{
    g.Clear(Color.FromArgb(0xF3, 0xF3, 0xF3));
    var x = 10;
    foreach (var (size, bitmap) in frames)
    {
        g.DrawImageUnscaled(bitmap, x, 266 - size);
        x += size + 10;
    }
    preview.Save(previewPath, ImageFormat.Png);
}
Console.WriteLine($"Wrote {icoPath}");

// ---- drawing ----

static Bitmap Render(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);

    var small = size <= 24;
    var s = size / 256f;
    var inset = small ? 0.5f : 10 * s;
    var tile = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
    var radius = (small ? 64 : 58) * s;

    using var tilePath = RoundedRect(tile, radius);

    // Emerald → teal tile.
    using (var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(size, size),
               Color.FromArgb(0x34, 0xD3, 0x99), Color.FromArgb(0x0E, 0x74, 0x90)))
    {
        g.FillPath(fill, tilePath);
    }

    if (!small)
    {
        // Soft glossy highlight on the upper half.
        using var clip = new Region(tilePath);
        g.SetClip(clip, CombineMode.Replace);
        using var gloss = new LinearGradientBrush(new PointF(0, tile.Top), new PointF(0, tile.Top + tile.Height * 0.55f),
            Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
        g.FillRectangle(gloss, tile.Left, tile.Top, tile.Width, tile.Height * 0.55f);
        g.ResetClip();

        using var rim = new Pen(Color.FromArgb(60, 0, 0, 0), Math.Max(1, 3 * s));
        g.DrawPath(rim, tilePath);
    }

    // Lightning bolt, centred on (128,128) in a 256 design grid.
    PointF[] bolt =
    [
        new(148, 30), new(66, 146), new(122, 146), new(104, 226),
        new(190, 104), new(134, 104), new(166, 30),
    ];
    var scale = small ? 1.12f : 1f;
    var points = bolt.Select(p => new PointF((128 + (p.X - 128) * scale) * s, (128 + (p.Y - 128) * scale) * s)).ToArray();

    if (!small)
    {
        var shadowOffset = 6 * s;
        using var shadow = new SolidBrush(Color.FromArgb(70, 0, 40, 50));
        g.FillPolygon(shadow, points.Select(p => new PointF(p.X, p.Y + shadowOffset)).ToArray());
    }

    using (var white = new SolidBrush(Color.White))
    {
        g.FillPolygon(white, points);
    }

    return bmp;
}

static GraphicsPath RoundedRect(RectangleF r, float radius)
{
    var d = radius * 2;
    var path = new GraphicsPath();
    path.AddArc(r.Left, r.Top, d, d, 180, 90);
    path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}

// ---- ICO writer: 32-bit DIB frames below 256 px (widest compatibility), PNG for 256 px ----

static void WriteIco(string path, List<(int Size, Bitmap Bitmap)> frames)
{
    var images = frames.Select(f => (f.Size, Data: f.Size >= 256 ? Png(f.Bitmap) : Dib(f.Bitmap))).ToList();

    using var stream = File.Create(path);
    using var w = new BinaryWriter(stream);
    w.Write((ushort)0);
    w.Write((ushort)1);
    w.Write((ushort)images.Count);

    var offset = 6 + 16 * images.Count;
    foreach (var (size, data) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(data.Length);
        w.Write(offset);
        offset += data.Length;
    }
    foreach (var (_, data) in images) w.Write(data);
}

static byte[] Png(Bitmap bmp)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static byte[] Dib(Bitmap bmp)
{
    var size = bmp.Width;
    var maskStride = (size + 31) / 32 * 4;
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);

    // BITMAPINFOHEADER; height is doubled to cover the AND mask.
    w.Write(40);
    w.Write(size);
    w.Write(size * 2);
    w.Write((ushort)1);
    w.Write((ushort)32);
    w.Write(0);
    w.Write(size * size * 4 + maskStride * size);
    w.Write(0);
    w.Write(0);
    w.Write(0);
    w.Write(0);

    for (var y = size - 1; y >= 0; y--)
    for (var x = 0; x < size; x++)
    {
        var c = bmp.GetPixel(x, y);
        w.Write(c.B);
        w.Write(c.G);
        w.Write(c.R);
        w.Write(c.A);
    }

    for (var y = size - 1; y >= 0; y--)
    {
        var row = new byte[maskStride];
        for (var x = 0; x < size; x++)
        {
            if (bmp.GetPixel(x, y).A == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
        }
        w.Write(row);
    }

    return ms.ToArray();
}
