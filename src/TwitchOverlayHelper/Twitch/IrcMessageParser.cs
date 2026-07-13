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
        string displayName = tags.GetValueOrDefault("display-name") ?? "Okänd";
        var badges = ParseBadges(tags.GetValueOrDefault("badges"));

        message = new ChatMessage(
            tags.GetValueOrDefault("id") ?? Guid.NewGuid().ToString("N"),
            displayName,
            text,
            EmptyToNull(tags.GetValueOrDefault("color")),
            badges,
            tags.GetValueOrDefault("first-msg") == "1",
            tags.GetValueOrDefault("msg-id") == "highlighted-message",
            ParseTimestamp(tags.GetValueOrDefault("tmi-sent-ts")));
        return true;
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
