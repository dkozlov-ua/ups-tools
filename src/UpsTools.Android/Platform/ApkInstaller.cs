using Android.App;
using Android.Content;
using Android.Content.PM;

namespace UpsTools.Droid.Platform;

/// <summary>
/// Installs a downloaded update through Android's package installer: it asks the user to confirm, checks that the
/// APK is signed with the installed app's key, and replaces the app (settings kept). Monitoring starts again after
/// (<see cref="BootReceiver"/> hears that the app was replaced).
/// </summary>
internal static class ApkInstaller
{
    public const string ActionResult = "com.dkozlov.upstools.INSTALL_RESULT";

    /// <summary>Android lets the app install packages: the user allowed it once, under "Install unknown apps".</summary>
    public static bool Allowed(Context context) => context.PackageManager!.CanRequestPackageInstalls();

    /// <summary>Opens the page where the user allows UPS Tools to install apps.</summary>
    public static void AskToAllow(Activity activity) =>
        activity.StartActivity(new Intent(Android.Provider.Settings.ActionManageUnknownAppSources,
            Android.Net.Uri.Parse("package:" + activity.PackageName)));

    public static void Install(Context context, string apk)
    {
        var installer = context.PackageManager!.PackageInstaller;
        var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        parameters.SetAppPackageName(context.PackageName);
        var id = installer.CreateSession(parameters);
        using var session = installer.OpenSession(id);
        using (var output = session.OpenWrite(Path.GetFileName(apk), 0, new FileInfo(apk).Length))
        using (var input = File.OpenRead(apk))
        {
            input.CopyTo(output);
            session.Fsync(output);
        }

        // Android fills in the outcome, so the intent must stay changeable.
        var result = new Intent(context, typeof(InstallResultReceiver)).SetAction(ActionResult);
        var flags = PendingIntentFlags.UpdateCurrent | (OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0);
        var pending = PendingIntent.GetBroadcast(context, id, result, flags)!;
        session.Commit(pending.IntentSender);
    }
}

/// <summary>What the package installer says: confirm (shown to the user), installed, or why not.</summary>
[BroadcastReceiver(Exported = false)]
public sealed class InstallResultReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null) return;
        var log = UpsToolsApp.Current.Controller.Log;
        var status = (PackageInstallStatus)intent.GetIntExtra(PackageInstaller.ExtraStatus, (int)PackageInstallStatus.Failure);
        switch (status)
        {
            case PackageInstallStatus.PendingUserAction:
                var confirm = OperatingSystem.IsAndroidVersionAtLeast(33)
                    ? (Intent?)intent.GetParcelableExtra(Intent.ExtraIntent, Java.Lang.Class.FromType(typeof(Intent)))
#pragma warning disable CA1422, CS0618 // before Android 13
                    : (Intent?)intent.GetParcelableExtra(Intent.ExtraIntent);
#pragma warning restore CA1422, CS0618
                if (confirm is null) return;
                confirm.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(confirm);
                break;
            case PackageInstallStatus.Success:
                log.Info("Update installed");
                break;
            default:
                var message = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage);
                var why = status switch
                {
                    PackageInstallStatus.FailureAborted => "cancelled",
                    PackageInstallStatus.FailureConflict or PackageInstallStatus.FailureIncompatible =>
                        "it isn't signed with this app's key, or doesn't suit this phone",
                    PackageInstallStatus.FailureStorage => "not enough storage",
                    _ => message ?? status.ToString(),
                };
                log.Warn($"The update wasn't installed: {why}");
                Android.Widget.Toast.MakeText(context, $"The update wasn't installed: {why}", Android.Widget.ToastLength.Long)!.Show();
                break;
        }
    }
}
