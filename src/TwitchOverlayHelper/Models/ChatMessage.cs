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

    /// <summary>Twitch numeric user id. Required for Helix moderation calls; empty for locally generated messages.</summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>Lowercase login name, which is what chat commands and profile URLs use.</summary>
    public string UserLogin { get; init; } = string.Empty;

    /// <summary>True for a /me action message, which Twitch wraps in ACTION control characters.</summary>
    public bool IsAction { get; init; }

    public bool IsBroadcaster => HasBadge("broadcaster");
    public bool IsModerator => HasBadge("moderator");

    private bool HasBadge(string setId)
    {
        for (int i = 0; i < Badges.Count; i++)
            if (string.Equals(Badges[i].SetId, setId, StringComparison.Ordinal)) return true;
        return false;
    }
}

public enum ChatEventKind
{
    /// <summary>A single message was deleted (CLEARMSG).</summary>
    MessageDeleted,
    /// <summary>One user was timed out or banned (CLEARCHAT with a target).</summary>
    UserPurged,
    /// <summary>The whole chat was cleared (CLEARCHAT without a target).</summary>
    ChatCleared
}

/// <summary>Moderation feedback from IRC, so the reader sees that an action landed.</summary>
public sealed record ChatModerationEvent(
    ChatEventKind Kind,
    string? TargetMessageId,
    string? TargetUserId,
    string? TargetLogin,
    int? DurationSeconds,
    DateTimeOffset At);
