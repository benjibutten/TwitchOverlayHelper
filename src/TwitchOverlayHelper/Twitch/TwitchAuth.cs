using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace TwitchOverlayHelper.Twitch;

/// <summary>The code the user types on twitch.tv/activate while the app polls for approval.</summary>
public sealed record DeviceCodePrompt(string DeviceCode, string UserCode, string VerificationUri, int ExpiresInSeconds, int IntervalSeconds);

public sealed record TwitchTokens(string AccessToken, string RefreshToken, string[] Scopes);

public sealed record TwitchIdentity(string UserId, string Login, string[] Scopes);

public class TwitchAuthException(string message) : Exception(message);

/// <summary>
/// Twitch could not answer right now (rate limit or an outage) rather than rejecting the login.
/// The saved credentials are still good and must survive, so the user is not signed out by a hiccup.
/// </summary>
public sealed class TwitchAuthTransientException(string message) : TwitchAuthException(message);

/// <summary>
/// Device Code Flow. Chosen over the authorization-code flow because it needs no client secret
/// and no redirect URI, which is the only shape that fits a desktop app we ship as a single exe.
/// </summary>
public sealed class TwitchAuth(HttpClient httpClient)
{
    /// <summary>Channel point redemptions, in your own channel only.</summary>
    public const string RedemptionsScope = "channel:read:redemptions";

    /// <summary>Shoutouts, in channels you moderate. The read scope is enough; we never send one.</summary>
    public const string ShoutoutsScope = "moderator:read:shoutouts";

    /// <summary>Power-ups and cheers through channel.bits.use, in your own channel only.</summary>
    public const string BitsScope = "bits:read";

    /// <summary>Hype trains, in your own channel only.</summary>
    public const string HypeTrainScope = "channel:read:hype_train";

    /// <summary>
    /// The emotes this account may send anywhere – subscriber and follower emotes above all. Without
    /// it the picker still has the channel's own emotes and the global ones, which need no scope at
    /// all; what is missing is the half that is personal to the logged-in user.
    /// </summary>
    public const string EmotesScope = "user:read:emotes";

    /// <summary>
    /// What the app asks Twitch for at login. "Required" is about the request, not about running:
    /// every one of these is optional at run time. A login granted before a scope existed keeps
    /// working and the feature behind the missing scope simply stays off, so nothing here can lock
    /// a reader out of a chat they could already watch.
    /// </summary>
    public static readonly string[] RequiredScopes =
    [
        "chat:read",
        "chat:edit",
        "moderator:manage:banned_users",
        "moderator:manage:chat_messages",
        "channel:manage:raids",
        "user:read:follows",
        RedemptionsScope,
        ShoutoutsScope,
        BitsScope,
        HypeTrainScope,
        EmotesScope
    ];

    public static string ScopeString => string.Join(' ', RequiredScopes);

    /// <summary>
    /// Which of the scopes we ask for a stored login does not have. A refresh hands back the scopes
    /// the token was granted, never the ones we have started asking for since, so this is the only
    /// way to notice – and the app says "log in again to switch X on" instead of letting the user
    /// meet a silent 403 from Twitch weeks later.
    /// </summary>
    public static IReadOnlyList<string> MissingScopes(IEnumerable<string>? granted)
    {
        var have = new HashSet<string>(granted ?? [], StringComparer.OrdinalIgnoreCase);
        return RequiredScopes.Where(scope => !have.Contains(scope)).ToArray();
    }

    /// <summary>The feature behind a scope, worded for someone deciding whether to log in again.</summary>
    public static string DescribeScope(string scope) => scope switch
    {
        RedemptionsScope => "inlösta belöningar",
        ShoutoutsScope => "shoutouts",
        BitsScope => "power-ups och förstorade emotes",
        HypeTrainScope => "hypetåg",
        EmotesScope => "dina egna emotes i emote-väljaren",
        "chat:read" => "läsa chatten",
        "chat:edit" => "skriva i chatten",
        "moderator:manage:banned_users" => "timeout och ban",
        "moderator:manage:chat_messages" => "ta bort meddelanden",
        "channel:manage:raids" => "starta raid",
        "user:read:follows" => "kanallistan i raid-väljaren",
        _ => scope
    };

    public async Task<DeviceCodePrompt> StartDeviceFlowAsync(string clientId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new TwitchAuthException("Fyll i Client ID först.");

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(),
            ["scopes"] = ScopeString
        });
        using HttpResponseMessage response = await httpClient.PostAsync("https://id.twitch.tv/oauth2/device", content, cancellationToken).ConfigureAwait(false);
        JsonElement json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TwitchAuthException(DescribeError(json, "Twitch nekade inloggningen. Kontrollera att Client ID hör till en app av typen “Public”."));

        return new DeviceCodePrompt(
            json.GetProperty("device_code").GetString()!,
            json.GetProperty("user_code").GetString()!,
            json.TryGetProperty("verification_uri", out JsonElement uri) ? uri.GetString()! : "https://www.twitch.tv/activate",
            json.TryGetProperty("expires_in", out JsonElement expires) ? expires.GetInt32() : 1800,
            Math.Max(1, json.TryGetProperty("interval", out JsonElement interval) ? interval.GetInt32() : 5));
    }

    /// <summary>Polls until the user approves, the code expires, or the caller cancels.</summary>
    public async Task<TwitchTokens> AwaitApprovalAsync(string clientId, DeviceCodePrompt prompt, CancellationToken cancellationToken = default)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(prompt.ExpiresInSeconds);
        var delay = TimeSpan.FromSeconds(prompt.IntervalSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId.Trim(),
                ["device_code"] = prompt.DeviceCode,
                ["scopes"] = ScopeString,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            });
            using HttpResponseMessage response = await httpClient.PostAsync("https://id.twitch.tv/oauth2/token", content, cancellationToken).ConfigureAwait(false);
            JsonElement json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode) return ReadTokens(json);

            string message = DescribeError(json, string.Empty);
            // "authorization_pending" simply means the user has not finished on twitch.tv yet.
            if (response.StatusCode == HttpStatusCode.BadRequest && message.Contains("pending", StringComparison.OrdinalIgnoreCase)) continue;
            // Twitch reports this the OAuth way, with an underscore; the spaced form is only what a
            // human-readable variant would look like, so both are accepted.
            if (message.Contains("slow_down", StringComparison.OrdinalIgnoreCase)
                || message.Contains("slow down", StringComparison.OrdinalIgnoreCase)) { delay += TimeSpan.FromSeconds(2); continue; }
            throw new TwitchAuthException(message.Length > 0 ? message : "Inloggningen avbröts av Twitch.");
        }

        throw new TwitchAuthException("Koden hann gå ut. Försök logga in igen.");
    }

    public async Task<TwitchTokens> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(),
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        });
        using HttpResponseMessage response = await httpClient.PostAsync("https://id.twitch.tv/oauth2/token", content, cancellationToken).ConfigureAwait(false);
        JsonElement json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw Failure(response.StatusCode, json, "Den sparade inloggningen gick inte att förnya. Logga in igen.");
        return ReadTokens(json);
    }

    /// <summary>Resolves who the token belongs to; Helix moderation calls need the moderator's own user id.</summary>
    public async Task<TwitchIdentity> ValidateAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
        request.Headers.TryAddWithoutValidation("Authorization", "OAuth " + accessToken);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        JsonElement json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw Failure(response.StatusCode, json, "Twitch godkände inte den sparade inloggningen.");

        return new TwitchIdentity(
            json.GetProperty("user_id").GetString()!,
            json.GetProperty("login").GetString()!,
            json.TryGetProperty("scopes", out JsonElement scopes) && scopes.ValueKind == JsonValueKind.Array
                ? scopes.EnumerateArray().Select(s => s.GetString() ?? string.Empty).ToArray()
                : []);
    }

    public async Task RevokeAsync(string clientId, string accessToken, CancellationToken cancellationToken = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(),
            ["token"] = accessToken
        });
        try { await httpClient.PostAsync("https://id.twitch.tv/oauth2/revoke", content, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException) { /* Logging out locally must succeed even if Twitch is unreachable. */ }
    }

    /// <summary>
    /// Separates "Twitch says no" from "Twitch is busy". Only the former means the stored login is
    /// dead; treating a 429 or a 503 as final would sign the user out over a passing outage.
    /// </summary>
    private static TwitchAuthException Failure(HttpStatusCode status, JsonElement json, string fallback)
    {
        bool transient = status == HttpStatusCode.TooManyRequests
            || status == HttpStatusCode.RequestTimeout
            || (int)status >= 500;
        return transient
            ? new TwitchAuthTransientException(DescribeError(json, "Twitch svarar inte just nu. Försök igen om en stund."))
            : new TwitchAuthException(DescribeError(json, fallback));
    }

    private static TwitchTokens ReadTokens(JsonElement json) => new(
        json.GetProperty("access_token").GetString()!,
        json.TryGetProperty("refresh_token", out JsonElement refresh) ? refresh.GetString() ?? string.Empty : string.Empty,
        json.TryGetProperty("scope", out JsonElement scope) && scope.ValueKind == JsonValueKind.Array
            ? scope.EnumerateArray().Select(s => s.GetString() ?? string.Empty).ToArray()
            : []);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try { return JsonDocument.Parse(body.Length == 0 ? "{}" : body).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    private static string DescribeError(JsonElement json, string fallback)
    {
        if (json.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String)
        {
            string text = message.GetString() ?? string.Empty;
            if (text.Length > 0) return text;
        }
        if (json.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String)
            return error.GetString() ?? fallback;
        return fallback;
    }
}
