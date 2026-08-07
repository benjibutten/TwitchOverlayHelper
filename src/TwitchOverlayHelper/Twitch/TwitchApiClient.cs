using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TwitchOverlayHelper.Twitch;

public sealed record RaidCandidate(string UserId, string Login, string DisplayName, string GameName, int ViewerCount, string ThumbnailUrl);

public class TwitchApiException(string message) : Exception(message);

/// <summary>
/// Twitch understood the call and said no: the token lacks the scope, or the user does not hold the
/// role the call needs in that channel. A subclass rather than a sibling, so every caller that
/// already treats a Twitch refusal as a readable error keeps doing so; only the code that can
/// degrade – subscribing to events we may not be allowed to see – catches the narrower type and
/// quietly turns that feature off.
/// </summary>
public sealed class TwitchNotPermittedException(string message) : TwitchApiException(message);

/// <summary>One channel point reward as the channel has it configured.</summary>
public sealed record CustomReward(string Id, string Title, int Cost);

/// <summary>Helix calls behind the dock's moderation buttons. Every call needs the moderator's own user id.</summary>
public sealed class TwitchApiClient(HttpClient httpClient, TwitchSession session)
{
    /// <summary>Times out a user; Twitch caps a timeout at 14 days.</summary>
    public Task TimeoutAsync(string broadcasterId, string userId, int seconds, string? reason, CancellationToken cancellationToken = default)
        => BanAsync(broadcasterId, userId, Math.Clamp(seconds, 1, 1209600), reason, cancellationToken);

    public Task BanAsync(string broadcasterId, string userId, string? reason, CancellationToken cancellationToken = default)
        => BanAsync(broadcasterId, userId, null, reason, cancellationToken);

    private async Task BanAsync(string broadcasterId, string userId, int? durationSeconds, string? reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new TwitchApiException("Meddelandet saknar användar-id, åtgärden går inte att utföra.");

        var payload = new StringBuilder("{\"data\":{\"user_id\":").Append(JsonSerializer.Serialize(userId));
        if (durationSeconds is int duration) payload.Append(",\"duration\":").Append(duration);
        if (!string.IsNullOrWhiteSpace(reason)) payload.Append(",\"reason\":").Append(JsonSerializer.Serialize(reason.Trim()));
        payload.Append("}}");

        string url = $"https://api.twitch.tv/helix/moderation/bans?broadcaster_id={Uri.EscapeDataString(broadcasterId)}&moderator_id={Uri.EscapeDataString(session.UserId)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteMessageAsync(string broadcasterId, string messageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageId)) throw new TwitchApiException("Meddelandet saknar id och kan inte tas bort.");
        string url = $"https://api.twitch.tv/helix/moderation/chat?broadcaster_id={Uri.EscapeDataString(broadcasterId)}" +
                     $"&moderator_id={Uri.EscapeDataString(session.UserId)}&message_id={Uri.EscapeDataString(messageId)}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task UnbanAsync(string broadcasterId, string userId, CancellationToken cancellationToken = default)
    {
        string url = $"https://api.twitch.tv/helix/moderation/bans?broadcaster_id={Uri.EscapeDataString(broadcasterId)}" +
                     $"&moderator_id={Uri.EscapeDataString(session.UserId)}&user_id={Uri.EscapeDataString(userId)}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartRaidAsync(string fromBroadcasterId, string toBroadcasterId, CancellationToken cancellationToken = default)
    {
        string url = $"https://api.twitch.tv/helix/raids?from_broadcaster_id={Uri.EscapeDataString(fromBroadcasterId)}" +
                     $"&to_broadcaster_id={Uri.EscapeDataString(toBroadcasterId)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelRaidAsync(string broadcasterId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"https://api.twitch.tv/helix/raids?broadcaster_id={Uri.EscapeDataString(broadcasterId)}");
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Live channels the logged-in user follows – the shortlist the raid picker offers.</summary>
    public async Task<IReadOnlyList<RaidCandidate>> GetFollowedLiveChannelsAsync(CancellationToken cancellationToken = default)
    {
        string url = $"https://api.twitch.tv/helix/streams/followed?user_id={Uri.EscapeDataString(session.UserId)}&first=100";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        JsonElement json = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        var result = new List<RaidCandidate>();
        if (!json.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (JsonElement stream in data.EnumerateArray())
        {
            result.Add(new RaidCandidate(
                ReadString(stream, "user_id"),
                ReadString(stream, "user_login"),
                ReadString(stream, "user_name"),
                ReadString(stream, "game_name"),
                stream.TryGetProperty("viewer_count", out JsonElement viewers) && viewers.ValueKind == JsonValueKind.Number ? viewers.GetInt32() : 0,
                ReadString(stream, "thumbnail_url").Replace("{width}", "160", StringComparison.Ordinal).Replace("{height}", "90", StringComparison.Ordinal)));
        }
        result.Sort((a, b) => b.ViewerCount.CompareTo(a.ViewerCount));
        return result;
    }

    /// <summary>
    /// Subscribes to an EventSub topic over an open WebSocket session. The condition differs per
    /// topic, so it is handed in already shaped rather than guessed at here.
    /// </summary>
    public async Task CreateEventSubSubscriptionAsync(
        string type,
        string version,
        IReadOnlyDictionary<string, string> condition,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var payload = new StringBuilder("{\"type\":").Append(JsonSerializer.Serialize(type))
            .Append(",\"version\":").Append(JsonSerializer.Serialize(version))
            .Append(",\"condition\":{");
        bool first = true;
        foreach ((string key, string value) in condition)
        {
            if (!first) payload.Append(',');
            payload.Append(JsonSerializer.Serialize(key)).Append(':').Append(JsonSerializer.Serialize(value));
            first = false;
        }
        payload.Append("},\"transport\":{\"method\":\"websocket\",\"session_id\":")
            .Append(JsonSerializer.Serialize(sessionId)).Append("}}");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.twitch.tv/helix/eventsub/subscriptions")
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The channel's rewards, so a redemption can be shown by name from the very first one instead
    /// of waiting to learn the name from a redemption that has already gone past.
    /// </summary>
    public async Task<IReadOnlyList<CustomReward>> GetCustomRewardsAsync(string broadcasterId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.twitch.tv/helix/channel_points/custom_rewards?broadcaster_id={Uri.EscapeDataString(broadcasterId)}");
        JsonElement json = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        var result = new List<CustomReward>();
        if (!json.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (JsonElement reward in data.EnumerateArray())
        {
            string id = ReadString(reward, "id");
            if (id.Length == 0) continue;
            result.Add(new CustomReward(
                id,
                ReadString(reward, "title"),
                reward.TryGetProperty("cost", out JsonElement cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetInt32() : 0));
        }
        return result;
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string accessToken = await session.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Client-Id", session.ClientId);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        // 401 is in here too: a token that is missing a scope is rejected as unauthorised, and that
        // is a permission answer rather than a fault – the caller turns the feature off and moves on.
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new TwitchNotPermittedException(DescribeError(response.StatusCode, body));
        if (!response.IsSuccessStatusCode) throw new TwitchApiException(DescribeError(response.StatusCode, body));

        if (body.Length == 0) return default;
        try { return JsonDocument.Parse(body).RootElement.Clone(); }
        catch (JsonException) { return default; }
    }

    private static string DescribeError(HttpStatusCode status, string body)
    {
        string? detail = null;
        try
        {
            if (body.Length > 0 && JsonDocument.Parse(body).RootElement.TryGetProperty("message", out JsonElement message))
                detail = message.GetString();
        }
        catch (JsonException) { }

        string prefix = status switch
        {
            HttpStatusCode.Unauthorized => "Twitch nekade åtgärden – logga in igen.",
            HttpStatusCode.Forbidden => "Du saknar behörighet för den här åtgärden i kanalen.",
            HttpStatusCode.NotFound => "Twitch hittade inte kanalen eller användaren.",
            HttpStatusCode.TooManyRequests => "För många åtgärder på kort tid – vänta en stund.",
            _ => "Twitch svarade med ett fel."
        };
        return string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix} ({detail})";
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
