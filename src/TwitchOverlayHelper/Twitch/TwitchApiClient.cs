using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TwitchOverlayHelper.Twitch;

public sealed record RaidCandidate(string UserId, string Login, string DisplayName, string GameName, int ViewerCount, string ThumbnailUrl);

public sealed class TwitchApiException(string message) : Exception(message);

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

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string accessToken = await session.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Client-Id", session.ClientId);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
