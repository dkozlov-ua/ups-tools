using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using UpsTools.Core;
using UpsTools.Services;

namespace UpsTools.Droid.Platform;

/// <summary>One UPS's settings: its name, event limits, notifications and the status notification; Forget.</summary>
[Activity(Label = "UPS", Theme = "@style/AppTheme", Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout)]
public sealed class UpsSettingsActivity : ScreenActivity
{
    public const string ExtraId = "ups";

    private UpsUnit? _unit;
    private LinearLayout? _webhooks;

    protected override string ScreenTitle => _unit?.Title ?? "UPS";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        _unit = UpsToolsApp.Current.Controller.Find(Intent?.GetStringExtra(ExtraId) ?? "");
        base.OnCreate(savedInstanceState);
        if (_unit is null) Finish();
    }

    protected override bool RebuildOnResume => false;

    // The list of webhooks follows what WebhookActivity changed, without rebuilding the fields above it.
    protected override void OnResume()
    {
        base.OnResume();
        ShowWebhooks();
    }

    private void ShowWebhooks()
    {
        if (_webhooks is null || _unit is not { } unit) return;
        _webhooks.RemoveAllViews();
        if (unit.Settings.Webhooks.Count == 0) _webhooks.AddView(Ui.Text(this, "None.", 14, secondary: true));
        foreach (var hook in unit.Settings.Webhooks)
        {
            var row = Ui.Column(this, 2);
            row.SetPadding(0, Ui.Dp(this, 6), 0, Ui.Dp(this, 6));
            row.AddView(Ui.Text(this, WebhookSender.HostOf(hook) + (hook.Enabled ? "" : " (off)"), 15, bold: true));
            var events = hook.Events.Count == Enum.GetValues<UpsEventKind>().Length ? "every event"
                : hook.Events.Count == 0 ? "no event" : string.Join(", ", hook.Events.Order().Select(k => k.Title()));
            row.AddView(Ui.Text(this, $"{(hook.Format == WebhookFormat.Json ? "JSON" : "Text")} on {events}", 12, secondary: true));
            row.Clickable = true;
            row.Click += (_, _) => StartActivity(new Intent(this, typeof(WebhookActivity))
                .PutExtra(WebhookActivity.ExtraUps, unit.Settings.Id).PutExtra(WebhookActivity.ExtraHook, hook.Id));
            _webhooks.AddView(row, Ui.Match());
        }
    }

    protected override void Build(LinearLayout content)
    {
        if (_unit is not { } unit) return;
        var ups = unit.Settings;

        var about = Ui.Card(this, 6);
        about.AddView(Ui.Text(this, "Name", 15, bold: true));
        var name = Ui.Field(this, ups.Label ?? "", unit.Device.Title);
        name.TextChanged += (_, _) => Controller.Rename(unit, name.Text);
        about.AddView(name, Ui.Match());
        about.AddView(Ui.Text(this, $"What notifications and the log call it; empty: its model, {unit.Device.Title}.", 13, secondary: true));
        about.AddView(Ui.Text(this, $"{unit.Device.Name} · {unit.Device.Address} · {unit.Route}", 12, secondary: true));
        content.AddView(about, Ui.Match());

        content.AddView(Ui.Heading(this, "Events"));
        var events = Ui.Card(this, 14);
        events.AddView(Ui.Number(this, "Grid lost grace period",
            "The grid must stay gone this long (0–600 seconds): a shorter blip is only logged. 0 fires at the first reading without it.",
            "s", 0, AppSettings.MaxGridLostDelaySeconds, () => ups.GridLostDelaySeconds, v =>
            {
                ups.GridLostDelaySeconds = v;
                Controller.ApplyRules(unit);
            }), Ui.Match());
        events.AddView(Ui.Text(this,
            "\"Battery low\" fires once per outage, when the UPS runs on battery and either limit below is reached. 0 turns a limit off.",
            13, secondary: true));
        events.AddView(Ui.Number(this, "Low battery threshold", "At or below this level (0–99 %).", "%",
            0, AppSettings.MaxBatteryLowPercent, () => ups.BatteryLowPercent, v =>
            {
                ups.BatteryLowPercent = v;
                Controller.ApplyRules(unit);
            }), Ui.Match());
        events.AddView(Ui.Number(this, "Time on battery left threshold",
            "At or below this many minutes at the current load (0–600), in two readings in a row, so a short spike in load doesn't count.",
            "min", 0, AppSettings.MaxBatteryLowMinutes, () => ups.BatteryLowMinutes, v =>
            {
                ups.BatteryLowMinutes = v;
                Controller.ApplyRules(unit);
            }), Ui.Match());
        events.AddView(Ui.Number(this, "UPS unreachable on battery grace period",
            "When the UPS stops answering while on battery, its charge can't be known: count it as low after this many minutes (0–600; 0: never).",
            "min", 0, AppSettings.MaxUnreachableOnBatteryMinutes, () => ups.UnreachableOnBatteryMinutes, v =>
            {
                ups.UnreachableOnBatteryMinutes = v;
                Controller.ApplyRules(unit);
            }), Ui.Match());
        content.AddView(events, Ui.Match());

        content.AddView(Ui.Heading(this, "Notifications"));
        var notifications = Ui.Card(this, 0);
        notifications.AddView(Ui.Text(this,
            "Notify when these events happen. A newer notification replaces the older one about the same thing: \"Grid restored\" replaces \"Grid lost\".",
            13, secondary: true));
        foreach (var kind in Enum.GetValues<UpsEventKind>())
            notifications.AddView(Ui.Check(this, kind.Title(), ups.NotifyOn(kind), on =>
            {
                ups.Notifications[kind] = on;
                Controller.Save();
            }));
        content.AddView(notifications, Ui.Match());

        content.AddView(Ui.Heading(this, "Status notification"));
        var status = Ui.Card(this, 0);
        status.AddView(Ui.Switch(this, "Show in the status notification",
            "A line about this UPS in the notification that stays while monitoring, e.g. \"Grid online · 80 % · 120 W load\".",
            ups.ShowInStatus, on =>
            {
                ups.ShowInStatus = on;
                Controller.Save();
                MonitorService.SettingsChanged();
            }), Ui.Match());
        status.AddView(Ui.Text(this,
            "It takes the colour of the UPS that needs attention most: yellow on battery after \"Grid lost\", red after \"Battery low\" " +
            "(until the grid is back) and \"UPS unreachable\". Change the colour for these events of this UPS:",
            13, secondary: true));
        foreach (var kind in UpsSettings.StatusEventKinds)
            status.AddView(Ui.Check(this, kind.Title(), ups.StatusOn(kind), on =>
            {
                ups.StatusEvents[kind] = on;
                Controller.Save();
                MonitorService.SettingsChanged();
            }));
        content.AddView(status, Ui.Match());

        content.AddView(Ui.Heading(this, "Webhooks"));
        var hooks = Ui.Card(this, 4);
        hooks.AddView(Ui.Text(this,
            "URLs this phone calls on this UPS's events: Home Assistant, ntfy, a NAS, anything that takes a POST. " +
            "Every event is also broadcast to the apps on the phone as com.dkozlov.upstools.EVENT, with the same fields as extras, for Tasker or MacroDroid.",
            13, secondary: true));
        _webhooks = Ui.Column(this, 0);
        hooks.AddView(_webhooks, Ui.Match());
        hooks.AddView(Ui.Button(this, "Add webhook…", () => StartActivity(new Intent(this, typeof(WebhookActivity))
            .PutExtra(WebhookActivity.ExtraUps, unit.Settings.Id))));
        content.AddView(hooks, Ui.Match());
        ShowWebhooks();

        var forget = Ui.Button(this, "Forget this UPS", () =>
            new AlertDialog.Builder(this)
                .SetTitle($"Forget {unit.Title}?")!
                .SetMessage("UPS Tools stops watching it and forgets its settings. You can add it again later.")!
                .SetPositiveButton("Forget", async (_, _) =>
                {
                    await Controller.ForgetAsync(unit);
                    if (Controller.Upses.Count == 0) MonitorService.Stop(this);
                    Finish();
                })!
                .SetNegativeButton("Cancel", (_, _) => { })!
                .Show());
        forget.SetTextColor(Ui.ToneColor(this, ViewModels.Tone.Bad));
        content.AddView(forget);
    }
}
