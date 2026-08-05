using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Twitch;

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
        int textAt = line.IndexOf(" :", commandAt + 10, StringComparison.Ordinal);
        if (textAt < 0) return false;

        string text = line[(textAt + 2)..];
        bool isAction = TryUnwrapAction(ref text);
        string login = ParseLogin(line[tagEnd..commandAt]);
        string displayName = tags.GetValueOrDefault("display-name") ?? (login.Length > 0 ? login : "Okänd");
        var badges = ParseBadges(tags.GetValueOrDefault("badges"));

        message = new ChatMessage(
            tags.GetValueOrDefault("id") ?? Guid.NewGuid().ToString("N"),
            displayName,
            text,
            EmptyToNull(tags.GetValueOrDefault("color")),
            badges,
            tags.GetValueOrDefault("first-msg") == "1",
            tags.GetValueOrDefault("msg-id") == "highlighted-message",
            ParseTimestamp(tags.GetValueOrDefault("tmi-sent-ts")),
            ParseEmotes(tags.GetValueOrDefault("emotes"), text))
        {
            UserId = tags.GetValueOrDefault("user-id") ?? string.Empty,
            UserLogin = login,
            IsAction = isAction,
            RewardId = EmptyToNull(tags.GetValueOrDefault("custom-reward-id"))
        };
        return true;
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
                    || start < 0 || end < start || end + 1 >= codePointToUtf16.Length)
                    continue;
                int utf16Start = codePointToUtf16[start];
                int utf16End = codePointToUtf16[end + 1];
                result.Add(new EmoteSpan(emoteId, utf16Start, utf16End - utf16Start));
            }
        }
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

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
}
