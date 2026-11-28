using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using UpsTools.Core;
using UpsTools.Services;

namespace UpsTools.Droid;

/// <summary>
/// An event as the phone tells other apps and services of it: what happened and the UPS's state then. The fields
/// are the Windows scripts' variables (<see cref="ScriptEnvironment"/>), named for JSON.
/// </summary>
/// <param name="Device">The phone that tells, as the others on the network call it.</param>
/// <param name="Test">Sent by Send test, not by the event.</param>
public sealed record EventMessage(UpsEventKind Kind, UpsNotice Notice, IReadOnlyDictionary<string, string?> Variables, string Device,
    bool Test = false)
{
    /// <summary>The fields: a script variable's value, as a number or flag where it is one; none for a value not known.</summary>
    public IReadOnlyList<(string Name, object Value)> Fields
    {
        get
        {
            var fields = new List<(string, object)>
            {
                ("event", Kind.ToString()),
                ("title", Notice.Title),
                ("message", Notice.Body),
            };
            foreach (var (variable, name, type) in Map)
            {
                if (Variables.GetValueOrDefault(variable) is not { } value) continue;
                object typed = type switch
                {
                    'b' => value == "1",
                    'n' when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
                    _ => value,
                };
                fields.Add((name, typed));
            }
            fields.Add(("device", Device));
            if (Test) fields.Add(("test", true));
            return fields;
        }
    }

    /// <summary>The script variables, the name each has here, and what it is: b a flag, n a number, s text.</summary>
    private static readonly (string Variable, string Name, char Type)[] Map =
    [
        (ScriptEnvironment.Name, "ups", 's'),
        (ScriptEnvironment.Grid, "grid", 'b'),
        (ScriptEnvironment.Reachable, "reachable", 'b'),
        (ScriptEnvironment.BatteryPercent, "batteryPercent", 'n'),
        (ScriptEnvironment.RuntimeMinutes, "runtimeMinutes", 'n'),
        (ScriptEnvironment.LoadWatts, "loadW", 'n'),
        (ScriptEnvironment.OutageSeconds, "outageSeconds", 'n'),
        (ScriptEnvironment.Model, "model", 's'),
        (ScriptEnvironment.Serial, "serial", 's'),
        (ScriptEnvironment.ReadingTime, "readingTime", 's'),
    ];

    public string ToJson()
    {
        var json = new JsonObject();
        foreach (var (name, value) in Fields)
        {
            json[name] = value switch
            {
                bool b => JsonValue.Create(b),
                long n => JsonValue.Create(n),
                _ => JsonValue.Create((string)value),
            };
        }
        return json.ToJsonString(Written);
    }

    /// <summary>UTF-8 as it is: names and texts stay readable (e.g. a dash, or a UPS named in Cyrillic), not escaped.</summary>
    private static readonly JsonSerializerOptions Written = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

/// <summary>Tells the apps on the phone of an event (Tasker, MacroDroid…): an Android broadcast.</summary>
public interface IEventBroadcast
{
    void Send(EventMessage message);
}

/// <summary>Calls a UPS's webhooks: a POST with the event, retried a few times while the network is down.</summary>
/// <param name="handler">The HTTP stack; the platform's unless a test says otherwise.</param>
/// <param name="retryDelays">How long to wait before each retry; three tries over half a minute unless a test says otherwise.</param>
public sealed class WebhookSender(HttpMessageHandler? handler = null, IReadOnlyList<TimeSpan>? retryDelays = null)
{
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler(), disposeHandler: handler is null)
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly IReadOnlyList<TimeSpan> _retryDelays = retryDelays ?? [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(25)];

    /// <summary>The request a webhook gets for <paramref name="message"/>.</summary>
    public static HttpRequestMessage Request(WebhookSettings hook, EventMessage message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, hook.Url.Trim());
        if (hook.Format == WebhookFormat.Json)
        {
            request.Content = new StringContent(message.ToJson(), Encoding.UTF8, "application/json");
        }
        else
        {
            var body = message.Notice.Footnote is { } footnote ? $"{message.Notice.Body}\n{footnote}" : message.Notice.Body;
            request.Content = new StringContent(body, Encoding.UTF8, "text/plain");
            var ups = message.Variables.GetValueOrDefault(ScriptEnvironment.Name);
            var title = ups is null ? message.Notice.Title : $"{message.Notice.Title}: {ups}";
            request.Headers.TryAddWithoutValidation("Title", HeaderValue(message.Test ? "[test] " + title : title));
            request.Headers.TryAddWithoutValidation("Priority", message.Notice.Important ? "high" : "default");
        }
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("UpsTools-Android", "1"));
        return request;
    }

    /// <summary>A header value as it is (ASCII), or as an RFC 2047 encoded word, which ntfy and others decode.</summary>
    internal static string HeaderValue(string text) =>
        text.All(c => c is >= ' ' and <= '~') ? text : $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}?=";

    /// <summary>Sends it, retrying when the network or the server fails; what happened, for the log.</summary>
    /// <returns>Null when the webhook took it, or why not.</returns>
    public async Task<string?> SendAsync(WebhookSettings hook, EventMessage message, CancellationToken ct = default)
    {
        string? failure = null;
        for (var attempt = 0; attempt <= _retryDelays.Count; attempt++)
        {
            if (attempt > 0) await Task.Delay(_retryDelays[attempt - 1], ct);
            try
            {
                using var request = Request(hook, message);
                using var response = await _http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode) return null;
                failure = $"{(int)response.StatusCode} {response.ReasonPhrase}";
                if ((int)response.StatusCode < 500 && (int)response.StatusCode != 429) return failure; // asking again won't help
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                failure = ex is TaskCanceledException ? "no answer in time" : ex.GetBaseException().Message;
            }
        }
        return failure;
    }

    /// <summary>The host the webhook calls, to name it in the log and the settings.</summary>
    public static string HostOf(WebhookSettings hook) =>
        Uri.TryCreate(hook.Url.Trim(), UriKind.Absolute, out var uri) ? uri.Host : hook.Url;
}
