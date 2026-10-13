using System.Net.Http;
using System.Text.Json;
using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Twitch;

/// <summary>
/// Fetches the lines that were said before we got here. Twitch has no such thing – IRC sends no
/// history on join and Helix has no endpoint for chat – so this asks recent-messages.robotty.de,
/// the community service Chatterino uses for the same job.
///
/// <para>It needs no key or login, but it is a free third-party service: it can be down, and
/// broadcasters can opt their channel out. Failures are therefore silent and yield no history.</para>
///
/// <para>The service answers with raw IRC lines, tags and all, which
/// <see cref="IrcMessageParser"/> reads like lines from the live connection.</para>
/// </summary>
public sealed class RecentMessagesClient(HttpClient httpClient)
{
    private const string BaseUrl = "https://recent-messages.robotty.de/api/v2/recent-messages/";

    /// <summary>
    /// The lines the service still has for this channel, oldest first, without deleted messages
    /// and messages from banned users. Empty when the service does not answer successfully.
    /// </summary>
    public async Task<IReadOnlyList<ChatTimelineItem>> GetAsync(string channel, int limit, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channel)) return [];

        // Both hide flags are on so a moderated message does not come back to life on a restart.
        string url = $"{BaseUrl}{Uri.EscapeDataString(channel)}" +
            $"?limit={Math.Clamp(limit, 1, 800)}&hide_moderation_messages=true&hide_moderated_messages=true";

        using HttpResponseMessage response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return [];

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(body);
    }

    /// <summary>
    /// Turns the service's answer into timeline items. Lines that are neither a chat message nor a
    /// user notice are skipped, and a body that is not valid JSON yields an empty list.
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
