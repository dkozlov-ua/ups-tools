using System.Globalization;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using UpsTools.Core;
using UpsTools.Core.History;

namespace UpsTools.Droid.Platform;

/// <summary>
/// One UPS's History: its chart over 6 hours, 24 hours or 7 days, and the events in that time. Its settings are a
/// button away. Tapping a UPS on the dashboard opens it.
/// </summary>
[Activity(Label = "History", Theme = "@style/AppTheme", Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout)]
public sealed class HistoryActivity : ScreenActivity
{
    public const string ExtraId = "ups";

    private static TimeSpan _range = TimeSpan.FromHours(24);
    private UpsUnit? _unit;
    private HistoryChartView? _chart;
    private LinearLayout? _events;

    protected override string ScreenTitle => _unit?.Title ?? "History";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        _unit = UpsToolsApp.Current.Controller.Find(Intent?.GetStringExtra(ExtraId) ?? "");
        base.OnCreate(savedInstanceState);
        if (_unit is null) Finish();
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (_unit is { } unit) unit.History.Changed += OnRecorded;
    }

    protected override void OnPause()
    {
        if (_unit is { } unit) unit.History.Changed -= OnRecorded;
        base.OnPause();
    }

    private void OnRecorded() => RunOnUiThread(() =>
    {
        _chart?.Invalidate();
        ShowEvents();
    });

    protected override void BuildActions(LinearLayout actions)
    {
        if (_unit is { } unit)
            actions.AddView(Ui.Button(this, "Settings", () =>
                StartActivity(new Intent(this, typeof(UpsSettingsActivity)).PutExtra(UpsSettingsActivity.ExtraId, unit.Settings.Id))));
    }

    protected override void Build(LinearLayout content)
    {
        if (_unit is not { } unit) return;

        var ranges = Ui.Row(this, 6);
        foreach (var (label, span) in HistoryChart.Ranges)
        {
            var selected = span == _range;
            var button = Ui.Button(this, label, () =>
            {
                _range = span;
                Rebuild();
            }, filled: selected);
            ranges.AddView(button);
        }
        content.AddView(ranges);

        var card = Ui.Card(this, 8);
        card.SetPadding(Ui.Dp(this, 6), Ui.Dp(this, 12), Ui.Dp(this, 6), Ui.Dp(this, 8));
        _chart = new HistoryChartView(this) { History = unit.History, Range = _range };
        card.AddView(_chart, new LinearLayout.LayoutParams(Android.Views.ViewGroup.LayoutParams.MatchParent, Ui.Dp(this, 260)));
        card.AddView(Legend());
        content.AddView(card, Ui.Match());

        content.AddView(Ui.Text(this, "A point a minute, as this phone saw the UPS (also through another device), kept 7 days. Touch the chart to read it.",
            12, secondary: true));

        content.AddView(Ui.Heading(this, "Events"));
        _events = Ui.Card(this, 6);
        content.AddView(_events, Ui.Match());
        ShowEvents();
    }

    private LinearLayout Legend()
    {
        var legend = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        legend.SetPadding(Ui.Dp(this, 8), 0, 0, 0);
        void Key(string text, Android.Graphics.Color color)
        {
            legend.AddView(Ui.Text(this, "■", 14, color: color));
            var label = Ui.Text(this, text, 12, secondary: true);
            label.SetPadding(Ui.Dp(this, 3), 0, Ui.Dp(this, 12), 0);
            legend.AddView(label);
        }
        Key("Battery", HistoryChartView.BatteryColor);
        Key("Load", HistoryChartView.LoadColor);
        Key("On battery", Android.Graphics.Color.Rgb(0xF5, 0x9E, 0x0B));
        Key("No readings", Android.Graphics.Color.Gray);
        return legend;
    }

    /// <summary>The events in the range, newest first.</summary>
    private void ShowEvents()
    {
        if (_events is null || _unit is null) return;
        _events.RemoveAllViews();
        var events = _unit.History.Events(DateTimeOffset.Now - _range).Reverse().Take(50).ToList();
        if (events.Count == 0) _events.AddView(Ui.Text(this, "None in this time.", 14, secondary: true));
        foreach (var e in events)
        {
            var row = Ui.Row(this, 10);
            row.AddView(Ui.Text(this, "●", 14, color: HistoryChartView.EventColor(e.Kind)));
            row.AddView(Ui.Text(this, e.Time.ToLocalTime().ToString("ddd HH:mm", CultureInfo.CurrentCulture), 13, secondary: true));
            row.AddView(Ui.Text(this, e.Kind.Title(), 14), Ui.Weight());
            _events.AddView(row, Ui.Match());
        }
    }
}
