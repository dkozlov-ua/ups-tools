using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using UpsTools.Services;
using UpsTools.ViewModels;
using Color = Android.Graphics.Color;

namespace UpsTools.Droid.Platform;

/// <summary>
/// The UPS battery widget: one UPS (chosen when it is added, <see cref="UpsWidgetSetup"/>), its state, its charge as a
/// bar in the state's colour, and the load with the time left or the charging. Tapping it opens the UPS's History.
/// </summary>
[BroadcastReceiver(Label = "UPS battery", Exported = true)]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData(AppWidgetManager.MetaDataAppwidgetProvider, Resource = "@xml/widget_ups_info")]
public sealed class UpsWidget : AppWidgetProvider
{
    private const string Preferences = "ups_widgets";

    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is not null) UpdateAll(context);
    }

    public override void OnDeleted(Context? context, int[]? appWidgetIds)
    {
        if (context is null || appWidgetIds is null) return;
        var editor = context.GetSharedPreferences(Preferences, FileCreationMode.Private)!.Edit()!;
        foreach (var id in appWidgetIds) editor.Remove(id.ToString());
        editor.Apply();
    }

    /// <summary>Which UPS (its settings' ID) a widget shows; null when it hasn't been chosen.</summary>
    public static string? UpsOf(Context context, int widget) =>
        context.GetSharedPreferences(Preferences, FileCreationMode.Private)!.GetString(widget.ToString(), null);

    public static void Choose(Context context, int widget, string ups) =>
        context.GetSharedPreferences(Preferences, FileCreationMode.Private)!.Edit()!.PutString(widget.ToString(), ups)!.Apply();

    public static void UpdateAll(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context)!;
        var ids = manager.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(UpsWidget))));
        foreach (var id in ids ?? []) Update(context, manager, id);
    }

    public static void Update(Context context, AppWidgetManager manager, int widget)
    {
        var controller = UpsToolsApp.Current.Controller;
        var unit = UpsOf(context, widget) is { } ups ? controller.Find(ups) : null;
        var look = Look.Of(unit, MonitorService.Running);

        var views = new RemoteViews(context.PackageName, Resource.Layout.widget_ups);
        views.SetTextViewText(Resource.Id.ups_name, unit?.Title ?? "No UPS");
        views.SetTextViewText(Resource.Id.ups_state, look.State);
        views.SetTextColor(Resource.Id.ups_state, look.Color);
        views.SetTextViewText(Resource.Id.ups_percent, look.Percent is { } p ? $"{p} %" : "—");
        views.SetTextViewText(Resource.Id.ups_detail, look.Detail);
        foreach (var (bar, state) in Bars)
        {
            views.SetViewVisibility(bar, state == look.Bar ? Android.Views.ViewStates.Visible : Android.Views.ViewStates.Gone);
            views.SetProgressBar(bar, 100, look.Percent ?? 0, false);
        }

        var open = unit is not null && MonitorService.Running
            ? new Intent(context, typeof(HistoryActivity)).PutExtra(HistoryActivity.ExtraId, unit.Settings.Id)
            : new Intent(context, typeof(MainActivity));
        open.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        views.SetOnClickPendingIntent(Resource.Id.ups_root,
            PendingIntent.GetActivity(context, 1000 + widget, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent));
        manager.UpdateAppWidget(widget, views);
    }

    private static readonly (int Bar, TrayState State)[] Bars =
    [
        (Resource.Id.ups_bar_good, TrayState.GridOnline),
        (Resource.Id.ups_bar_warning, TrayState.OnBattery),
        (Resource.Id.ups_bar_bad, TrayState.Problem),
        (Resource.Id.ups_bar_neutral, TrayState.Neutral),
    ];

    /// <summary>What the widget shows of a UPS: its state's words and colour, the bar, the charge, the line below.</summary>
    internal sealed record Look(string State, Color Color, TrayState Bar, int? Percent, string Detail)
    {
        public static Look Of(UpsUnit? unit, bool running)
        {
            if (unit is null) return new("", Notifier.ColorOf(TrayState.Neutral), TrayState.Neutral, null, "Not paired any more: add the widget again");
            var d = unit.Dashboard;
            int? percent = d.BatteryValue == "—" ? null : (int)Math.Round(d.BatteryPercent);
            if (!running) return new("Not monitoring", Notifier.ColorOf(TrayState.Neutral), TrayState.Neutral, percent, "Tap to open UPS Tools");

            var state = MonitorController.StateOf(unit);
            var grid = d.GridValue switch { "Online" => true, "Lost" => false, _ => (bool?)null };
            var words = d.Link switch
            {
                LinkStatus.Unreachable => "Unreachable",
                LinkStatus.Connecting or LinkStatus.NotPaired => "Connecting…",
                LinkStatus.Retrying => "Reconnecting…",
                _ => grid switch
                {
                    false when d.IsBatteryLow => "Battery low",
                    false => "On battery",
                    true => "Grid online",
                    null => "Connected",
                },
            };
            var load = d.OutputValue == "—" ? null : $"{d.OutputValue} load";
            var detail = d.Link == LinkStatus.Unreachable ? (d.LastUpdated.Length > 0 ? $"Last reading: {d.LastUpdated["Updated ".Length..]}" : "No reading yet")
                : grid == false ? Join(load, d.RuntimeValue == "—" ? null : $"{d.RuntimeValue} left")
                : Join(load, d.FlowTone == Tone.Good ? $"charging {d.FlowValue}" : null);
            return new(words, Notifier.ColorOf(state), state, percent, detail);
        }

        private static string Join(params string?[] parts) => string.Join(" · ", parts.OfType<string>());
    }
}

/// <summary>
/// Which UPS a UPS battery widget shows: asked when it is added (and when it is reconfigured). With a single UPS
/// paired, that one, without asking.
/// </summary>
[Activity(Name = "com.dkozlov.upstools.UpsWidgetSetup", Label = "Choose the UPS", Theme = "@style/AppTheme", Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout)]
[IntentFilter([AppWidgetManager.ActionAppwidgetConfigure])]
public sealed class UpsWidgetSetup : ScreenActivity
{
    private int _widget = AppWidgetManager.InvalidAppwidgetId;

    protected override string ScreenTitle => "Choose the UPS";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        _widget = Intent?.GetIntExtra(AppWidgetManager.ExtraAppwidgetId, AppWidgetManager.InvalidAppwidgetId) ?? AppWidgetManager.InvalidAppwidgetId;
        // Leaving without choosing doesn't add the widget.
        SetResult(Result.Canceled, new Intent().PutExtra(AppWidgetManager.ExtraAppwidgetId, _widget));
        base.OnCreate(savedInstanceState);
        if (_widget == AppWidgetManager.InvalidAppwidgetId) Finish();
        else if (Controller.Upses.Count == 1 && UpsWidget.UpsOf(this, _widget) is null) Choose(Controller.Upses[0]);
    }

    protected override void Build(Android.Widget.LinearLayout content)
    {
        if (Controller.Upses.Count == 0)
        {
            var card = Ui.Card(this);
            card.AddView(Ui.Text(this, "No UPS paired yet: pair one in UPS Tools, then add the widget again.", 15));
            card.AddView(Ui.Button(this, "Add UPS…", () =>
            {
                StartActivity(typeof(PairActivity));
                Finish();
            }, filled: true));
            content.AddView(card, Ui.Match());
            return;
        }

        content.AddView(Ui.Text(this, "Which UPS should this widget show?", 15, secondary: true));
        var current = UpsWidget.UpsOf(this, _widget);
        var list = Ui.Card(this, 4);
        foreach (var unit in Controller.Upses)
        {
            var row = Ui.Column(this, 2);
            row.SetPadding(0, Ui.Dp(this, 8), 0, Ui.Dp(this, 8));
            row.AddView(Ui.Text(this, unit.Title + (unit.Settings.Id == current ? "  ✓" : ""), 17, bold: true));
            row.AddView(Ui.Text(this, unit.Dashboard.Summary, 13, secondary: true));
            row.Clickable = true;
            row.Click += (_, _) => Choose(unit);
            list.AddView(row, Ui.Match());
        }
        content.AddView(list, Ui.Match());
    }

    private void Choose(UpsUnit unit)
    {
        UpsWidget.Choose(this, _widget, unit.Settings.Id);
        UpsWidget.Update(this, AppWidgetManager.GetInstance(this)!, _widget);
        SetResult(Result.Ok, new Intent().PutExtra(AppWidgetManager.ExtraAppwidgetId, _widget));
        Finish();
    }
}
