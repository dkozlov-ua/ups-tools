using Android.App;
using Android.Content;
using Android.Graphics;
using UpsTools.Core;
using UpsTools.Services;

namespace UpsTools.Droid.Platform;

/// <summary>
/// The notifications: one for each UPS event (a newer one about the same thing replaces the older: "Grid restored"
/// replaces "Grid lost"), and the status notification that stays while monitoring runs.
/// </summary>
public sealed class Notifier : IEventNotifications
{
    public const int StatusId = 1;

    /// <summary>Grid lost, battery low, UPS unreachable: they pop up and sound.</summary>
    private const string AlertsChannel = "alerts";

    /// <summary>Grid restored, UPS reachable again.</summary>
    private const string EventsChannel = "events";

    /// <summary>The status notification: silent, low in the list.</summary>
    public const string StatusChannel = "status";

    private readonly Context _context;
    private readonly NotificationManager _manager;

    public Notifier(Context context)
    {
        _context = context;
        _manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        _manager.CreateNotificationChannel(new NotificationChannel(AlertsChannel, "Power problems", NotificationImportance.High)
        {
            Description = "Grid lost, battery low, UPS unreachable",
            LockscreenVisibility = NotificationVisibility.Public,
        });
        _manager.CreateNotificationChannel(new NotificationChannel(EventsChannel, "Power back", NotificationImportance.Default)
        {
            Description = "Grid restored, UPS reachable again",
            LockscreenVisibility = NotificationVisibility.Public,
        });
        _manager.CreateNotificationChannel(new NotificationChannel(StatusChannel, "Monitoring status", NotificationImportance.Low)
        {
            Description = "Stays while UPS Tools watches your UPSes: their state at a glance",
            LockscreenVisibility = NotificationVisibility.Public,
        });
    }

    public static Color ColorOf(NoticeTone tone) => tone switch
    {
        NoticeTone.Good => Ui.Palette.Good,
        NoticeTone.Warning => Ui.Palette.Warning,
        NoticeTone.Problem => Ui.Palette.Bad,
        _ => Ui.Palette.Neutral,
    };

    public static Color ColorOf(TrayState state) => state switch
    {
        TrayState.GridOnline => Ui.Palette.Good,
        TrayState.OnBattery => Ui.Palette.Warning,
        TrayState.Problem => Ui.Palette.Bad,
        _ => Ui.Palette.Neutral,
    };

    /// <summary>Opens the dashboard.</summary>
    private PendingIntent OpenApp() => PendingIntent.GetActivity(_context, 0,
        new Intent(_context, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop),
        PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

    public void Show(UpsNotice notice, string ups, string source)
    {
        var body = notice.Footnote is null ? notice.Body : $"{notice.Body}\n{notice.Footnote}";
        var notification = new Notification.Builder(_context, notice.Important ? AlertsChannel : EventsChannel)
            .SetSmallIcon(Resource.Drawable.ic_stat_ups)
            .SetContentTitle(notice.Title)
            .SetContentText(notice.Body)
            .SetStyle(new Notification.BigTextStyle().BigText(body))
            .SetSubText(ups)
            .SetColor(ColorOf(notice.Tone).ToArgb())
            .SetCategory(notice.Important ? Notification.CategoryAlarm : Notification.CategoryStatus)
            .SetVisibility(NotificationVisibility.Public)
            .SetShowWhen(true)
            .SetAutoCancel(true)
            .SetContentIntent(OpenApp())
            .Build();
        _manager.Notify(IdOf($"{source}/{notice.Tag}"), notification);
    }

    public void ShowTest(string? ups)
    {
        var builder = new Notification.Builder(_context, EventsChannel)
            .SetSmallIcon(Resource.Drawable.ic_stat_ups)
            .SetContentTitle("Test notification")
            .SetContentText("This is how UPS Tools tells you that the grid went down or came back.")
            .SetColor(Ui.Palette.Good.ToArgb())
            .SetAutoCancel(true)
            .SetContentIntent(OpenApp());
        if (ups is not null) builder.SetSubText(ups);
        _manager.Notify(IdOf("test"), builder.Build());
    }

    /// <summary>The status notification: the state of every UPS, as the tray icon and its tooltip on Windows.</summary>
    public Notification Status(MonitorController controller)
    {
        var (state, title, lines) = controller.Status;
        var text = lines.Count > 0 ? string.Join("\n", lines) : controller.Upses.Count == 0 ? "Pair a UPS to watch it." : "";
        var stop = PendingIntent.GetService(_context, 1,
            new Intent(_context, typeof(MonitorService)).SetAction(MonitorService.ActionStop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        return new Notification.Builder(_context, StatusChannel)
            .SetSmallIcon(Resource.Drawable.ic_stat_ups)
            .SetContentTitle(title)
            .SetContentText(lines.Count > 0 ? lines[0] : text)
            .SetStyle(new Notification.BigTextStyle().BigText(text))
            .SetColor(ColorOf(state).ToArgb())
            .SetLargeIcon(StatusArt.Tile(state, Ui.Dp(_context, 64)))
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetShowWhen(false)
            .SetCategory(Notification.CategoryService)
            .SetVisibility(NotificationVisibility.Public)
            .SetContentIntent(OpenApp())
            .AddAction(new Notification.Action.Builder(null, "Stop monitoring", stop).Build())
            .Build();
    }

    public void UpdateStatus(Notification notification) => _manager.Notify(StatusId, notification);

    /// <summary>A notification ID that stays the same for the same key (string.GetHashCode changes per process).</summary>
    private static int IdOf(string key)
    {
        var hash = 2166136261u;
        foreach (var c in key) hash = (hash ^ c) * 16777619u;
        return (int)(hash & 0x3FFFFFFF) + 100; // clear of StatusId
    }
}
