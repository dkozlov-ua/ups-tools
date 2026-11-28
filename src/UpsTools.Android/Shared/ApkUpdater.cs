using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace UpsTools.Droid;

/// <summary>A release on GitHub, with its APK.</summary>
/// <param name="Sha256">The APK's SHA-256 as GitHub lists it, lowercase hex.</param>
public sealed record ApkRelease(Version Version, string PageUrl, string ApkName, Uri ApkUrl, long Size, string Sha256);

/// <summary>
/// Updates for the phone, as the Windows app's Updater: the latest release on the project's GitHub, and its APK
/// (UpsTools-X.Y.Z-android.apk), downloaded and checked against the SHA-256 GitHub lists for it. Android then checks
/// that it is signed with the installed app's key.
/// </summary>
/// <param name="handler">The HTTP stack; the platform's unless a test says otherwise.</param>
public sealed class ApkUpdater(HttpMessageHandler? handler = null)
{
    public const string Repository = "dkozlov-ua/ups-tools";

    private static readonly Uri LatestUrl = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private static readonly string DownloadPrefix = $"https://github.com/{Repository}/releases/download/";

    private readonly HttpClient _http = CreateClient(handler);

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UpsTools-Android", "1"));
        return http;
    }

    public static string ApkNameOf(Version version) => $"UpsTools-{version.ToString(3)}-android.apk";

    public async Task<ApkRelease> GetLatestAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        return Parse(json.RootElement);
    }

    internal static ApkRelease Parse(JsonElement release)
    {
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            throw new IOException($"The latest release has an unexpected tag '{tag}'");
        var page = release.TryGetProperty("html_url", out var url) ? url.GetString() ?? "" : "";

        var apkName = ApkNameOf(version);
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != apkName) continue;

            var download = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!download.StartsWith(DownloadPrefix, StringComparison.Ordinal))
                throw new IOException($"The APK of {version.ToString(3)} is not on {Repository}'s releases");
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 7 + 64)
                throw new IOException($"GitHub lists no SHA-256 for the APK of {version.ToString(3)}");

            return new ApkRelease(version, page, apkName, new Uri(download), asset.GetProperty("size").GetInt64(),
                digest[7..].ToLowerInvariant());
        }
        throw new IOException($"The release {tag} has no APK for Android ({apkName})");
    }

    /// <summary>
    /// Downloads the APK into <paramref name="folder"/> (emptied first), checking its SHA-256; a download that doesn't
    /// match is deleted and throws.
    /// </summary>
    public async Task<string> DownloadAsync(ApkRelease release, string folder, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.EnumerateFiles(folder))
        {
            try { File.Delete(old); } catch (IOException) { /* in use */ }
        }

        var path = Path.Combine(folder, release.ApkName);
        var partial = path + ".partial";
        using (var response = await _http.GetAsync(release.ApkUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!response.IsSuccessStatusCode)
                throw new IOException($"The download failed: {(int)response.StatusCode} {response.ReasonPhrase}");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    total += read;
                    if (release.Size > 0) progress?.Report(Math.Min(1, (double)total / release.Size));
                }
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual != release.Sha256)
            {
                File.Delete(partial);
                throw new IOException($"The download of {release.ApkName} is damaged: its SHA-256 isn't the one GitHub lists");
            }
        }
        File.Move(partial, path, overwrite: true);
        return path;
    }
}
