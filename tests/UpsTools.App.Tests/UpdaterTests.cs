using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UpsTools.AppTests.Support;
using UpsTools.Services;
using UpsTools.ViewModels;

namespace UpsTools.AppTests;

/// <summary>GitHub as the tests script it: the latest release and its setup.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public const string ApiUrl = "https://api.github.com/repos/dkozlov-ua/ups-tools/releases/latest";

    public byte[] Setup { get; set; } = Encoding.ASCII.GetBytes("MZ pretend setup");
    public HttpStatusCode ApiStatus { get; set; } = HttpStatusCode.OK;
    public string? LatestJson { get; set; }
    public List<HttpRequestMessage> Requests { get; } = [];

    public static string SetupUrl(string version) =>
        $"https://github.com/dkozlov-ua/ups-tools/releases/download/v{version}/UpsTools-{version}-setup.exe";

    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Setup));

    public void Release(string version, string? digest = null, string? url = null, string? name = null) =>
        LatestJson = JsonSerializer.Serialize(new
        {
            tag_name = "v" + version,
            html_url = $"https://github.com/dkozlov-ua/ups-tools/releases/tag/v{version}",
            assets = new[]
            {
                new
                {
                    name = name ?? $"UpsTools-{version}-setup.exe",
                    browser_download_url = url ?? SetupUrl(version),
                    size = Setup.Length,
                    digest = digest ?? "sha256:" + Sha256,
                },
            },
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var url = request.RequestUri!.ToString();
        if (url == ApiUrl)
            return Task.FromResult(new HttpResponseMessage(ApiStatus) { Content = new StringContent(LatestJson ?? "{}") });
        if (url.StartsWith("https://github.com/dkozlov-ua/ups-tools/releases/download/", StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Setup) });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public sealed class UpdaterTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeGitHub _github = new();
    private readonly List<ProcessStartInfo> _started = [];
    private readonly Updater _updater;

    public UpdaterTests() => _updater = new Updater(_github, _started.Add, _folder.Path);

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task The_latest_release_and_its_setup_are_read_from_the_github_api()
    {
        _github.Release("0.7.0");
        var release = await _updater.GetLatestAsync();

        Assert.Equal(new Version(0, 7, 0), release.Version);
        Assert.Equal("UpsTools-0.7.0-setup.exe", release.SetupName);
        Assert.Equal(new Uri(FakeGitHub.SetupUrl("0.7.0")), release.SetupUrl);
        Assert.Equal(_github.Sha256, release.Sha256);
        Assert.Equal("https://github.com/dkozlov-ua/ups-tools/releases/tag/v0.7.0", release.PageUrl);

        var request = _github.Requests.Single();
        Assert.StartsWith("UpsTools/", request.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());
    }

    [Theory]
    [InlineData(null, "https://github.com/someone-else/ups-tools/releases/download/v0.7.0/UpsTools-0.7.0-setup.exe", null, "not on dkozlov-ua/ups-tools's releases")]
    [InlineData("md5:abc", null, null, "no SHA-256")]
    [InlineData(null, null, "UpsTools-0.7.0-portable.zip", "has no UpsTools-0.7.0-setup.exe")]
    public async Task A_release_without_a_trustworthy_setup_is_refused(string? digest, string? url, string? name, string error)
    {
        _github.Release("0.7.0", digest, url, name);
        var ex = await Assert.ThrowsAsync<IOException>(() => _updater.GetLatestAsync());
        Assert.Contains(error, ex.Message);
    }

    [Fact]
    public async Task A_github_error_is_reported()
    {
        _github.ApiStatus = HttpStatusCode.Forbidden;
        var ex = await Assert.ThrowsAsync<IOException>(() => _updater.GetLatestAsync());
        Assert.Equal("GitHub answered 403 Forbidden", ex.Message);
    }

    [Fact]
    public async Task The_download_is_checked_against_githubs_sha256_and_reports_progress()
    {
        File.WriteAllText(Path.Combine(_folder.Path, "UpsTools-0.6.0-setup.exe"), "an earlier download");
        _github.Release("0.7.0");
        var release = await _updater.GetLatestAsync();
        var progress = new List<double>();

        var path = await _updater.DownloadAsync(release, new SyncProgress(progress.Add));

        Assert.Equal(Path.Combine(_folder.Path, "UpsTools-0.7.0-setup.exe"), path);
        Assert.Equal(_github.Setup, File.ReadAllBytes(path));
        Assert.Equal(1.0, progress.Last());
        Assert.Equal([path], Directory.GetFiles(_folder.Path)); // the earlier download is gone
    }

    [Fact]
    public async Task A_download_that_does_not_match_the_sha256_is_thrown_away()
    {
        _github.Release("0.7.0", digest: "sha256:" + new string('0', 64));
        var release = await _updater.GetLatestAsync();

        var ex = await Assert.ThrowsAsync<IOException>(() => _updater.DownloadAsync(release));
        Assert.Contains("SHA-256 differs", ex.Message);
        Assert.Empty(Directory.GetFiles(_folder.Path));
    }

    [Fact]
    public void Install_runs_the_setup_silently_and_asks_it_to_open_the_window_again()
    {
        var setup = Path.Combine(_folder.Path, "UpsTools-0.7.0-setup.exe");
        _updater.Install(setup);

        var info = _started.Single();
        Assert.Equal(setup, info.FileName);
        Assert.Equal($"/SILENT /SUPPRESSMSGBOXES /NORESTART /OPENWINDOW=yes \"/LOG={Path.Combine(_folder.Path, "setup.log")}\"", info.Arguments);
    }

    /// <summary>Reports on the calling thread, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

public sealed class UpdateViewModelTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeGitHub _github = new();
    private readonly List<ProcessStartInfo> _started = [];
    private readonly AppLog _log;
    private readonly UpdateViewModel _vm;

    public UpdateViewModelTests()
    {
        _log = new AppLog(_folder.Path);
        _vm = new UpdateViewModel(new Updater(_github, _started.Add, Path.Combine(_folder.Path, "download")), _log, new Version(0, 6, 0));
    }

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task Starts_as_a_check_button()
    {
        Assert.Equal(("Check for updates", true, "", false), (_vm.ActionText, _vm.CanAct, _vm.Status, _vm.HasRelease));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task On_the_latest_version_it_says_so()
    {
        _github.Release("0.6.0");
        await _vm.ActAsync();
        Assert.Equal(("Check for updates", true, "You have the latest version.", false), (_vm.ActionText, _vm.CanAct, _vm.Status, _vm.HasRelease));
        Assert.Empty(_started);
    }

    [Fact]
    public async Task A_newer_release_is_offered_then_downloaded_and_installed()
    {
        _github.Release("0.7.0");
        await _vm.ActAsync();
        Assert.Equal(("Update to 0.7.0", true, "UPS Tools 0.7.0 is available.", true), (_vm.ActionText, _vm.CanAct, _vm.Status, _vm.HasRelease));
        Assert.Equal("https://github.com/dkozlov-ua/ups-tools/releases/tag/v0.7.0", _vm.ReleasePage);
        Assert.Empty(_started);

        await _vm.ActAsync();

        var setup = _started.Single();
        Assert.Equal(Path.Combine(_folder.Path, "download", "UpsTools-0.7.0-setup.exe"), setup.FileName);
        Assert.Equal(_github.Setup, File.ReadAllBytes(setup.FileName));
        Assert.Equal(("Installing…", false), (_vm.ActionText, _vm.CanAct)); // the setup exits the app next
        Assert.Equal("The setup closes UPS Tools, updates it and opens it again.", _vm.Status);
        Assert.Contains(_log.Recent, e => e.Message == "Installing UPS Tools 0.7.0; the setup restarts the app");
    }

    [Fact]
    public async Task A_failed_check_says_why_and_can_be_retried()
    {
        _github.ApiStatus = HttpStatusCode.ServiceUnavailable;
        await _vm.ActAsync();
        Assert.Equal(("Check for updates", true), (_vm.ActionText, _vm.CanAct));
        Assert.Equal("Could not check for updates: GitHub answered 503 Service Unavailable", _vm.Status);

        _github.ApiStatus = HttpStatusCode.OK;
        _github.Release("0.6.0");
        await _vm.ActAsync();
        Assert.Equal("You have the latest version.", _vm.Status);
    }

    [Fact]
    public async Task A_failed_download_installs_nothing_and_can_be_retried()
    {
        _github.Release("0.7.0", digest: "sha256:" + new string('0', 64));
        await _vm.ActAsync();
        await _vm.ActAsync();

        Assert.Empty(_started);
        Assert.Equal(("Update to 0.7.0", true), (_vm.ActionText, _vm.CanAct));
        Assert.Equal("The update failed: The downloaded setup is not the one GitHub lists (its SHA-256 differs)", _vm.Status);
        Assert.Contains(_log.Recent, e => e.Message.StartsWith("Updating to 0.7.0 failed"));
    }
}
