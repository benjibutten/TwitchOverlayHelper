using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Twitch;

/// <summary>
/// How the logged-in account looks in the joined channel, as Twitch describes it in USERSTATE.
/// Our own messages never come back down the connection that sent them, so a line written from
/// the dock is dressed in this.
/// </summary>
/// <param name="MessageId">
/// Twitch's id for the message this USERSTATE answers. Null when Twitch sends none; the line can
/// then be shown but not pinned or deleted through Helix.
/// </param>
internal sealed record UserState(string? MessageId, string? DisplayName, string? Color, IReadOnlyList<ChatBadge> Badges)
{
    /// <summary>Whether Twitch calls us a moderator here. See ChatMessage.HasModTag for why the badges cannot answer this.</summary>
    public bool HasModTag { get; init; }
}

internal static class IrcMessageParser
{
    public static bool TryParseChatMessage(string line, out ChatMessage? message)
    {
        message = null;
        if (!line.StartsWith('@')) return false;

        int tagEnd = line.IndexOf(' ');
        if (tagEnd < 0) return false;
        var tags = ParseTags(line[1..tagEnd]);

        int commandAt = line.IndexOf(" PRIVMSG #", tagEnd, StringComparison.Ordinal);
        if (commandAt < 0) return false;
        if (ReadTrailing(line, commandAt + 10) is not { } text) return false;
        bool isAction = TryUnwrapAction(ref text);
        string login = ParseLogin(line[tagEnd..commandAt]);
        string displayName = tags.GetValueOrDefault("display-name") ?? (login.Length > 0 ? login : "Okänd");
        var badges = ParseBadges(tags.GetValueOrDefault("badges"));

        // Emote positions are counted against the text as Twitch sent it, so they have to be read
        // before the reply mention is cut away – and moved along with it.
        IReadOnlyList<EmoteSpan> emotes = ParseEmotes(tags.GetValueOrDefault("emotes"), text);
        IReadOnlyList<GifSpan> gifs = ParseGifs(tags.GetValueOrDefault("gifs"), text);
        ChatReply? reply = ParseReply(tags);
        if (reply is not null) StripReplyMention(ref text, ref emotes, ref gifs, reply);

        message = new ChatMessage(
            tags.GetValueOrDefault("id") ?? Guid.NewGuid().ToString("N"),
            displayName,
            text,
            EmptyToNull(tags.GetValueOrDefault("color")),
            badges,
            tags.GetValueOrDefault("first-msg") == "1",
            tags.GetValueOrDefault("msg-id") == "highlighted-message",
            ParseTimestamp(tags.GetValueOrDefault("tmi-sent-ts")),
            emotes)
        {
            Gifs = gifs,
            UserId = tags.GetValueOrDefault("user-id") ?? string.Empty,
            UserLogin = login,
            HasModTag = ReadModTag(tags),
            IsAction = isAction,
            Reply = reply,
            RewardId = EmptyToNull(tags.GetValueOrDefault("custom-reward-id")),
            // A cheer is an ordinary message that happens to carry bits; Twitch sends no notice for it.
            Bits = ParseCount(tags.GetValueOrDefault("bits")),
            // Undocumented but sent in practice. Read from IRC rather than EventSub so the effect
            // also shows when logged out or in someone else's channel.
            MessageEffectId = EmptyToNull(tags.GetValueOrDefault("animation-id"))
        };
        return true;
    }

    /// <summary>
    /// Reads the reply-parent-* tags, which Twitch sends only on messages written through the reply
    /// button. Returns null for a message that is not a reply.
    /// </summary>
    internal static ChatReply? ParseReply(Dictionary<string, string> tags)
    {
        if (EmptyToNull(tags.GetValueOrDefault("reply-parent-msg-id")) is not { } parentId) return null;

        string login = (EmptyToNull(tags.GetValueOrDefault("reply-parent-user-login")) ?? string.Empty).ToLowerInvariant();
        return new ChatReply(
            parentId,
            tags.GetValueOrDefault("reply-parent-user-id") ?? string.Empty,
            login,
            EmptyToNull(tags.GetValueOrDefault("reply-parent-display-name")) ?? (login.Length > 0 ? login : "Okänd"),
            tags.GetValueOrDefault("reply-parent-msg-body") ?? string.Empty);
    }

    /// <summary>
    /// Cuts the "@name " that Twitch pastes onto the front of every reply, shifting the emote and
    /// GIF spans to match, so a reply like "@name !pet" still starts with its command. Returns
    /// whether a mention was removed. Matches the display name or the login, since Twitch writes
    /// whichever the sender's client used.
    /// </summary>
    internal static bool StripReplyMention(
        ref string text,
        ref IReadOnlyList<EmoteSpan> emotes,
        ref IReadOnlyList<GifSpan> gifs,
        ChatReply reply)
    {
        foreach (string name in new[] { reply.ParentDisplayName, reply.ParentLogin })
        {
            if (name.Length == 0) continue;
            string mention = $"@{name} ";
            if (!text.StartsWith(mention, StringComparison.OrdinalIgnoreCase)) continue;

            text = text[mention.Length..];
            emotes = ShiftEmotes(emotes, mention.Length);
            gifs = ShiftGifs(gifs, mention.Length);
            return true;
        }
        return false;
    }

    private static IReadOnlyList<EmoteSpan> ShiftEmotes(IReadOnlyList<EmoteSpan> emotes, int offset)
    {
        if (emotes.Count == 0) return emotes;
        var result = new List<EmoteSpan>(emotes.Count);
        foreach (EmoteSpan emote in emotes)
        {
            // A span inside the cut-away mention has nothing left to point at.
            if (emote.Start < offset) continue;
            result.Add(emote with { Start = emote.Start - offset });
        }
        return result;
    }

    private static IReadOnlyList<GifSpan> ShiftGifs(IReadOnlyList<GifSpan> gifs, int offset)
    {
        if (gifs.Count == 0) return gifs;
        var result = new List<GifSpan>(gifs.Count);
        foreach (GifSpan gif in gifs)
        {
            if (gif.Start < offset) continue;
            result.Add(gif with { Start = gif.Start - offset });
        }
        return result;
    }

    /// <summary>Twitch wraps /me in SOH control characters; they must not reach the reader.</summary>
    internal static bool TryUnwrapAction(ref string text)
    {
        const char marker = (char)1;
        const string opening = "ACTION ";
        if (text.Length < opening.Length + 2 || text[0] != marker || text[^1] != marker) return false;
        if (!text.AsSpan(1).StartsWith(opening, StringComparison.Ordinal)) return false;
        text = text[(opening.Length + 1)..^1];
        return true;
    }

    /// <summary>Extracts "name" from the " :name!name@name.tmi.twitch.tv" prefix that precedes PRIVMSG.</summary>
    internal static string ParseLogin(string prefix)
    {
        int start = prefix.IndexOf(':');
        if (start < 0) return string.Empty;
        int end = prefix.IndexOf('!', start);
        return end > start ? prefix[(start + 1)..end].ToLowerInvariant() : string.Empty;
    }

    /// <summary>
    /// Parses USERSTATE, which Twitch sends when we join a channel and again after every line we
    /// send that it accepts. Its arrival is the only confirmation that a message went out: a
    /// refused line is answered with a NOTICE and no USERSTATE.
    /// </summary>
    public static bool TryParseUserState(string line, out UserState? state)
    {
        state = null;
        if (!line.StartsWith('@')) return false;

        int tagEnd = line.IndexOf(' ');
        if (tagEnd < 0) return false;
        if (line.IndexOf(" USERSTATE #", tagEnd, StringComparison.Ordinal) < 0) return false;

        var tags = ParseTags(line[1..tagEnd]);
        state = new UserState(
            EmptyToNull(tags.GetValueOrDefault("id")),
            EmptyToNull(tags.GetValueOrDefault("display-name")),
            EmptyToNull(tags.GetValueOrDefault("color")),
            ParseBadges(tags.GetValueOrDefault("badges")))
        {
            HasModTag = ReadModTag(tags)
        };
        return true;
    }

    /// <summary>
    /// Parses a NOTICE saying a line we sent was not delivered – banned, timed out, slow mode,
    /// followers only, a duplicate, the rate limit. Twitch marks those with a msg-id in the
    /// <c>msg_*</c> family and sends no USERSTATE after them. Room notices such as "slow mode on"
    /// use the same command without that msg-id and are not refusals.
    /// </summary>
    public static bool TryParseSendRefusal(string line, out string? reason)
    {
        reason = null;
        if (!line.StartsWith('@')) return false;

        int tagEnd = line.IndexOf(' ');
        if (tagEnd < 0) return false;
        int notice = line.IndexOf(" NOTICE ", tagEnd, StringComparison.Ordinal);
        if (notice < 0) return false;

        var tags = ParseTags(line[1..tagEnd]);
        string id = tags.GetValueOrDefault("msg-id") ?? string.Empty;
        // "unrecognized_cmd" is the same answer for a line beginning with a slash: nothing was said.
        if (!id.StartsWith("msg_", StringComparison.Ordinal) && id != "unrecognized_cmd") return false;

        int text = line.IndexOf(" :", notice, StringComparison.Ordinal);
        reason = text >= 0 ? line[(text + 2)..].Trim() : string.Empty;
        // Twitch's own wording is the useful part; the msg-id is the fallback when there is none.
        if (reason.Length == 0) reason = id;
        return true;
    }

    /// <summary>Parses CLEARMSG and CLEARCHAT so deletions, timeouts and bans become visible.</summary>
    public static bool TryParseModerationEvent(string line, out ChatModerationEvent? moderationEvent)
    {
        moderationEvent = null;
        if (!line.StartsWith('@')) return false;

        int tagEnd = line.IndexOf(' ');
        if (tagEnd < 0) return false;
        var tags = ParseTags(line[1..tagEnd]);
        DateTimeOffset at = ParseTimestamp(tags.GetValueOrDefault("tmi-sent-ts"));

        if (line.IndexOf(" CLEARMSG #", tagEnd, StringComparison.Ordinal) >= 0)
        {
            moderationEvent = new ChatModerationEvent(
                ChatEventKind.MessageDeleted,
                EmptyToNull(tags.GetValueOrDefault("target-msg-id")),
                null,
                EmptyToNull(tags.GetValueOrDefault("login"))?.ToLowerInvariant(),
                null,
                at);
            return true;
        }

        int clearChat = line.IndexOf(" CLEARCHAT #", tagEnd, StringComparison.Ordinal);
        if (clearChat < 0) return false;

        // A trailing " :login" names the purged user; without it Twitch cleared the entire room.
        int targetAt = line.IndexOf(" :", clearChat + 12, StringComparison.Ordinal);
        string? target = targetAt >= 0 ? line[(targetAt + 2)..].Trim().ToLowerInvariant() : null;
        if (string.IsNullOrEmpty(target))
        {
            moderationEvent = new ChatModerationEvent(ChatEventKind.ChatCleared, null, null, null, null, at);
            return true;
        }

        int? duration = int.TryParse(tags.GetValueOrDefault("ban-duration"), out int seconds) ? seconds : null;
        moderationEvent = new ChatModerationEvent(
            ChatEventKind.UserPurged,
            null,
            EmptyToNull(tags.GetValueOrDefault("target-user-id")),
            target,
            duration,
            at);
        return true;
    }

    /// <summary>
    /// Parses USERNOTICE, which is how subs, raids and announcements reach IRC. An unrecognised
    /// msg-id comes back as <see cref="ChatEventType.Other"/> carrying Twitch's own system-msg.
    /// </summary>
    public static bool TryParseUserNotice(string line, out ChatEvent? chatEvent)
    {
        chatEvent = null;
        if (!line.StartsWith('@')) return false;

        int tagEnd = line.IndexOf(' ');
        if (tagEnd < 0) return false;

        const string command = " USERNOTICE #";
        int commandAt = line.IndexOf(command, tagEnd, StringComparison.Ordinal);
        if (commandAt < 0) return false;

        var tags = ParseTags(line[1..tagEnd]);
        string msgId = tags.GetValueOrDefault("msg-id") ?? string.Empty;

        // The trailing text is the chatter's own words and is absent for most notice types.
        string? message = null;
        if (ReadTrailing(line, commandAt + command.Length) is { } trailing && EmptyToNull(trailing) is { } raw)
        {
            TryUnwrapAction(ref raw);
            message = raw;
        }

        string login = (EmptyToNull(tags.GetValueOrDefault("login")) ?? ParseLogin(line[tagEnd..commandAt])).ToLowerInvariant();
        string displayName = tags.GetValueOrDefault("display-name") ?? (login.Length > 0 ? login : "Okänd");

        chatEvent = new ChatEvent(
            MapType(msgId, tags),
            tags.GetValueOrDefault("id") ?? Guid.NewGuid().ToString("N"),
            displayName,
            ParseTimestamp(tags.GetValueOrDefault("tmi-sent-ts")))
        {
            UserLogin = login,
            UserId = tags.GetValueOrDefault("user-id") ?? string.Empty,
            NameColor = EmptyToNull(tags.GetValueOrDefault("color")),
            Badges = ParseBadges(tags.GetValueOrDefault("badges")),
            SystemMessage = EmptyToNull(tags.GetValueOrDefault("system-msg")),
            Message = message,
            Emotes = message is null ? Array.Empty<EmoteSpan>() : ParseEmotes(tags.GetValueOrDefault("emotes"), message),
            Tier = EmptyToNull(tags.GetValueOrDefault("msg-param-sub-plan")),
            // Legacy msg-param-months is often 0 on a resub, so the cumulative count comes first.
            Months = ParseCount(tags.GetValueOrDefault("msg-param-cumulative-months"))
                     ?? ParseCount(tags.GetValueOrDefault("msg-param-months")),
            // A streak is only true when the viewer chose to share it.
            StreakMonths = tags.GetValueOrDefault("msg-param-should-share-streak") == "1"
                ? ParseCount(tags.GetValueOrDefault("msg-param-streak-months"))
                : null,
            GiftCount = ParseCount(tags.GetValueOrDefault("msg-param-mass-gift-count")),
            RecipientDisplayName = EmptyToNull(tags.GetValueOrDefault("msg-param-recipient-display-name"))
                                   ?? EmptyToNull(tags.GetValueOrDefault("msg-param-recipient-user-name")),
            ViewerCount = ParseCount(tags.GetValueOrDefault("msg-param-viewerCount")),
            Bits = ParseCount(tags.GetValueOrDefault("msg-param-threshold")),
            StreakValue = ParseCount(tags.GetValueOrDefault("msg-param-value")),
            AnnouncementColor = EmptyToNull(tags.GetValueOrDefault("msg-param-color"))
        };
        return true;
    }

    private static ChatEventType MapType(string msgId, Dictionary<string, string> tags) => msgId switch
    {
        "sub" or "resub" => ChatEventType.Subscription,
        "subgift" => ChatEventType.SubGift,
        "submysterygift" => ChatEventType.CommunityGift,
        "giftpaidupgrade" or "anongiftpaidupgrade" or "primepaidupgrade" => ChatEventType.SubUpgrade,
        "raid" => ChatEventType.Raid,
        "unraid" => ChatEventType.Unraid,
        // Sent in practice but missing from Twitch's IRC documentation.
        "announcement" => ChatEventType.Announcement,
        "bitsbadgetier" => ChatEventType.BitsBadge,
        "viewermilestone" when tags.GetValueOrDefault("msg-param-category") == "watch-streak" => ChatEventType.WatchStreak,
        "ritual" when tags.GetValueOrDefault("msg-param-ritual-name") == "new_chatter" => ChatEventType.NewChatter,
        _ => ChatEventType.Other
    };

    // Tag format: "25:0-4,12-16/1902:6-10". Ranges are inclusive and counted in
    // Unicode code points, so they must be mapped to UTF-16 indices before use.
    internal static IReadOnlyList<EmoteSpan> ParseEmotes(string? raw, string text)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<EmoteSpan>();

        int[] codePointToUtf16 = BuildCodePointIndexMap(text);
        var result = new List<EmoteSpan>();
        foreach (string emote in raw.Split('/'))
        {
            int colon = emote.IndexOf(':');
            if (colon <= 0) continue;
            string emoteId = emote[..colon];
            foreach (string range in emote[(colon + 1)..].Split(','))
            {
                int dash = range.IndexOf('-');
                if (dash <= 0
                    || !int.TryParse(range[..dash], out int start)
                    || !int.TryParse(range[(dash + 1)..], out int end)
                    || start < 0 || end < start || end >= codePointToUtf16.Length - 1)
                    continue;
                int utf16Start = codePointToUtf16[start];
                int utf16End = codePointToUtf16[end + 1];
                result.Add(new EmoteSpan(emoteId, utf16Start, utf16End - utf16Start));
            }
        }
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    // Tag format: "0-33|joSNxeswxuc74Juo8X|https://media4.giphy.com/media/...". Ranges are
    // inclusive and counted in Unicode code points, like the emote tag. Twitch asks that the
    // address be used unchanged; it is only checked to be https, so a line from IRC cannot point
    // the streamer's machine at another scheme.
    internal static IReadOnlyList<GifSpan> ParseGifs(string? raw, string text)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<GifSpan>();

        int[] codePointToUtf16 = BuildCodePointIndexMap(text);
        var result = new List<GifSpan>();
        foreach (string entry in SplitGifEntries(raw))
        {
            // Three fields at most, so an address containing a pipe of its own stays whole.
            string[] parts = entry.Split('|', 3);
            if (parts.Length < 3) continue;

            int dash = parts[0].IndexOf('-');
            if (dash <= 0
                || !int.TryParse(parts[0][..dash], out int start)
                || !int.TryParse(parts[0][(dash + 1)..], out int end)
                || start < 0 || end < start || end >= codePointToUtf16.Length - 1)
                continue;
            if (!IsHttpsUrl(parts[2])) continue;

            int utf16Start = codePointToUtf16[start];
            int utf16End = codePointToUtf16[end + 1];
            result.Add(new GifSpan(parts[1], parts[2], utf16Start, utf16End - utf16Start));
        }
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    /// <summary>
    /// Cuts the gifs tag into its entries. Twitch separates them with a comma, which a query
    /// string may also contain, so a comma only starts a new entry when "digits-digits|" follows.
    /// </summary>
    private static List<string> SplitGifEntries(string raw)
    {
        var entries = new List<string>();
        foreach (string part in raw.Split(','))
        {
            if (entries.Count > 0 && !StartsEntry(part)) entries[^1] += "," + part;
            else entries.Add(part);
        }
        return entries;
    }

    private static bool StartsEntry(string part)
    {
        int i = 0;
        while (i < part.Length && char.IsAsciiDigit(part[i])) i++;
        if (i == 0 || i >= part.Length || part[i] != '-') return false;
        int dash = i++;
        while (i < part.Length && char.IsAsciiDigit(part[i])) i++;
        return i > dash + 1 && i < part.Length && part[i] == '|';
    }

    private static bool IsHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? url) && url.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// The UTF-16 index where each code point of the text begins, with the text's length as a
    /// final entry so an inclusive Twitch range end can be read as map[end + 1].
    ///
    /// <para>Callers must bound <c>end</c> against <c>Length - 1</c> rather than testing
    /// <c>end + 1</c>: the range comes off the wire, and <see cref="int.MaxValue"/> + 1 wraps
    /// negative and passes any upper bound.</para>
    /// </summary>
    private static int[] BuildCodePointIndexMap(string text)
    {
        var map = new List<int>(text.Length + 1);
        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
            map.Add(i);
        map.Add(text.Length);
        return map.ToArray();
    }

    public static string? TryGetRoomId(string line)
    {
        if (!line.StartsWith('@')) return null;
        int end = line.IndexOf(' ');
        return end > 0 ? ParseTags(line[1..end]).GetValueOrDefault("room-id") : null;
    }

    private static Dictionary<string, string> ParseTags(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in raw.Split(';'))
        {
            int equals = part.IndexOf('=');
            if (equals >= 0) result[part[..equals]] = Unescape(part[(equals + 1)..]);
            else result[part] = string.Empty;
        }
        return result;
    }

    /// <summary>
    /// What was said, taken from after the channel name. Null when the line carries no text at all.
    /// </summary>
    /// <param name="channelAt">Index of the first character of the channel name.</param>
    private static string? ReadTrailing(string line, int channelAt)
    {
        int channelEnd = line.IndexOf(' ', channelAt);
        if (channelEnd < 0) return null;

        // The leading ":" is optional: Twitch always sends it, but the recent-messages service
        // omits it for single-word messages, as IRC allows.
        string trailing = line[(channelEnd + 1)..];
        return trailing.StartsWith(':') ? trailing[1..] : trailing;
    }

    /// <summary>
    /// Whether Twitch marked the sender as a moderator of this room, from the mod tag with
    /// user-type as a fallback. False for the broadcaster: Twitch does not count the owner as a mod
    /// of their own channel, so callers check the broadcaster badge separately.
    /// </summary>
    private static bool ReadModTag(Dictionary<string, string> tags) =>
        tags.GetValueOrDefault("mod") == "1" || tags.GetValueOrDefault("user-type") == "mod";

    private static IReadOnlyList<ChatBadge> ParseBadges(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<ChatBadge>();
        var result = new List<ChatBadge>();
        foreach (string badge in raw.Split(','))
        {
            int slash = badge.IndexOf('/');
            if (slash > 0 && slash < badge.Length - 1)
                result.Add(new ChatBadge(badge[..slash], badge[(slash + 1)..]));
        }
        return result;
    }

    private static string Unescape(string value) => value
        .Replace("\\s", " ", StringComparison.Ordinal)
        .Replace("\\:", ";", StringComparison.Ordinal)
        .Replace("\\r", "\r", StringComparison.Ordinal)
        .Replace("\\n", "\n", StringComparison.Ordinal)
        .Replace("\\\\", "\\", StringComparison.Ordinal);

    private static DateTimeOffset ParseTimestamp(string? value) =>
        long.TryParse(value, out long milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : DateTimeOffset.Now;

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Reads a counting tag. Twitch writes 0 where it means "not applicable", so 0 is null too.</summary>
    private static int? ParseCount(string? value) =>
        int.TryParse(value, out int count) && count > 0 ? count : null;
}
