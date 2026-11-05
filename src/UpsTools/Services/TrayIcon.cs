using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace UpsTools.Services;

/// <summary>Shows the UPS state outside the window.</summary>
public interface ITray
{
    void Update(TrayState state, string status);

    /// <summary>A balloon, for when Windows notifications aren't available.</summary>
    void Notify(string title, string text, bool warning = false);
}

/// <summary>Notification-area icon, coloured by the UPS state, with a small context menu.</summary>
public sealed class TrayIcon : ITray, IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _statusItem;
    private readonly Dictionary<TrayState, Drawing.Icon> _icons = [];

    public TrayIcon(Action open, Action exit)
    {
        using var stream = StatusArt.OpenAppIcon();
        using var baseIcon = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        foreach (var state in Enum.GetValues<TrayState>()) _icons[state] = Recolored(baseIcon, state);

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

    /// <summary>The icon's state, tooltip and the status line of its menu (for the tests).</summary>
    internal (TrayState State, string Tooltip, string Status) Current => (_state, _icon.Text, _statusItem.Text ?? "");

    /// <summary>The icon shown for each state (for the tests).</summary>
    internal IReadOnlyDictionary<TrayState, Drawing.Icon> Icons => _icons;

    private TrayState _state;

    public void Update(TrayState state, string status)
    {
        _state = state;
        _icon.Icon = _icons[state];
        var text = $"UPS Tools — {status}";
        _icon.Text = text.Length > 127 ? text[..127] : text;
        _statusItem.Text = status;
    }

    public void Notify(string title, string text, bool warning = false) =>
        _icon.ShowBalloonTip(5000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    internal static Drawing.Icon Recolored(Drawing.Icon baseIcon, TrayState state)
    {
        using var bitmap = baseIcon.ToBitmap();
        StatusArt.Recolor(bitmap, state);

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
