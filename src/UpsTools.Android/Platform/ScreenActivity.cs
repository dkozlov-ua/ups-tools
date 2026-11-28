using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;
using View = Android.Views.View;

namespace UpsTools.Droid.Platform;

/// <summary>
/// A screen: a header (title, a back button on the inner screens, actions on the right) over a scrolling column that
/// <see cref="Build"/> fills. It runs edge to edge, keeping clear of the system bars and the keyboard.
/// </summary>
public abstract class ScreenActivity : Activity
{
    private LinearLayout _content = null!;
    private LinearLayout _actions = null!;
    private TextView _title = null!;
    private bool _rebuildQueued;

    protected MonitorController Controller => UpsToolsApp.Current.Controller;

    /// <summary>The main screen has no back button.</summary>
    protected virtual bool HasBack => true;

    protected abstract string ScreenTitle { get; }

    /// <summary>Fills the screen (again): <paramref name="content"/> is empty.</summary>
    protected abstract void Build(LinearLayout content);

    /// <summary>The header's buttons on the right; none unless overridden.</summary>
    protected virtual void BuildActions(LinearLayout actions)
    {
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // Edge to edge, as Android 15 and later have it anyway: the same layout on every version from 11.
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && !OperatingSystem.IsAndroidVersionAtLeast(35))
        {
#pragma warning disable CA1422 // obsolete from Android 15, where it is what happens
            Window!.SetDecorFitsSystemWindows(false);
#pragma warning restore CA1422
        }

        var root = Ui.Column(this);
        root.SetBackgroundColor(Ui.Background(this));

        var header = Ui.Row(this, 4);
        header.SetPadding(Ui.Dp(this, 8), Ui.Dp(this, 8), Ui.Dp(this, 8), Ui.Dp(this, 4));
        if (HasBack)
        {
            var back = new Button(this) { Text = "←" };
            back.SetTextSize(Android.Util.ComplexUnitType.Sp, 22);
            back.SetTextColor(Ui.Primary(this));
            back.SetBackgroundColor(Android.Graphics.Color.Transparent);
            back.ContentDescription = "Back";
            back.Click += (_, _) => Finish();
            header.AddView(back, new LinearLayout.LayoutParams(Ui.Dp(this, 48), Ui.Dp(this, 48)));
        }
        _title = Ui.Text(this, ScreenTitle, 22, bold: true);
        _title.SetPadding(HasBack ? 0 : Ui.Dp(this, 8), 0, 0, 0);
        header.AddView(_title, Ui.Weight());
        _actions = Ui.Row(this, 0);
        header.AddView(_actions);
        root.AddView(header, Ui.Match());

        var scroll = new ScrollView(this) { FillViewport = true };
        _content = Ui.Column(this, 12);
        var pad = Ui.Dp(this, 12);
        _content.SetPadding(pad, Ui.Dp(this, 4), pad, Ui.Dp(this, 24));
        scroll.AddView(_content, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));

        root.SetOnApplyWindowInsetsListener(new Insets());
        SetContentView(root);
    }

    /// <summary>
    /// Builds the screen again each time it comes back (e.g. from a system settings page); a screen with fields to
    /// type in builds once, so that what is typed and the focus stay.
    /// </summary>
    protected virtual bool RebuildOnResume => true;

    private bool _built;

    protected override void OnResume()
    {
        base.OnResume();
        if (_built && !RebuildOnResume) return;
        _built = true;
        Rebuild();
    }

    /// <summary>Builds the screen again now.</summary>
    protected void Rebuild()
    {
        _rebuildQueued = false;
        _title.Text = ScreenTitle;
        _actions.RemoveAllViews();
        BuildActions(_actions);
        _content.RemoveAllViews();
        Build(_content);
    }

    /// <summary>Builds the screen again soon, once for any number of changes that come together.</summary>
    protected void QueueRebuild()
    {
        if (_rebuildQueued || IsFinishing || IsDestroyed) return;
        _rebuildQueued = true;
        _content.PostDelayed(() =>
        {
            if (_rebuildQueued && !IsFinishing && !IsDestroyed) Rebuild();
        }, 150);
    }

    protected void Toast(string text) => Android.Widget.Toast.MakeText(this, text, ToastLength.Short)!.Show();

    /// <summary>
    /// Pads the screen clear of the status and navigation bars, and of the keyboard while it is up. Before Android 11
    /// the window keeps clear of them itself (it isn't edge to edge there).
    /// </summary>
    private sealed class Insets : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(30)) return insets;
            var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.Ime() | WindowInsets.Type.DisplayCutout());
            v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            return insets;
        }
    }
}
