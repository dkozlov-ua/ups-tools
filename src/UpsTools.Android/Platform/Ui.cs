using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using UpsTools.ViewModels;
using Color = Android.Graphics.Color;
using Orientation = Android.Widget.Orientation;
using View = Android.Views.View;

namespace UpsTools.Droid.Platform;

/// <summary>Builds the screens' views in code: text, cards, rows of buttons, fields, in the app's colours.</summary>
public static class Ui
{
    public static class Palette
    {
        public static readonly Color Good = Color.ParseColor("#2E7D32");
        public static readonly Color Warning = Color.ParseColor("#E89A00");
        public static readonly Color Bad = Color.ParseColor("#C62828");
        public static readonly Color Neutral = Color.ParseColor("#607D8B");
        public static readonly Color Accent = Color.ParseColor("#0E7490");
    }

    public static bool IsNight(Context c) =>
        OperatingSystem.IsAndroidVersionAtLeast(29)
        && (c.Resources!.Configuration!.UiMode & UiMode.NightMask) == UiMode.NightYes;

    public static Color Background(Context c) => IsNight(c) ? Color.ParseColor("#121417") : Color.ParseColor("#F3F4F6");
    public static Color CardColor(Context c) => IsNight(c) ? Color.ParseColor("#1E2226") : Color.White;
    public static Color Primary(Context c) => IsNight(c) ? Color.ParseColor("#ECEFF1") : Color.ParseColor("#1B1F23");
    public static Color Secondary(Context c) => IsNight(c) ? Color.ParseColor("#9AA4AD") : Color.ParseColor("#5F6B76");
    public static Color Divider(Context c) => IsNight(c) ? Color.ParseColor("#2E343A") : Color.ParseColor("#E2E5E9");

    /// <summary>A tone's colour, brighter on a dark background.</summary>
    public static Color ToneColor(Context c, Tone tone) => tone switch
    {
        Tone.Good => IsNight(c) ? Color.ParseColor("#66BB6A") : Palette.Good,
        Tone.Warning => IsNight(c) ? Color.ParseColor("#FFB300") : Palette.Warning,
        Tone.Bad => IsNight(c) ? Color.ParseColor("#EF5350") : Palette.Bad,
        _ => Primary(c),
    };

    public static int Dp(Context c, float dp) => (int)Math.Round(dp * c.Resources!.DisplayMetrics!.Density);

    public static LinearLayout Column(Context c, int spacingDp = 0)
    {
        var column = new LinearLayout(c) { Orientation = Orientation.Vertical };
        if (spacingDp > 0)
        {
            column.ShowDividers = ShowDividers.Middle;
            column.SetDividerDrawable(Spacer(c, spacingDp));
        }
        return column;
    }

    public static LinearLayout Row(Context c, int spacingDp = 8)
    {
        var row = new LinearLayout(c) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        if (spacingDp > 0)
        {
            row.ShowDividers = ShowDividers.Middle;
            row.SetDividerDrawable(Spacer(c, spacingDp));
        }
        return row;
    }

    private static Drawable Spacer(Context c, int dp)
    {
        var spacer = new GradientDrawable();
        spacer.SetSize(Dp(c, dp), Dp(c, dp));
        spacer.SetColor(Color.Transparent);
        return spacer;
    }

    public static TextView Text(Context c, string text, float sp = 15, bool bold = false, Color? color = null, bool secondary = false)
    {
        var view = new TextView(c) { Text = text };
        view.SetTextSize(Android.Util.ComplexUnitType.Sp, sp);
        view.SetTextColor(color ?? (secondary ? Secondary(c) : Primary(c)));
        if (bold) view.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Bold), TypefaceStyle.Bold);
        return view;
    }

    /// <summary>A section's heading, in the accent colour.</summary>
    public static TextView Heading(Context c, string text)
    {
        var view = Text(c, text, 14, bold: true, color: IsNight(c) ? Color.ParseColor("#4FC3D9") : Palette.Accent);
        view.SetPadding(Dp(c, 4), Dp(c, 16), 0, Dp(c, 2));
        return view;
    }

    /// <summary>A rounded panel with padding.</summary>
    public static LinearLayout Card(Context c, int spacingDp = 8)
    {
        var card = Column(c, spacingDp);
        var background = new GradientDrawable();
        background.SetColor(CardColor(c));
        background.SetCornerRadius(Dp(c, 16));
        card.Background = background;
        var pad = Dp(c, 16);
        card.SetPadding(pad, pad, pad, pad);
        return card;
    }

    public static Button Button(Context c, string text, Action onClick, bool filled = false)
    {
        var button = new Button(c) { Text = text };
        button.SetAllCaps(false);
        if (filled)
        {
            var background = new GradientDrawable();
            background.SetColor(Palette.Accent);
            background.SetCornerRadius(Dp(c, 20));
            button.Background = background;
            button.SetTextColor(Color.White);
            button.SetPadding(Dp(c, 20), 0, Dp(c, 20), 0);
        }
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A switch with its title and an explanation below.</summary>
    public static View Switch(Context c, string title, string? detail, bool on, Action<bool> changed)
    {
        var column = Column(c);
        var toggle = new Android.Widget.Switch(c) { Text = title, Checked = on };
        toggle.SetTextColor(Primary(c));
        toggle.SetTextSize(Android.Util.ComplexUnitType.Sp, 15);
        toggle.CheckedChange += (_, e) => changed(e.IsChecked);
        column.AddView(toggle, Match());
        if (detail is not null) column.AddView(Text(c, detail, 13, secondary: true));
        return column;
    }

    public static CheckBox Check(Context c, string title, bool on, Action<bool> changed)
    {
        var box = new CheckBox(c) { Text = title, Checked = on };
        box.SetTextColor(Primary(c));
        box.CheckedChange += (_, e) => changed(e.IsChecked);
        return box;
    }

    /// <summary>
    /// A whole number from <paramref name="min"/> to <paramref name="max"/>: each valid number typed is kept at once
    /// (clamped); leaving the field shows the number kept.
    /// </summary>
    public static View Number(Context c, string title, string? detail, string unit, int min, int max, Func<int> get, Action<int> set)
    {
        var column = Column(c, 2);
        var row = Row(c);
        row.AddView(Text(c, title), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        var field = new EditText(c) { Text = get().ToString(), InputType = InputTypes.ClassNumber };
        field.SetEms(4);
        field.SetTextColor(Primary(c));
        field.Gravity = GravityFlags.End;
        field.TextChanged += (_, _) =>
        {
            if (!int.TryParse(field.Text, out var value)) return;
            value = Math.Clamp(value, min, max);
            if (value != get()) set(value);
        };
        field.FocusChange += (_, e) =>
        {
            if (!e.HasFocus && field.Text != get().ToString()) field.Text = get().ToString();
        };
        row.AddView(field);
        row.AddView(Text(c, unit, 14, secondary: true));
        column.AddView(row, Match());
        if (detail is not null) column.AddView(Text(c, detail, 13, secondary: true));
        return column;
    }

    public static EditText Field(Context c, string text, string hint, InputTypes type = InputTypes.ClassText)
    {
        var field = new EditText(c) { Text = text, Hint = hint, InputType = type };
        field.SetTextColor(Primary(c));
        field.SetHintTextColor(Secondary(c));
        field.SetSingleLine(true);
        return field;
    }

    public static LinearLayout.LayoutParams Match() =>
        new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

    public static LinearLayout.LayoutParams Weight(float weight = 1) =>
        new(0, ViewGroup.LayoutParams.WrapContent, weight);

    /// <summary>A small rounded label, e.g. the link state "Connected".</summary>
    public static TextView Chip(Context c, string text, Color color)
    {
        var chip = Text(c, text, 12, bold: true, color: color);
        var background = new GradientDrawable();
        background.SetColor(Color.Argb(36, color.R, color.G, color.B));
        background.SetCornerRadius(Dp(c, 12));
        chip.Background = background;
        chip.SetPadding(Dp(c, 10), Dp(c, 3), Dp(c, 10), Dp(c, 3));
        return chip;
    }

    /// <summary>A notice across the screen with an action, e.g. "Bluetooth is off. [Turn on]".</summary>
    public static View Banner(Context c, string text, Color color, string? action = null, Action? onAction = null)
    {
        var card = Card(c);
        var background = new GradientDrawable();
        background.SetColor(Color.Argb(IsNight(c) ? 60 : 30, color.R, color.G, color.B));
        background.SetCornerRadius(Dp(c, 16));
        card.Background = background;
        var row = Row(c);
        row.AddView(Text(c, text, 14), Weight());
        if (action is not null && onAction is not null) row.AddView(Button(c, action, onAction));
        card.AddView(row, Match());
        return card;
    }
}
