using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using UpsTools.Core;
using UpsTools.Droid.Tests.Support;
using UpsTools.Services;

namespace UpsTools.Droid.Tests;

/// <summary>Webhooks and the broadcast for Tasker: what they carry, retries, and which events call which.</summary>
public sealed class WebhookTests : IDisposable
{
    private readonly TestUiThread _ui = new();
    private readonly TempFolder _folder = new();
    private readonly FakeUps _ups = new();
    private readonly Server _server = new();
    private readonly Broadcasts _broadcasts = new();
    private MonitorController? _controller;

    public WebhookTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    public void Dispose()
    {
        if (_controller is { } controller) _ui.Run(controller.StopAsync).Wait(TimeSpan.FromSeconds(10));
        _ui.Dispose();
        _folder.Dispose();
    }

    private static readonly UpsStatus Reading = new()
    {
        Timestamp = new DateTimeOffset(2026, 11, 28, 13, 10, 0, TimeSpan.FromHours(2)),
        Model = "River 3 Plus",
        Serial = "R631ZE1AWH5K2080",
        GridPresent = false,
        BatteryPercent = 87,
        CapacityWh = 286,
        AcOutputW = 120.4,
    };

    private static EventMessage Message(bool test = false) => new(UpsEventKind.GridLost, UpsNotice.For(UpsEventKind.GridLost, Reading),
        ScriptEnvironment.For(UpsEventKind.GridLost, Reading, reachable: true, TimeSpan.FromSeconds(42.7), manual: false, "Стіл"), "Galaxy S22", test);

    [Fact]
    public async Task Json_carries_the_event_and_the_state_as_the_script_variables_do()
    {
        using var request = WebhookSender.Request(new WebhookSettings { Url = "https://ha.local/api/webhook/ups" }, Message());
        var json = await request.Content!.ReadAsStringAsync();
        Assert.Equal("application/json", request.Content.Headers.ContentType!.MediaType);
        Assert.Contains("\"title\":\"Grid lost — running on battery\"", json); // UTF-8 as it is, not escaped
        Assert.Contains("\"ups\":\"Стіл\"", json);

        using var document = JsonDocument.Parse(json);
        var fields = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString());
        Assert.Equal(new Dictionary<string, string>
        {
            ["event"] = "GridLost",
            ["title"] = "Grid lost — running on battery",
            ["message"] = Message().Notice.Body,
            ["ups"] = "Стіл",
            ["grid"] = "False",
            ["reachable"] = "True",
            ["batteryPercent"] = "87",
            ["runtimeMinutes"] = Message().Variables[ScriptEnvironment.RuntimeMinutes]!,
            ["loadW"] = "120",
            ["outageSeconds"] = "42",
            ["model"] = "River 3 Plus",
            ["serial"] = "R631ZE1AWH5K2080",
            ["readingTime"] = "2026-11-28T13:10:00+02:00",
            ["device"] = "Galaxy S22",
        }, fields);
        Assert.Equal(JsonValueKind.Number, document.RootElement.GetProperty("batteryPercent").ValueKind);
        Assert.Equal(JsonValueKind.False, document.RootElement.GetProperty("grid").ValueKind);
    }

    [Fact]
    public async Task Text_is_the_notification_with_its_title_in_a_header_for_ntfy()
    {
        using var request = WebhookSender.Request(new WebhookSettings { Url = "https://ntfy.sh/my-ups", Format = WebhookFormat.Text }, Message(test: true));
        Assert.Equal(Message().Notice.Body, await request.Content!.ReadAsStringAsync());
        Assert.Equal("text/plain", request.Content.Headers.ContentType!.MediaType);
        var title = request.Headers.GetValues("Title").Single();
        Assert.StartsWith("=?UTF-8?B?", title); // not ASCII: encoded, as ntfy reads it
        Assert.Equal("[test] Grid lost — running on battery: Стіл",
            Encoding.UTF8.GetString(Convert.FromBase64String(title["=?UTF-8?B?".Length..^2])));
        Assert.Equal("high", request.Headers.GetValues("Priority").Single());
        Assert.Equal("Plain ascii", WebhookSender.HeaderValue("Plain ascii"));
    }

    [Fact]
    public async Task A_server_error_is_retried_and_a_refusal_is_not()
    {
        var sender = new WebhookSender(_server, [TimeSpan.Zero, TimeSpan.Zero]);
        var hook = new WebhookSettings { Url = "https://example.com/hook" };

        _server.Answers.Enqueue(HttpStatusCode.BadGateway);
        _server.Answers.Enqueue(HttpStatusCode.OK);
        Assert.Null(await sender.SendAsync(hook, Message()));
        Assert.Equal(2, _server.Requests.Count);

        _server.Answers.Enqueue(HttpStatusCode.NotFound);
        Assert.Equal("404 Not Found", await sender.SendAsync(hook, Message()));
        Assert.Equal(3, _server.Requests.Count);

        for (var i = 0; i < 3; i++) _server.Answers.Enqueue(HttpStatusCode.InternalServerError);
        Assert.Equal("500 Internal Server Error", await sender.SendAsync(hook, Message()));
        Assert.Equal(6, _server.Requests.Count);
    }

    [Fact]
    public async Task An_event_calls_the_webhooks_set_for_it_and_tells_the_other_apps()
    {
        var log = new AppLog(_folder.Path);
        new SettingsStore(log, _folder.Path).Save(new AppSettings
        {
            Upses = [new UpsSettings
            {
                Device = FakeUps.Paired,
                Label = "Desk",
                Webhooks =
                [
                    new WebhookSettings { Url = "https://ha.local/lost", Events = [UpsEventKind.GridLost] },
                    new WebhookSettings { Url = "https://ha.local/back", Events = [UpsEventKind.GridRestored] },
                    new WebhookSettings { Url = "https://ha.local/off", Enabled = false },
                    new WebhookSettings { Url = "not a url" },
                ],
            }],
        });
        _controller = await _ui.Run(() =>
        {
            var c = new MonitorController(_ui, log, [_ups], "Phone", webhooks: new WebhookSender(_server, [])) { Broadcast = _broadcasts };
            c.Settings.UpdatePeriodSeconds = 0;
            c.Start();
            return c;
        });

        _ups.Answer(true, 80);
        _ups.Answer(false, 79);
        await Until(() => _server.Requests.Count == 1 && log.Recent.Any(e => e.Message.StartsWith("Webhook ha.local (Grid lost): sent")));
        Assert.Equal("https://ha.local/lost", _server.Requests.Single().Url);
        Assert.Contains("\"ups\":\"Desk\"", _server.Requests.Single().Body);

        var told = Assert.Single(_broadcasts.Sent);
        Assert.Equal((UpsEventKind.GridLost, "Phone", false), (told.Kind, told.Device, told.Test));

        _ups.Answer(true, 79);
        await Until(() => _server.Requests.Count == 2);
        Assert.Equal("https://ha.local/back", _server.Requests.Last().Url);
        Assert.Equal(2, _broadcasts.Sent.Count);
    }

    [Fact]
    public async Task Send_test_reports_what_the_webhook_answered()
    {
        var log = new AppLog(_folder.Path);
        new SettingsStore(log, _folder.Path).Save(new AppSettings { Upses = [new UpsSettings { Device = FakeUps.Paired }] });
        _controller = await _ui.Run(() => new MonitorController(_ui, log, [_ups], "Phone", webhooks: new WebhookSender(_server, [])));

        _server.Answers.Enqueue(HttpStatusCode.Unauthorized);
        var answer = await _ui.Run(() => _controller.TestWebhookAsync(_controller.Upses[0], new WebhookSettings { Url = "https://x.example/h" }));
        Assert.Equal("401 Unauthorized", answer);
        Assert.Contains("\"test\":true", _server.Requests.Single().Body);
    }

    [Theory]
    [InlineData("https://ntfy.sh/topic", true)]
    [InlineData("http://192.168.0.10:8123/api/webhook/x", true)]
    [InlineData("ftp://host/x", false)]
    [InlineData("ntfy.sh/topic", false)]
    [InlineData("", false)]
    public void Only_http_urls_are_called(string url, bool valid) => Assert.Equal(valid, WebhookSettings.IsValidUrl(url));

    private async Task Until(Func<bool> condition, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out");
            await Task.Delay(20);
        }
    }

    /// <summary>A web server that answers what the test queues (200 when nothing is), and keeps what it got.</summary>
    private sealed class Server : HttpMessageHandler
    {
        public ConcurrentQueue<HttpStatusCode> Answers { get; } = new();
        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests) Requests.Add((request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(Answers.TryDequeue(out var code) ? code : HttpStatusCode.OK);
        }
    }

    private sealed class Broadcasts : IEventBroadcast
    {
        public List<EventMessage> Sent { get; } = [];

        public void Send(EventMessage message)
        {
            lock (Sent) Sent.Add(message);
        }
    }
}
