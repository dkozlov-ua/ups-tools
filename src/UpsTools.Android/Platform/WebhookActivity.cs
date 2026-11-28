using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Text;
using Android.Widget;
using UpsTools.Core;
using UpsTools.Services;

namespace UpsTools.Droid.Platform;

/// <summary>A webhook of a UPS, new or one it has: its URL, what it sends, on which events; Send test, Delete.</summary>
[Activity(Label = "Webhook", Theme = "@style/AppTheme", Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout)]
public sealed class WebhookActivity : ScreenActivity
{
    public const string ExtraUps = "ups";
    public const string ExtraHook = "hook";

    private UpsUnit? _unit;

    /// <summary>What is being edited: a copy, kept only by Save.</summary>
    private WebhookSettings _hook = new();
    private bool _isNew;
    private TextView? _result;

    protected override string ScreenTitle => _isNew ? "New webhook" : "Webhook";

    protected override bool RebuildOnResume => false;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        _unit = UpsToolsApp.Current.Controller.Find(Intent?.GetStringExtra(ExtraUps) ?? "");
        var existing = _unit?.Settings.Webhooks.FirstOrDefault(h => h.Id == Intent?.GetStringExtra(ExtraHook));
        _isNew = existing is null;
        if (existing is not null)
        {
            _hook = new WebhookSettings
            {
                Id = existing.Id,
                Url = existing.Url,
                Format = existing.Format,
                Enabled = existing.Enabled,
                Events = [.. existing.Events],
            };
        }
        base.OnCreate(savedInstanceState);
        if (_unit is null) Finish();
    }

    protected override void Build(LinearLayout content)
    {
        if (_unit is not { } unit) return;

        var main = Ui.Card(this, 8);
        main.AddView(Ui.Text(this, "URL", 15, bold: true));
        var url = Ui.Field(this, _hook.Url, "https://…", InputTypes.ClassText | InputTypes.TextVariationUri);
        url.TextChanged += (_, _) => _hook.Url = url.Text ?? "";
        main.AddView(url, Ui.Match());
        main.AddView(Ui.Text(this, $"Called with a POST on the events below of {unit.Title}, from this phone; tried 3 times over half a minute if it fails.",
            13, secondary: true));
        main.AddView(Ui.Switch(this, "On", null, _hook.Enabled, on => _hook.Enabled = on), Ui.Match());
        content.AddView(main, Ui.Match());

        content.AddView(Ui.Heading(this, "Sends"));
        var format = Ui.Card(this, 4);
        var group = new RadioGroup(this);
        RadioButton Choice(string text, WebhookFormat value)
        {
            var radio = new RadioButton(this) { Text = text, Checked = _hook.Format == value, Id = Android.Views.View.GenerateViewId() };
            radio.SetTextColor(Ui.Primary(this));
            radio.CheckedChange += (_, e) => { if (e.IsChecked) _hook.Format = value; };
            group.AddView(radio);
            return radio;
        }
        Choice("JSON: the event and the UPS's state (Home Assistant, n8n, Node-RED…)", WebhookFormat.Json);
        Choice("Text: the notification's text, its title in a Title header (ntfy…)", WebhookFormat.Text);
        format.AddView(group, Ui.Match());
        format.AddView(Ui.Text(this,
            "JSON fields: event, title, message, ups, grid, reachable, batteryPercent, runtimeMinutes, loadW, outageSeconds, model, serial, " +
            "readingTime, device (and test on a test), as the scripts' variables on Windows. A value the UPS doesn't report is left out.",
            12, secondary: true));
        content.AddView(format, Ui.Match());

        content.AddView(Ui.Heading(this, "On"));
        var events = Ui.Card(this, 0);
        foreach (var kind in Enum.GetValues<UpsEventKind>())
            events.AddView(Ui.Check(this, kind.Title(), _hook.Events.Contains(kind), on =>
            {
                _hook.Events.Remove(kind);
                if (on) _hook.Events.Add(kind);
            }));
        content.AddView(events, Ui.Match());

        var buttons = Ui.Row(this);
        buttons.AddView(Ui.Button(this, "Save", Save, filled: true));
        buttons.AddView(Ui.Button(this, "Send test", () => _ = TestAsync()));
        if (!_isNew)
        {
            var delete = Ui.Button(this, "Delete", () =>
            {
                unit.Settings.Webhooks.RemoveAll(h => h.Id == _hook.Id);
                Controller.Save();
                Finish();
            });
            delete.SetTextColor(Ui.ToneColor(this, ViewModels.Tone.Bad));
            buttons.AddView(delete);
        }
        content.AddView(buttons);
        _result = Ui.Text(this, "", 13);
        content.AddView(_result);
    }

    private bool Check()
    {
        if (WebhookSettings.IsValidUrl(_hook.Url)) return true;
        Show("Enter an http:// or https:// URL.", problem: true);
        return false;
    }

    private void Save()
    {
        if (_unit is not { } unit || !Check()) return;
        _hook.Url = _hook.Url.Trim();
        var index = unit.Settings.Webhooks.FindIndex(h => h.Id == _hook.Id);
        if (index >= 0) unit.Settings.Webhooks[index] = _hook;
        else unit.Settings.Webhooks.Add(_hook);
        Controller.Save();
        Finish();
    }

    private async Task TestAsync()
    {
        if (_unit is not { } unit || !Check()) return;
        Show("Sending a test \"Grid lost\"…");
        var failure = await Controller.TestWebhookAsync(unit, _hook);
        Show(failure is null ? "Sent: the webhook took it." : $"It failed: {failure}", problem: failure is not null);
    }

    private void Show(string text, bool problem = false)
    {
        if (_result is null) return;
        _result.Text = text;
        _result.SetTextColor(problem ? Ui.ToneColor(this, ViewModels.Tone.Bad) : Ui.Primary(this));
    }
}
