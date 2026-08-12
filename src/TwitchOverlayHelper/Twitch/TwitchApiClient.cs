using System.Globalization;
using System.Net;
using TwitchOverlayHelper.Diagnostics;
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
/// One of the channel's own bits Power-ups. <paramref name="RequiresInput"/> matters more here than
/// it does for a reward: a Power-up that asks for nothing sends no text, and there is nothing to
/// read out loud.
/// </summary>
public sealed record CustomPowerUp(string Id, string Title, int Bits, bool Enabled, bool RequiresInput);

/// <summary>
/// A reward to create, worded the way the pet settings ask for it.
/// </summary>
/// <param name="RequireInput">
/// Whether the viewer must type something. The pets read that text to pick a species, so it is
/// normally on – but a reward that always gives the default pet is a fine thing to sell too.
/// </param>
/// <param name="CooldownSeconds">Zero for no cooldown, which is Twitch's own default.</param>
public sealed record NewCustomReward(
    string Title,
    int Cost,
    string? Prompt,
    bool RequireInput,
    int CooldownSeconds,
    string? BackgroundColor);

/// <summary>
/// A redemption sitting in the channel's request queue, as Helix hands it back.
/// <paramref name="RedeemedAt"/> is what tells a redemption from before this app started from one
/// that arrived while it was starting.
/// </summary>
public sealed record QueuedRedemption(string Id, string RewardId, string UserName, int Cost, DateTimeOffset RedeemedAt);

/// <summary>
/// What a redemption should become. Twitch only accepts these two from an app, and only for a
/// redemption still sitting unfulfilled in the queue.
/// </summary>
public enum RedemptionStatus
{
    /// <summary>Done and paid for: the points stay spent.</summary>
    Fulfilled,
    /// <summary>Refused: Twitch gives the viewer their points back.</summary>
    Canceled
}

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
    /// <param name="onlyManageable">
    /// Narrows the answer to the rewards this client id created, which are the only ones whose
    /// redemptions it may ever answer.
    /// </param>
    public async Task<IReadOnlyList<CustomReward>> GetCustomRewardsAsync(
        string broadcasterId, bool onlyManageable = false, CancellationToken cancellationToken = default)
    {
        string url = $"https://api.twitch.tv/helix/channel_points/custom_rewards?broadcaster_id={Uri.EscapeDataString(broadcasterId)}";
        if (onlyManageable) url += "&only_manageable_rewards=true";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        JsonElement json = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        var result = new List<CustomReward>();
        if (!json.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (JsonElement reward in data.EnumerateArray())
        {
            string id = ReadString(reward, "id");
            if (id.Length == 0) continue;
            result.Add(ReadReward(reward));
        }
        return result;
    }

    /// <summary>
    /// Creates a reward owned by this app, which is the only kind whose redemptions it may answer
    /// later. Everything else about refunding follows from that one fact.
    ///
    /// <para><b>The queue is the point.</b> <c>should_redemptions_skip_request_queue</c> is sent as
    /// false and is not offered as a choice: a redemption that skips the queue is fulfilled the
    /// moment it is made, and a fulfilled redemption can never be refunded. Skipping the queue and
    /// refunding are the same setting seen from two sides.</para>
    ///
    /// <para>Twitch refuses a second reward with a title the channel already uses, so a streamer
    /// moving over from a hand-made reward has to rename or delete the old one first. That comes
    /// back as an ordinary <see cref="TwitchApiException"/> carrying Twitch's own wording.</para>
    /// </summary>
    public async Task<CustomReward> CreateCustomRewardAsync(string broadcasterId, NewCustomReward reward, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reward.Title)) throw new TwitchApiException("Belöningen behöver ett namn.");

        var payload = new StringBuilder("{\"title\":").Append(JsonSerializer.Serialize(reward.Title.Trim()))
            .Append(",\"cost\":").Append(Math.Max(1, reward.Cost))
            .Append(",\"is_enabled\":true")
            .Append(",\"should_redemptions_skip_request_queue\":false")
            .Append(",\"is_user_input_required\":").Append(reward.RequireInput ? "true" : "false");
        if (!string.IsNullOrWhiteSpace(reward.Prompt))
            payload.Append(",\"prompt\":").Append(JsonSerializer.Serialize(reward.Prompt.Trim()));
        if (reward.CooldownSeconds > 0)
            payload.Append(",\"is_global_cooldown_enabled\":true,\"global_cooldown_seconds\":").Append(reward.CooldownSeconds);
        if (!string.IsNullOrWhiteSpace(reward.BackgroundColor))
            payload.Append(",\"background_color\":").Append(JsonSerializer.Serialize(reward.BackgroundColor.Trim()));
        payload.Append('}');

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.twitch.tv/helix/channel_points/custom_rewards?broadcaster_id={Uri.EscapeDataString(broadcasterId)}")
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        JsonElement json = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!json.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            throw new TwitchApiException("Twitch skapade belöningen men svarade inte med den.");
        return ReadReward(data[0]);
    }

    /// <summary>
    /// Answers one redemption. <see cref="RedemptionStatus.Canceled"/> is the refund: Twitch puts the
    /// points back on the viewer's balance itself, so there is nothing else to pay back by hand.
    ///
    /// <para>Only works on a redemption still sitting unfulfilled, and only on a reward this client
    /// id created – a reward made in the dashboard answers 403 no matter which scopes the token
    /// carries. That refusal arrives as <see cref="TwitchNotPermittedException"/>, which is what
    /// lets a caller tell "not ours to answer" from "Twitch is having a bad day".</para>
    /// </summary>
    public async Task UpdateRedemptionStatusAsync(
        string broadcasterId,
        string rewardId,
        string redemptionId,
        RedemptionStatus status,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rewardId) || string.IsNullOrWhiteSpace(redemptionId))
            throw new TwitchApiException("Inlösen saknar id och går inte att besvara.");

        string url = $"https://api.twitch.tv/helix/channel_points/custom_rewards/redemptions?id={Uri.EscapeDataString(redemptionId)}" +
                     $"&broadcaster_id={Uri.EscapeDataString(broadcasterId)}&reward_id={Uri.EscapeDataString(rewardId)}";
        string body = status == RedemptionStatus.Canceled ? "{\"status\":\"CANCELED\"}" : "{\"status\":\"FULFILLED\"}";
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What is still waiting in one reward's queue. Read at startup: the pets live in memory only,
    /// so anything left unfulfilled from before this process began belongs to a pet that no longer
    /// exists and has to be paid back.
    /// </summary>
    public async Task<IReadOnlyList<QueuedRedemption>> GetUnfulfilledRedemptionsAsync(
        string broadcasterId, string rewardId, CancellationToken cancellationToken = default)
    {
        var result = new List<QueuedRedemption>();
        string? cursor = null;

        // Fifty per page. The cap is high enough to cover a stream's worth of pet redemptions
        // several times over, and exists so a queue that has been left to grow for months cannot
        // turn one reconnect into a thousand API calls. Hitting it is said out loud rather than
        // passed over: the rest stays in the queue, and the streamer has the dashboard for it.
        const int maxPages = 40;
        for (int page = 0; page < maxPages; page++)
        {
            string url = "https://api.twitch.tv/helix/channel_points/custom_rewards/redemptions" +
                         $"?broadcaster_id={Uri.EscapeDataString(broadcasterId)}&reward_id={Uri.EscapeDataString(rewardId)}" +
                         "&status=UNFULFILLED&first=50";
            if (cursor is { Length: > 0 }) url += $"&after={Uri.EscapeDataString(cursor)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            JsonElement json = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!json.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array) break;

            foreach (JsonElement redemption in data.EnumerateArray())
            {
                string id = ReadString(redemption, "id");
                if (id.Length == 0) continue;
                JsonElement reward = redemption.TryGetProperty("reward", out JsonElement nested) ? nested : default;
                result.Add(new QueuedRedemption(
                    id,
                    rewardId,
                    ReadString(redemption, "user_name"),
                    reward.ValueKind == JsonValueKind.Object && reward.TryGetProperty("cost", out JsonElement cost) && cost.ValueKind == JsonValueKind.Number
                        ? cost.GetInt32()
                        : 0,
                    DateTimeOffset.TryParse(ReadString(redemption, "redeemed_at"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal, out DateTimeOffset at)
                        ? at
                        : DateTimeOffset.MinValue));
            }

            cursor = json.TryGetProperty("pagination", out JsonElement pagination) && pagination.ValueKind == JsonValueKind.Object
                ? ReadString(pagination, "cursor")
                : string.Empty;
            if (string.IsNullOrEmpty(cursor)) break;
            if (page == maxPages - 1)
                AppLog.Warn($"Pets: kön för belöning {rewardId} är längre än {maxPages * 50} inlösen – resten ligger kvar i Twitchs kö.");
        }
        return result;
    }

    /// <summary>
    /// The channel's own bits Power-ups, so the settings can offer them by name instead of asking
    /// for an id pasted out of a dashboard.
    ///
    /// <para>Read-only, and that is the whole API: Twitch offers no way for an app to create a
    /// custom Power-up, change one, or answer a redemption of one. A channel may have fifty at
    /// most, so this is deliberately unpaged.</para>
    /// </summary>
    public async Task<IReadOnlyList<CustomPowerUp>> GetCustomPowerUpsAsync(string broadcasterId, CancellationToken cancellationToken = default)
    {
        string url = $"https://api.twitch.tv/helix/bits/custom_power_ups?broadcaster_id={Uri.EscapeDataString(broadcasterId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        JsonElement json = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        var result = new List<CustomPowerUp>();
        if (!json.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (JsonElement powerUp in data.EnumerateArray())
        {
            string id = ReadString(powerUp, "id");
            if (id.Length == 0) continue;
            result.Add(new CustomPowerUp(
                id,
                ReadString(powerUp, "title"),
                powerUp.TryGetProperty("bits", out JsonElement bits) && bits.ValueKind == JsonValueKind.Number ? bits.GetInt32() : 0,
                // Absent reads as enabled: a Power-up we cannot judge is better offered than hidden,
                // and the list is only ever used to fill in an id.
                !powerUp.TryGetProperty("is_enabled", out JsonElement enabled) || enabled.ValueKind != JsonValueKind.False,
                powerUp.TryGetProperty("is_user_input_required", out JsonElement input) && input.ValueKind == JsonValueKind.True));
        }
        return result;
    }

    private static CustomReward ReadReward(JsonElement reward) => new(
        ReadString(reward, "id"),
        ReadString(reward, "title"),
        reward.TryGetProperty("cost", out JsonElement cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetInt32() : 0);

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
