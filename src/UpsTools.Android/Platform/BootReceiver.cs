using Android.App;
using Android.Content;

namespace UpsTools.Droid.Platform;

/// <summary>Starts monitoring again when the phone has started (or the app was updated), if it is to.</summary>
[BroadcastReceiver(Exported = true, Enabled = true)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced])]
public sealed class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        var controller = UpsToolsApp.Current.Controller;
        if (!controller.Settings.StartAtBoot || controller.Upses.Count == 0) return;
        try
        {
            MonitorService.Start(context);
        }
        catch (Exception ex)
        {
            // Android 12+ refuses foreground services from the background but for these broadcasts; just in case.
            controller.Log.Warn($"Could not start monitoring after {intent?.Action}: {ex.Message}");
        }
    }
}
