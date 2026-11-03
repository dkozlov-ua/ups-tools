using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace UpsTools.Services;

public enum TrayState
{
    Neutral,
    GridOnline,
    OnBattery,
    Problem,
}

/// <summary>Notification-area icon with a coloured status dot and a small context menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _statusItem;
    private readonly Dictionary<TrayState, Drawing.Icon> _icons = [];

    public TrayIcon(Action open, Action exit)
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        using var baseIcon = new Drawing.Icon(info.Stream, Forms.SystemInformation.SmallIconSize);
        foreach (var state in Enum.GetValues<TrayState>()) _icons[state] = WithStatusDot(baseIcon, state);

        _statusItem = new Forms.ToolStripMenuItem("Starting…") { Enabled = false };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Open UPS Tools", null, (_, _) => open());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Icon = _icons[TrayState.Neutral],
            Text = "UPS Tools",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) open();
        };
    }

    public void Update(TrayState state, string status)
    {
        _icon.Icon = _icons[state];
        var text = $"UPS Tools — {status}";
        _icon.Text = text.Length > 127 ? text[..127] : text;
        _statusItem.Text = status;
    }

    public void Notify(string title, string text, bool warning = false) =>
        _icon.ShowBalloonTip(5000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    private static Drawing.Icon WithStatusDot(Drawing.Icon baseIcon, TrayState state)
    {
        using var bitmap = baseIcon.ToBitmap();
        var color = state switch
        {
            TrayState.GridOnline => Drawing.Color.FromArgb(0x16, 0xA3, 0x4A),
            TrayState.OnBattery => Drawing.Color.FromArgb(0xF5, 0x9E, 0x0B),
            TrayState.Problem => Drawing.Color.FromArgb(0xDC, 0x26, 0x26),
            _ => Drawing.Color.Empty,
        };

        if (color != Drawing.Color.Empty)
        {
            using var g = Drawing.Graphics.FromImage(bitmap);
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var d = bitmap.Width * 0.5f;
            var rect = new Drawing.RectangleF(bitmap.Width - d, bitmap.Height - d, d - 0.5f, d - 0.5f);
            using var fill = new Drawing.SolidBrush(color);
            using var border = new Drawing.Pen(Drawing.Color.White, Math.Max(1f, bitmap.Width / 16f));
            g.FillEllipse(fill, rect);
            g.DrawEllipse(border, rect);
        }

        var handle = bitmap.GetHicon();
        try
        {
            return (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
    }
}
