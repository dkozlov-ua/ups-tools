using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace UpsTools.Core.EcoFlow;

/// <summary>
/// Looks up the user ID of an EcoFlow account by signing in to EcoFlow's servers, as the EcoFlow app does. Only the
/// user ID is kept; the password is sent to EcoFlow and nowhere else.
/// </summary>
public static class EcoFlowAccount
{
    public static readonly Uri LoginUri = new("https://api.ecoflow.com/auth/login");

    /// <summary>Returns the user ID; throws <see cref="IOException"/> with EcoFlow's reason when sign-in fails.</summary>
    public static async Task<string> GetUserIdAsync(HttpClient http, string email, string password, CancellationToken ct)
    {
        var request = new
        {
            email = email.Trim(),
            password = Convert.ToBase64String(Encoding.UTF8.GetBytes(password)),
            scene = "IOT_APP",
            appVersion = "1.0.0",
            oauth = new { bundleId = "com.ef.EcoFlow" },
            userType = "ECOFLOW",
        };

        using var response = await http.PostAsJsonAsync(LoginUri, request, ct);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"EcoFlow sign-in failed ({(int)response.StatusCode} {response.ReasonPhrase})");

        return UserIdFromResponse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>The user ID in a sign-in response: <c>{"code": "0", "data": {"user": {"userId": "…"}}}</c>.</summary>
    internal static string UserIdFromResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var c) ? c.ToString() : null;
        if (code != "0")
        {
            var message = root.TryGetProperty("message", out var m) ? m.ToString() : "no reason given";
            throw new IOException($"EcoFlow sign-in failed: {message}");
        }

        if (root.TryGetProperty("data", out var data) && data.TryGetProperty("user", out var user) &&
            user.TryGetProperty("userId", out var id) && id.ToString() is { Length: > 0 } userId)
            return userId;
        throw new IOException("EcoFlow sign-in succeeded but returned no user ID");
    }
}
