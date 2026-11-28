using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Graphics.Drawables;
using Android.Service.QuickSettings;
using Android.Widget;
using UpsTools.Services;

namespace UpsTools.Droid.Platform;

/// <summary>
/// The UPSes at a glance outside the app: the Quick Settings tile and the home-screen widget. Both say what the status
/// notification says, and follow it (<see cref="Update"/>, from <see cref="MonitorService"/>).
/// </summary>
internal static class Glance
{
    public static void Update(Context context)
    {
        try
        {
            StatusWidget.Update(context);
            UpsWidget.UpdateAll(context);
            TileService.RequestListeningState(context, new ComponentName(context, Java.Lang.Class.FromType(typeof(StatusTile))));
        }
        catch (Exception ex)
        {
            UpsToolsApp.Current.Controller.Log.Info($"Could not update the widget or the tile: {ex.Message}");
        }
    }

    /// <summary>Opens the dashboard.</summary>
    public static PendingIntent OpenApp(Context context) => PendingIntent.GetActivity(context, 2,
        new Intent(context, typeof(MainActivity)).SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop),
        PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

    /// <summary>A UPS's line without the state the headline already says: "100 % · 158 W load".</summary>
    public static string Detail(UpsUnit unit)
    {
        var parts = unit.Dashboard.Summary.Split(" · ");
        return parts.Length > 1 ? string.Join(" · ", parts.Skip(1)) : parts[0];
    }
}

/// <summary>
/// The Quick Settings tile: the headline of the status notification (e.g. "On battery") as its label, the UPS's charge
/// and load (or, with several, each one's charge) below it; active while monitoring. Tapping it opens the app.
/// </summary>
[Service(Label = "UPS", Icon = "@drawable/ic_stat_ups", Permission = Android.Manifest.Permission.BindQuickSettingsTile, Exported = true)]
[IntentFilter([TileService.ActionQsTile])]
[MetaData(TileService.MetaDataActiveTile, Value = "true")]
public sealed class StatusTile : TileService
{
    public override void OnStartListening()
    {
        base.OnStartListening();
        Show();
    }

    private void Show()
    {
        if (QsTile is not { } tile) return;
        var controller = UpsToolsApp.Current.Controller;
        var (state, title, _) = controller.Status;
        string subtitle;
        if (!MonitorService.Running || controller.Upses.Count == 0)
        {
            tile.State = TileState.Inactive;
            tile.Label = controller.Upses.Count == 0 ? "No UPS paired" : "UPS Tools";
            subtitle = controller.Upses.Count == 0 ? "Tap to add one" : "Monitoring is off";
        }
        else
        {
            tile.State = TileState.Active;
            tile.Label = title;
            subtitle = controller.Upses.Count == 1 ? Glance.Detail(controller.Upses[0])
                : string.Join(", ", controller.Upses.Select(u => $"{u.Title} {u.Dashboard.BatteryValue}"));
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) tile.Subtitle = subtitle;
        else tile.Label = $"{tile.Label}: {subtitle}";
        tile.Icon = Icon.CreateWithResource(this, Resource.Drawable.ic_stat_ups);
        tile.ContentDescription = $"UPS Tools: {tile.Label}, {subtitle}";
        tile.UpdateTile();
    }

    public override void OnClick()
    {
        base.OnClick();
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            StartActivityAndCollapse(Glance.OpenApp(this));
        }
        else
        {
#pragma warning disable CA1422, CS0618 // before Android 14
            StartActivityAndCollapse(new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask));
#pragma warning restore CA1422, CS0618
        }
    }
}

/// <summary>
/// The home-screen widget: the headline, a line for each UPS (a dot in its colour, its name, its charge and load),
/// and whether monitoring runs. Tapping it opens the app.
/// </summary>
[BroadcastReceiver(Label = "All UPSes", Exported = true)]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData(AppWidgetManager.MetaDataAppwidgetProvider, Resource = "@xml/widget_info")]
public sealed class StatusWidget : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is not null) Update(context);
    }

    public static void Update(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context)!;
        var component = new ComponentName(context, Java.Lang.Class.FromType(typeof(StatusWidget)));
        if (manager.GetAppWidgetIds(component) is not { Length: > 0 }) return;

        var controller = UpsToolsApp.Current.Controller;
        var running = MonitorService.Running;
        var (state, title, _) = controller.Status;
        var views = new RemoteViews(context.PackageName, Resource.Layout.widget_status);
        views.SetTextViewText(Resource.Id.widget_title, running || controller.Upses.Count == 0 ? title : "Monitoring is off");
        views.SetTextColor(Resource.Id.widget_title, running ? Notifier.ColorOf(state) : Android.Graphics.Color.Rgb(0xB0, 0xB8, 0xC0));

        views.RemoveAllViews(Resource.Id.widget_rows);
        foreach (var unit in controller.Upses)
        {
            var row = new RemoteViews(context.PackageName, Resource.Layout.widget_row);
            row.SetTextColor(Resource.Id.row_dot, Notifier.ColorOf(running ? MonitorController.StateOf(unit) : TrayState.Neutral));
            row.SetTextViewText(Resource.Id.row_title, unit.Title);
            row.SetTextViewText(Resource.Id.row_text, running ? Glance.Detail(unit) : unit.Dashboard.BatteryValue);
            views.AddView(Resource.Id.widget_rows, row);
        }
        views.SetTextViewText(Resource.Id.widget_footer, controller.Upses.Count == 0 ? "Tap to add your UPS"
            : running ? $"UPS Tools · {DateTime.Now:HH:mm}" : "UPS Tools · tap to start");
        views.SetOnClickPendingIntent(Resource.Id.widget_root, Glance.OpenApp(context));
        manager.UpdateAppWidget(component, views);
    }
}
