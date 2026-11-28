using System.Text;
using Android.App;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using UpsTools.Services;
using Uri = Android.Net.Uri;

namespace UpsTools.Droid.Platform;

/// <summary>
/// Share log: the log (with the previous one, and what the app and phone are) as a text file, through Android's
/// share sheet. The log lives in the app's own storage, which nothing else can read; <see cref="LogProvider"/> lends
/// the one file to the app it is shared with.
/// </summary>
internal static class LogSharing
{
    /// <summary>The most of the log shared: the newest part, so that the file goes through any app.</summary>
    private const int MaxBytes = 2_000_000;

    public static void Share(Activity activity, MonitorController controller)
    {
        var file = Write(activity, controller);
        var uri = LogProvider.UriOf(activity, file);
        var send = new Intent(Intent.ActionSend)
            .SetType("text/plain")
            .PutExtra(Intent.ExtraStream, uri)
            .PutExtra(Intent.ExtraSubject, $"UPS Tools log ({controller.DeviceName})")
            .AddFlags(ActivityFlags.GrantReadUriPermission);
        send.ClipData = ClipData.NewRawUri(Path.GetFileName(file), uri);
        activity.StartActivity(Intent.CreateChooser(send, "Share log"));
    }

    /// <summary>Writes the file to share: a header, then the previous log and the current one, newest part kept.</summary>
    private static string Write(Context context, MonitorController controller)
    {
        var folder = LogProvider.Folder(context);
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.EnumerateFiles(folder)) File.Delete(old);

        var header = new StringBuilder()
            .AppendLine($"UPS Tools {AppVersion.Of(context)} on {Build.Manufacturer} {Build.Model}, Android {Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt})")
            .AppendLine($"Shared {DateTime.Now:yyyy-MM-dd HH:mm:ss}; monitoring {(MonitorService.Running ? "on" : "off")}, sharing {(controller.Settings.Sharing.Enabled ? "on" : "off")}")
            .AppendLine($"UPSes: {(controller.Upses.Count == 0 ? "none" : string.Join("; ", controller.Upses.Select(u => $"{u.Title} ({u.Device.Address}, {u.Route})")))}")
            .AppendLine();

        var log = controller.Log.FilePath;
        var text = new StringBuilder();
        foreach (var part in new[] { log + ".1", log })
        {
            try
            {
                if (File.Exists(part)) text.Append(File.ReadAllText(part));
            }
            catch (IOException)
            {
                // Being rotated right now: what is left still helps.
            }
        }
        var body = text.Length > MaxBytes ? "[…]\n" + text.ToString(text.Length - MaxBytes, MaxBytes) : text.ToString();

        var path = Path.Combine(folder, $"ups-tools-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, header + body);
        return path;
    }
}

/// <summary>
/// Lends the shared log file (only that folder, read only, to the app the share grants it to). Android's own
/// FileProvider is in AndroidX, which the app doesn't use for one file.
/// </summary>
[ContentProvider([Authority], Exported = false, GrantUriPermissions = true)]
public sealed class LogProvider : ContentProvider
{
    public const string Authority = "com.dkozlov.upstools.logs";

    public static string Folder(Context context) => Path.Combine(context.CacheDir!.AbsolutePath, "shared-log");

    public static Uri UriOf(Context context, string file) =>
        Uri.Parse($"content://{Authority}/{Uri.Encode(Path.GetFileName(file))}")!;

    /// <summary>The file a URI names, if it is one in the folder.</summary>
    private string? FileOf(Uri uri)
    {
        var name = uri.LastPathSegment;
        if (string.IsNullOrEmpty(name) || name.Contains('/') || name.Contains('\\') || name.StartsWith('.')) return null;
        var path = Path.Combine(Folder(Context!), name);
        return File.Exists(path) ? path : null;
    }

    public override bool OnCreate() => true;

    public override ParcelFileDescriptor? OpenFile(Uri uri, string mode)
    {
        if (mode != "r") throw new Java.Lang.SecurityException("The log is shared read only");
        var path = FileOf(uri) ?? throw new Java.IO.FileNotFoundException(uri.ToString());
        return ParcelFileDescriptor.Open(new Java.IO.File(path), ParcelFileMode.ReadOnly);
    }

    public override string? GetType(Uri uri) => "text/plain";

    /// <summary>The name and size, which the receiving app shows (e.g. as the attachment's name).</summary>
    public override ICursor? Query(Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder)
    {
        if (FileOf(uri) is not { } path) return null;
        var columns = projection ?? [IOpenableColumns.DisplayName, IOpenableColumns.Size];
        var cursor = new MatrixCursor(columns, 1);
        var row = new Java.Lang.Object[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            row[i] = columns[i] switch
            {
                IOpenableColumns.DisplayName => new Java.Lang.String(Path.GetFileName(path)),
                IOpenableColumns.Size => Java.Lang.Long.ValueOf(new FileInfo(path).Length),
                _ => null!,
            };
        }
        cursor.AddRow(row);
        return cursor;
    }

    public override Uri? Insert(Uri uri, ContentValues? values) => throw new Java.Lang.UnsupportedOperationException();

    public override int Delete(Uri uri, string? selection, string[]? selectionArgs) => throw new Java.Lang.UnsupportedOperationException();

    public override int Update(Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) =>
        throw new Java.Lang.UnsupportedOperationException();
}
