using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UpsTools.Droid.Tests.Support;

namespace UpsTools.Droid.Tests;

/// <summary>Updates from GitHub: the latest release's APK, found, downloaded and checked against GitHub's SHA-256.</summary>
public sealed class ApkUpdaterTests : IDisposable
{
    private static readonly byte[] Apk = Encoding.ASCII.GetBytes("PK\x03\x04 pretend this is an APK");
    private static readonly string ApkSha = Convert.ToHexStringLower(SHA256.HashData(Apk));
    private const string Download = "https://github.com/dkozlov-ua/ups-tools/releases/download/v1.2.0/UpsTools-1.2.0-android.apk";

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static string Release(string tag = "v1.2.0", string? apkName = "UpsTools-1.2.0-android.apk", string url = Download,
        string? digest = null) => JsonSerializer.Serialize(new
        {
            tag_name = tag,
            html_url = "https://github.com/dkozlov-ua/ups-tools/releases/tag/" + tag,
            assets = new object[]
            {
                new { name = "UpsTools-1.2.0-setup.exe", browser_download_url = "https://github.com/x", size = 10, digest = "sha256:" + new string('0', 64) },
                new { name = apkName, browser_download_url = url, size = Apk.Length, digest = digest ?? "sha256:" + ApkSha },
            },
        });

    private static ApkRelease Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ApkUpdater.Parse(document.RootElement);
    }

    [Fact]
    public void The_latest_release_gives_its_apk_and_sha256()
    {
        var release = Parse(Release());
        Assert.Equal((new Version(1, 2, 0), "UpsTools-1.2.0-android.apk", Download, (long)Apk.Length, ApkSha),
            (release.Version, release.ApkName, release.ApkUrl.ToString(), release.Size, release.Sha256));
    }

    [Theory]
    [InlineData("no apk")]
    [InlineData("no digest")]
    [InlineData("elsewhere")]
    [InlineData("bad tag")]
    public void A_release_that_can_not_be_trusted_or_has_no_apk_is_refused(string what)
    {
        var json = what switch
        {
            "no apk" => Release(apkName: "something-else.apk"),
            "no digest" => Release(digest: "md5:abc"),
            "elsewhere" => Release(url: "https://example.com/UpsTools-1.2.0-android.apk"),
            _ => Release(tag: "latest"),
        };
        Assert.Throws<IOException>(() => Parse(json));
    }

    [Fact]
    public async Task The_whole_way_from_github_to_a_checked_file()
    {
        using var github = new FakeGitHub(Release(), Apk);
        var updater = new ApkUpdater(github);
        var release = await updater.GetLatestAsync();
        var reported = new List<double>();
        var path = await updater.DownloadAsync(release, _folder.Path, new SyncProgress(reported.Add));

        Assert.Equal(Apk, await File.ReadAllBytesAsync(path));
        Assert.Equal("UpsTools-1.2.0-android.apk", Path.GetFileName(path));
        Assert.Equal(1.0, reported.Last());
        Assert.Contains(github.Requests, r => r.EndsWith("/repos/dkozlov-ua/ups-tools/releases/latest"));
    }

    [Fact]
    public async Task A_download_that_isnt_what_github_lists_is_deleted()
    {
        using var github = new FakeGitHub(Release(), [.. Apk, 0]);
        var updater = new ApkUpdater(github);
        var release = await updater.GetLatestAsync();

        await Assert.ThrowsAsync<IOException>(() => updater.DownloadAsync(release, _folder.Path));
        Assert.Empty(Directory.EnumerateFiles(_folder.Path));
    }

    /// <summary>Reports at once, on the thread that reports (Progress&lt;T&gt; would post it).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class FakeGitHub(string release, byte[] apk) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            HttpContent content = url.EndsWith("/releases/latest") ? new StringContent(release) : new ByteArrayContent(apk);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
