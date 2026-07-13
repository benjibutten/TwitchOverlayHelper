namespace TwitchOverlayHelper.Models;

public sealed record ChatBadge(string SetId, string Version);

/// <summary>UTF-16 index range in <see cref="ChatMessage.Text"/> that should render as a Twitch emote image.</summary>
public sealed record EmoteSpan(string EmoteId, int Start, int Length);

public sealed record ChatMessage(
    string Id,
    string DisplayName,
    string Text,
    string? NameColor,
    IReadOnlyList<ChatBadge> Badges,
    bool IsFirstMessage,
    bool IsHighlighted,
    DateTimeOffset SentAt,
    IReadOnlyList<EmoteSpan>? Emotes = null)
{
    public IReadOnlyList<EmoteSpan> Emotes { get; init; } = Emotes ?? Array.Empty<EmoteSpan>();
}
