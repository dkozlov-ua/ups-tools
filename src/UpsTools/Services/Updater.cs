using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace UpsTools.Services;

/// <summary>The latest release on GitHub and its setup.</summary>
/// <param name="Sha256">The setup's SHA-256 as GitHub reports it, lowercase hex.</param>
public sealed record AppRelease(Version Version, string PageUrl, string SetupName, Uri SetupUrl, long Size, string Sha256);

/// <summary>
/// Updates UPS Tools from the repository's GitHub releases: finds the latest one, downloads its setup (checking it
/// against the SHA-256 GitHub lists for it) and runs the setup, which exits this app, updates it and starts it again.
/// </summary>
/// <param name="handler">Talks to GitHub; the real network unless a test says otherwise.</param>
/// <param name="start">Starts the setup; <see cref="Process.Start(ProcessStartInfo)"/> unless a test says otherwise.</param>
/// <param name="downloadFolder">Where setups are downloaded to: a folder in %TEMP% unless a test says otherwise.</param>
public sealed class Updater(HttpMessageHandler? handler = null, Action<ProcessStartInfo>? start = null, string? downloadFolder = null)
{
    public const string Repository = "dkozlov-ua/ups-tools";

    private static readonly Uri LatestUrl = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private static readonly string DownloadPrefix = $"https://github.com/{Repository}/releases/download/";

    /// <summary>This copy's version, e.g. "0.6.0".</summary>
    public static string CurrentVersionText { get; } = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static Version CurrentVersion { get; } = Version.TryParse(CurrentVersionText, out var v) ? v : new Version(0, 0, 0);

    private readonly HttpClient _http = CreateClient(handler);
    private readonly Action<ProcessStartInfo> _start = start ?? (info => Process.Start(info)?.Dispose());
    private readonly string _folder = downloadFolder ?? Path.Combine(Path.GetTempPath(), "UpsTools-update" + AppProfile.Suffix);

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UpsTools", CurrentVersionText));
        return http;
    }

    /// <summary>The latest release; throws when there is none or it has no setup.</summary>
    public async Task<AppRelease> GetLatestAsync(CancellationToken ct = default)
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

    internal static AppRelease Parse(JsonElement release)
    {
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            throw new IOException($"The latest release has an unexpected tag '{tag}'");
        var page = release.TryGetProperty("html_url", out var url) ? url.GetString() ?? "" : "";

        var setupName = $"UpsTools-{version.ToString(3)}-setup.exe";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != setupName) continue;

            var download = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!download.StartsWith(DownloadPrefix, StringComparison.Ordinal))
                throw new IOException($"The setup of {version.ToString(3)} is not on {Repository}'s releases");
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 7 + 64)
                throw new IOException($"GitHub lists no SHA-256 for the setup of {version.ToString(3)}");

            return new AppRelease(version, page, setupName, new Uri(download), asset.GetProperty("size").GetInt64(),
                digest[7..].ToLowerInvariant());
        }
        throw new IOException($"The release {tag} has no {setupName}");
    }

    /// <summary>Downloads the setup and checks it; returns its path. <paramref name="progress"/> gets 0..1.</summary>
    public async Task<string> DownloadAsync(AppRelease release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // Earlier downloads go; a setup still running from here keeps its file, which is fine.
        Directory.CreateDirectory(_folder);
        foreach (var old in Directory.EnumerateFiles(_folder))
        {
            try { File.Delete(old); } catch { /* in use */ }
        }

        var path = Path.Combine(_folder, release.SetupName);
        var partial = path + ".partial";
        using (var response = await _http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
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
                throw new IOException("The downloaded setup is not the one GitHub lists (its SHA-256 differs)");
            }
        }

        File.Move(partial, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Runs the setup, which shows its progress, exits this app, updates it and opens it again.
    /// Its log goes next to it, as setup.log.
    /// </summary>
    public void Install(string setupPath)
    {
        var log = Path.Combine(Path.GetDirectoryName(setupPath)!, "setup.log");
        _start(new ProcessStartInfo(setupPath)
        {
            Arguments = $"/SILENT /SUPPRESSMSGBOXES /NORESTART /OPENWINDOW=yes \"/LOG={log}\"",
            UseShellExecute = false,
        });
    }
}
