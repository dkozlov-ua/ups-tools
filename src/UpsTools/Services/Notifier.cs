using System.IO;
using System.Xml.Linq;
using Microsoft.Win32;
using UpsTools.Core;
using Windows.UI.Notifications;
using Drawing = System.Drawing;

namespace UpsTools.Services;

/// <summary>
/// Windows notifications (toasts). UPS Tools isn't a packaged app, so it registers its own
/// AppUserModelID under HKCU\Software\Classes\AppUserModelId: that gives the notifications the
/// app's name and icon in their header and in Settings → System → Notifications.
/// When toasts can't be shown, the tray icon's balloon is used instead.
/// </summary>
public interface INotifications
{
    void Show(UpsNotice notice, string? attribution);

    /// <param name="tag">A later notification with the same tag replaces this one.</param>
    void Show(string tag, TrayState state, string title, string body, string? footnote = null,
        string? attribution = null, bool important = false);
}

public sealed class Notifier : INotifications
{
    /// <summary>DKozlov.UpsTools, or DKozlov.UpsTools-name for a profile (<see cref="AppProfile"/>).</summary>
    public static string AppUserModelId { get; } = "DKozlov.UpsTools" + AppProfile.Suffix;
    private static string RegistrationKey => @"Software\Classes\AppUserModelId\" + AppUserModelId;
    private const string Group = "ups";

    /// <summary>Generated images the notifications point at (not settings, so not roaming).</summary>
    public static string CacheFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpsTools" + AppProfile.Suffix);

    private readonly AppLog _log;
    private readonly ITray _tray;
    private readonly Action _activated;
    private readonly Dictionary<TrayState, string?> _logos = [];
    private readonly Dictionary<string, ToastNotification> _shown = [];
    private ToastNotifier? _toasts;
    private bool _toastsUnavailable;

    /// <param name="activated">Called, on any thread, when the user clicks a notification.</param>
    public Notifier(AppLog log, ITray tray, Action activated)
    {
        _log = log;
        _tray = tray;
        _activated = activated;
    }

    public void Show(UpsNotice notice, string? attribution) =>
        Show(notice.Tag, ToTray(notice.Tone), notice.Title, notice.Body, notice.Footnote, attribution, notice.Important);

    /// <param name="tag">A later notification with the same tag replaces this one.</param>
    public void Show(string tag, TrayState state, string title, string body, string? footnote = null,
        string? attribution = null, bool important = false)
    {
        if (TryShowToast(tag, state, title, body, footnote, attribution, important)) return;
        var text = footnote is null ? body : $"{body}\n{footnote}";
        _tray.Notify(title, text, warning: state is TrayState.OnBattery or TrayState.Problem);
    }

    private bool TryShowToast(string tag, TrayState state, string title, string body, string? footnote,
        string? attribution, bool important)
    {
        if (_toastsUnavailable) return false;
        try
        {
            _toasts ??= Register();

            var xml = new Windows.Data.Xml.Dom.XmlDocument();
            xml.LoadXml(ToastXml(title, body, footnote, attribution, Logo(state), important).ToString(SaveOptions.DisableFormatting));
            var notification = new ToastNotification(xml) { Tag = tag, Group = Group };
            notification.Activated += (_, _) => _activated();
            notification.Failed += (_, e) => _log.Warn($"Notification \"{title}\" failed: 0x{e.ErrorCode.HResult:X8}");

            _toasts.Show(notification);
            _shown[tag] = notification; // keeps the Activated handler alive
            return true;
        }
        catch (Exception ex)
        {
            _toastsUnavailable = true;
            _log.Warn($"Windows notifications are unavailable ({ex.Message}); using tray balloons instead");
            return false;
        }
    }

    /// <summary>The toast's content: title, body, then the footnote, with the attribution and the logo.</summary>
    internal static XElement ToastXml(string title, string body, string? footnote, string? attribution, string? logo, bool important)
    {
        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"),
            new XElement("text", title),
            new XElement("text", body));
        if (footnote is not null) binding.Add(new XElement("text", footnote));
        if (attribution is not null) binding.Add(new XElement("text", new XAttribute("placement", "attribution"), attribution));
        if (logo is not null)
            binding.Add(new XElement("image", new XAttribute("placement", "appLogoOverride"), new XAttribute("src", logo)));

        var toast = new XElement("toast", new XAttribute("launch", "open"), new XElement("visual", binding));
        if (important) toast.Add(new XAttribute("duration", "long"));
        return toast;
    }

    private ToastNotifier Register()
    {
        Directory.CreateDirectory(CacheFolder);
        var icon = SavePng("app.png", TrayState.Neutral);
        using (var key = Registry.CurrentUser.CreateSubKey(RegistrationKey))
        {
            key.SetValue("DisplayName", AppProfile.Name is { } profile ? $"UPS Tools ({profile})" : "UPS Tools");
            key.SetValue("IconUri", icon);
        }
        return ToastNotificationManager.CreateToastNotifier(AppUserModelId);
    }

    /// <summary>Removes the registration, the images, and the notifications still in Notification Center.</summary>
    public static void Unregister(AppLog log)
    {
        try { ToastNotificationManager.History.Clear(AppUserModelId); }
        catch { /* never registered, or nothing shown */ }

        bool registered;
        using (var key = Registry.CurrentUser.OpenSubKey(RegistrationKey)) registered = key is not null;
        if (registered)
        {
            Registry.CurrentUser.DeleteSubKeyTree(RegistrationKey, throwOnMissingSubKey: false);
            log.Info("Removed the notification registration");
        }

        if (!Directory.Exists(CacheFolder)) return;
        foreach (var file in Directory.EnumerateFiles(CacheFolder, "*.png"))
        {
            var name = Path.GetFileName(file);
            if (name == "app.png" || name.StartsWith("logo-", StringComparison.Ordinal)) File.Delete(file);
        }
        if (!Directory.EnumerateFileSystemEntries(CacheFolder).Any()) Directory.Delete(CacheFolder);
    }

    private string? Logo(TrayState state)
    {
        if (_logos.TryGetValue(state, out var uri)) return uri;
        try
        {
            uri = new Uri(SavePng($"logo-{state.ToString().ToLowerInvariant()}.png", state)).AbsoluteUri;
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not write the notification image: {ex.Message}");
            uri = null;
        }
        return _logos[state] = uri;
    }

    private static string SavePng(string name, TrayState state)
    {
        var path = Path.Combine(CacheFolder, name);
        using var bitmap = StatusArt.Render(128, state);
        bitmap.Save(path, Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    internal static TrayState ToTray(NoticeTone tone) => tone switch
    {
        NoticeTone.Good => TrayState.GridOnline,
        NoticeTone.Warning => TrayState.OnBattery,
        NoticeTone.Problem => TrayState.Problem,
        _ => TrayState.Neutral,
    };
}
