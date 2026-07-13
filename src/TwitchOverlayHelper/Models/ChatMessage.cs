namespace TwitchOverlayHelper.Models;

public sealed record ChatBadge(string SetId, string Version);

public sealed record ChatMessage(
    string Id,
    string DisplayName,
    string Text,
    string? NameColor,
    IReadOnlyList<ChatBadge> Badges,
    bool IsFirstMessage,
    bool IsHighlighted,
    DateTimeOffset SentAt);
