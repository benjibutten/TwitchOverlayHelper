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

/// <summary>
/// One emote the picker can offer. <paramref name="Group"/> is where it came from – "channel",
/// "yours" or "global" – which is what the dock sorts the picker into; the image is built from the
/// id by the same CDN pattern the chat lines already use, so no URL is carried over the wire.
/// </summary>
public sealed record UsableEmote(string Id, string Name, string Group);

/// <summary>
/// What the emote picker can offer right now.
/// </summary>
/// <param name="MissingScope">
/// The personal half is absent because the login predates the scope – a different thing from having
/// no emotes.
/// </param>
/// <param name="ChannelChecked">
/// Whether the channel's own emotes could be held against what this account may send. False means
/// they were left out rather than guessed at, which is worth saying: the picker then looks emptier
/// than the channel is, and the reason is fixable from the app.
/// </param>
public sealed record EmoteCatalog(IReadOnlyList<UsableEmote> Emotes, bool MissingScope, bool ChannelChecked);

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

    /// <summary>
    /// Pins a message at the top of the channel's chat, where the viewers see it too. Twitch keeps
    /// one mod-pinned message per channel, so a new pin quietly replaces whatever was pinned before.
    ///
    /// <para><b>PUT, and everything in the query string.</b> Every sibling call in this file posts a
    /// JSON body, and pinning reads like it should do the same – it does not, and Twitch answers a
    /// POST with a 404 that says nothing about why. No <c>duration_seconds</c> either: a pin stays
    /// until it is taken down, which is what pinning reads as.</para>
    /// </summary>
    public async Task PinMessageAsync(string broadcasterId, string messageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageId)) throw new TwitchApiException("Meddelandet saknar id och går inte att nåla fast.");
        string url = $"https://api.twitch.tv/helix/chat/pins?broadcaster_id={Uri.EscapeDataString(broadcasterId)}" +
                     $"&moderator_id={Uri.EscapeDataString(session.UserId)}&message_id={Uri.EscapeDataString(messageId)}";
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes the channel's pin down again.</summary>
    public async Task UnpinMessageAsync(string broadcasterId, string messageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageId)) throw new TwitchApiException("Meddelandet saknar id och nålen går inte att ta bort.");
        string url = $"https://api.twitch.tv/helix/chat/pins?broadcaster_id={Uri.EscapeDataString(broadcasterId)}" +
                     $"&moderator_id={Uri.EscapeDataString(session.UserId)}&message_id={Uri.EscapeDataString(messageId)}";
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

    /// <summary>One emote endpoint's answer, and whether the page cap cut it short.</summary>
    private sealed record EmotePage(IReadOnlyList<UsableEmote> Emotes, bool Truncated)
    {
        public static readonly EmotePage Empty = new([], false);
    }

    /// <summary>
    /// Everything the logged-in user may type into this channel's chat.
    ///
    /// <para>Three calls, because Twitch has no endpoint for "what may this account send here".
    /// Only the personal one needs a scope, so a login granted before that scope existed still gets
    /// a working picker – two thirds of one – instead of an error.</para>
    ///
    /// <para><b>The channel's list is not a permission list.</b> <c>chat/emotes</c> answers with
    /// every emote the channel has, subscriber tiers included, whether or not this account may use
    /// one – and an emote that may not be sent arrives in chat as loose words rather than as a
    /// picture. So where the personal list is known it decides, and the channel's list is narrowed
    /// to what appears in it. Two cases keep the whole list: your own channel, where a broadcaster
    /// may always use their own emotes, and a personal list long enough to have hit the page cap,
    /// which cannot be used to rule anything out.</para>
    ///
    /// <para>Claiming a name is not the same as drawing it: the dock draws the channel first and the
    /// global set last, but nearly every global emote is in the personal list too, so letting
    /// "yours" claim them would file Kappa under the reader's own emotes.</para>
    /// </summary>
    public async Task<EmoteCatalog> GetUsableEmotesAsync(string broadcasterId, CancellationToken cancellationToken = default)
    {
        // A channel we have not joined yet simply contributes nothing – the rest of the picker is
        // still worth showing, and the dock asks again once the room is known.
        EmotePage channel = broadcasterId.Length > 0
            ? await ReadEmotesAsync(
                $"https://api.twitch.tv/helix/chat/emotes?broadcaster_id={Uri.EscapeDataString(broadcasterId)}",
                "channel", 1, cancellationToken).ConfigureAwait(false)
            : EmotePage.Empty;

        bool missingScope = !session.HasScope(TwitchAuth.EmotesScope);
        EmotePage yours = EmotePage.Empty;
        if (!missingScope)
        {
            // Named with the channel as well: that is what adds this channel's follower emotes to
            // the answer. Someone subscribed to a hundred channels has a long list, so it is paged
            // – and capped, because a picker nobody can scroll through is not a better picker. The
            // page size is Twitch's own: this endpoint takes user_id, broadcaster_id and after, and
            // asking it for a "first" it does not document would be relying on it being ignored.
            string url = $"https://api.twitch.tv/helix/chat/emotes/user?user_id={Uri.EscapeDataString(session.UserId)}";
            if (broadcasterId.Length > 0) url += $"&broadcaster_id={Uri.EscapeDataString(broadcasterId)}";
            try
            {
                yours = await ReadEmotesAsync(url, "yours", 10, cancellationToken).ConfigureAwait(false);
            }
            catch (TwitchNotPermittedException)
            {
                // The token says it has the scope and Twitch says otherwise – treat it as the same
                // "log in again" answer rather than failing the whole picker.
                missingScope = true;
            }
        }

        EmotePage global = await ReadEmotesAsync("https://api.twitch.tv/helix/chat/emotes/global", "global", 1, cancellationToken).ConfigureAwait(false);

        // A broadcaster may always use their own emotes, so their channel needs no checking at all.
        bool ownChannel = broadcasterId.Length > 0 && string.Equals(broadcasterId, session.UserId, StringComparison.Ordinal);
        // Anywhere else the personal list is the only thing that can say what may be sent. Without
        // it – no scope, or a list that stopped at the page cap – there is nothing to check against.
        bool channelChecked = ownChannel || (!missingScope && !yours.Truncated);

        IReadOnlyList<UsableEmote> channelEmotes;
        if (ownChannel)
        {
            channelEmotes = channel.Emotes;
        }
        else if (channelChecked)
        {
            var allowed = new HashSet<string>(yours.Emotes.Select(emote => emote.Name), StringComparer.Ordinal);
            foreach (UsableEmote emote in global.Emotes) allowed.Add(emote.Name);
            channelEmotes = channel.Emotes.Where(emote => allowed.Contains(emote.Name)).ToArray();
        }
        else
        {
            // Left out rather than guessed at. Showing them and hoping is how a subscriber emote
            // gets offered to somebody who is not subscribed: it goes into the box as a picture,
            // reaches the chat as loose words, and the first sign anything was wrong is the message
            // itself. A smaller picker and a line saying why is the honest version of not knowing.
            channelEmotes = [];
        }

        var byName = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<UsableEmote>();

        void Take(IEnumerable<UsableEmote> emotes)
        {
            foreach (UsableEmote emote in emotes)
            {
                if (emote.Id.Length == 0 || emote.Name.Length == 0) continue;
                if (!byName.Add(emote.Name)) continue;
                ordered.Add(emote);
            }
        }

        Take(channelEmotes);
        Take(global.Emotes);
        Take(yours.Emotes);
        return new EmoteCatalog(ordered, missingScope, channelChecked);
    }

    /// <summary>Reads one emote endpoint, following its cursor for at most <paramref name="maxPages"/> pages.</summary>
    private async Task<EmotePage> ReadEmotesAsync(string url, string group, int maxPages, CancellationToken cancellationToken)
    {
        var result = new List<UsableEmote>();
        string? cursor = null;
        bool truncated = false;

        for (int page = 0; page < maxPages; page++)
        {
            string pageUrl = cursor is null ? url : $"{url}{(url.Contains('?') ? '&' : '?')}after={Uri.EscapeDataString(cursor)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
            JsonElement json = await SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!json.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array) break;
            foreach (JsonElement emote in data.EnumerateArray())
                result.Add(new UsableEmote(ReadString(emote, "id"), ReadString(emote, "name"), group));

            cursor = json.TryGetProperty("pagination", out JsonElement pagination) && pagination.ValueKind == JsonValueKind.Object
                ? ReadString(pagination, "cursor")
                : string.Empty;
            if (string.IsNullOrEmpty(cursor)) break;
            // More was on offer than we took. Said out loud because a list that stops early is safe
            // to read from and unsafe to rule things out with.
            truncated = page == maxPages - 1;
        }
        return new EmotePage(result, truncated);
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
