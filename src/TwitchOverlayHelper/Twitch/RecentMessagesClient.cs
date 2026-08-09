using System.Net.Http;
using System.Text.Json;
using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Twitch;

/// <summary>
/// Fetches the lines that were said before we got here. Twitch has no such thing – IRC sends no
/// history on join and Helix has no endpoint for chat – so this asks recent-messages.robotty.de,
/// the community service Chatterino uses for the same job.
///
/// <para>Worth knowing about the trade: no key, no login, no registration, just a GET. In return it
/// is one person's free service rather than Twitch's, it can be down, and broadcasters can have
/// their channel left out of it. Every failure here is silent by design – the chat that follows is
/// the point of the app, and the lines from before it are a courtesy.</para>
///
/// <para>The reason it fits so neatly: the service answers with raw IRC lines, tags and all, so
/// <see cref="IrcMessageParser"/> reads them with exactly the code that reads the live connection.
/// Nothing here knows what a chat message looks like.</para>
/// </summary>
public sealed class RecentMessagesClient(HttpClient httpClient)
{
    private const string BaseUrl = "https://recent-messages.robotty.de/api/v2/recent-messages/";

    /// <summary>
    /// The lines the service still has for this channel, oldest first.
    ///
    /// <para>Both "hide" flags are on: a message that was deleted, and a message from someone who
    /// was banned, must not come back to life on a restart. Moderation is a decision someone made
    /// about that chat, and quietly undoing it on screen would be the wrong kind of helpful.</para>
    /// </summary>
    public async Task<IReadOnlyList<ChatTimelineItem>> GetAsync(string channel, int limit, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channel)) return [];

        string url = $"{BaseUrl}{Uri.EscapeDataString(channel)}" +
            $"?limit={Math.Clamp(limit, 1, 800)}&hide_moderation_messages=true&hide_moderated_messages=true";

        using HttpResponseMessage response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return [];

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(body);
    }

    /// <summary>
    /// Turns the service's answer into timeline items. Anything that is not a chat line or a notice
    /// we draw a card for – and anything the parser does not recognise at all – is dropped without
    /// comment: the feed is someone else's format, and a line we cannot read is not an error.
    /// </summary>
    internal static IReadOnlyList<ChatTimelineItem> Parse(string body)
    {
        List<ChatTimelineItem> items = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
                messages.ValueKind != JsonValueKind.Array)
                return [];

            foreach (JsonElement element in messages.EnumerateArray())
            {
                if (element.GetString() is not { Length: > 0 } line) continue;
                if (IrcMessageParser.TryParseChatMessage(line, out ChatMessage? message))
                    items.Add(ChatTimelineItem.Of(message!));
                else if (IrcMessageParser.TryParseUserNotice(line, out ChatEvent? chatEvent))
                    items.Add(ChatTimelineItem.Of(chatEvent!));
            }
        }
        catch (JsonException)
        {
            return [];
        }
        return items;
    }
}
