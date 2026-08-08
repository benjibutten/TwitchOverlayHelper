namespace TwitchOverlayHelper.Models;

public sealed record ChatBadge(string SetId, string Version);

/// <summary>UTF-16 index range in <see cref="ChatMessage.Text"/> that should render as a Twitch emote image.</summary>
public sealed record EmoteSpan(string EmoteId, int Start, int Length);

/// <summary>
/// The message this one answers. Twitch carries it as reply-parent-* tags and additionally repeats
/// the parent's author as a leading "@name" inside the text; the parser strips that copy, so the
/// reply reads as its own sentence with the parent shown above it instead of as a bare mention.
/// </summary>
public sealed record ChatReply(
    string ParentMessageId,
    string ParentUserId,
    string ParentLogin,
    string ParentDisplayName,
    string ParentText);

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

    /// <summary>The message this one is a reply to, when it is one. Null for an ordinary line.</summary>
    public ChatReply? Reply { get; init; }

    /// <summary>
    /// Id of the channel point reward this message redeemed, when it did. Only redemptions that
    /// require the viewer to type a message ever reach IRC, so this is null for silent rewards.
    /// </summary>
    public string? RewardId { get; init; }

    /// <summary>
    /// Bits cheered with this message. Cheers are ordinary PRIVMSGs carrying a bits tag rather than
    /// a notice of their own, so they stay messages and only gain a marker.
    /// </summary>
    public int? Bits { get; init; }

    /// <summary>
    /// Name and cost of the redeemed reward, filled in from EventSub when we are allowed to see it.
    /// IRC only ever carries the reward's GUID, so these stay null in someone else's channel – the
    /// message still shows, it just says "belöning" rather than naming which one.
    /// </summary>
    public string? RewardTitle { get; init; }

    public int? RewardCost { get; init; }

    /// <summary>
    /// The Message Effect power-up on this line, as Twitch writes it in the animation-id tag –
    /// "simmer", "rainbow-eclipse", "cosmic-abyss". It rides along on the PRIVMSG itself, so unlike
    /// the other power-ups it needs no login and no EventSub.
    /// </summary>
    public string? MessageEffectId { get; init; }

    /// <summary>
    /// Set when a Gigantify an Emote power-up blew one emote up in this message. The value is the
    /// emote's id when EventSub named it, and an empty string when all we know is that something in
    /// the line was gigantified. IRC carries no sign of this power-up at all, so it is only ever
    /// filled in from channel.bits.use – your own channel, with bits:read.
    /// </summary>
    public string? GigantifiedEmoteId { get; init; }

    /// <summary>
    /// Which emote span is the enlarged one, or -1 when none is. Twitch does not mark it – that is
    /// the whole of open issue twitchdev/issues#1047 – and the convention every client renders by is
    /// that the gigantified emote is the *last* one in the message. This rests on that convention,
    /// not on a contract. When channel.bits.use did name the emote we can do better than guess and
    /// take the last span carrying that id, falling back to the convention if the message no longer
    /// contains it.
    /// </summary>
    public int GigantifiedEmoteIndex
    {
        get
        {
            if (GigantifiedEmoteId is null || Emotes.Count == 0) return -1;
            for (int i = Emotes.Count - 1; i >= 0; i--)
                if (string.Equals(Emotes[i].EmoteId, GigantifiedEmoteId, StringComparison.Ordinal)) return i;
            return Emotes.Count - 1;
        }
    }

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

/// <summary>
/// The kinds of USERNOTICE the chat views give a card of their own. Anything Twitch sends that is
/// not in this list becomes <see cref="Other"/> and is shown using the system-msg text, so a new
/// msg-id turns into a readable line instead of disappearing.
/// </summary>
public enum ChatEventType
{
    Other,
    /// <summary>A new sub or a resub (msg-id sub, resub).</summary>
    Subscription,
    /// <summary>One sub given to one named viewer (subgift).</summary>
    SubGift,
    /// <summary>A batch of subs given to the room at once (submysterygift).</summary>
    CommunityGift,
    /// <summary>A gifted or Prime sub continued as a paid one (giftpaidupgrade, primepaidupgrade).</summary>
    SubUpgrade,
    Raid,
    Unraid,
    Announcement,
    /// <summary>A viewer reached a new bits badge (bitsbadgetier).</summary>
    BitsBadge,
    /// <summary>A watch streak milestone (viewermilestone, category watch-streak).</summary>
    WatchStreak,
    /// <summary>A first-time chatter saying hello (ritual, new_chatter).</summary>
    NewChatter,
    /// <summary>A channel point reward was redeemed. EventSub only, and only in your own channel.</summary>
    RewardRedemption,
    /// <summary>This channel sent a shoutout to someone else.</summary>
    ShoutoutSent,
    /// <summary>Someone else shouted this channel out.</summary>
    ShoutoutReceived,
    /// <summary>
    /// A Celebration power-up: bits spent on an animation across the stream rather than on a
    /// message. It is the one power-up with nothing to attach itself to, which is why it gets a
    /// card of its own while the other two only mark a chat line.
    /// </summary>
    Celebration,
    /// <summary>
    /// A hype train started. The train itself is a state rather than an event – the dock draws it as
    /// a strip – but its start is a moment, and the overlay has nowhere to put a strip.
    /// </summary>
    HypeTrainBegin,
    /// <summary>A hype train ended, with the level it reached.</summary>
    HypeTrainEnd
}

/// <summary>
/// A non-message thing that happened in chat: a sub, a raid, an announcement. Built from IRC's
/// USERNOTICE and rendered as its own kind of card next to the chat lines.
/// </summary>
public sealed record ChatEvent(
    ChatEventType Type,
    string Id,
    string DisplayName,
    DateTimeOffset At)
{
    public string UserLogin { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string? NameColor { get; init; }
    public IReadOnlyList<ChatBadge> Badges { get; init; } = Array.Empty<ChatBadge>();

    /// <summary>
    /// Twitch's own English summary of the notice. Kept as the last resort for a msg-id we do not
    /// recognise, so an unknown event still says something instead of nothing.
    /// </summary>
    public string? SystemMessage { get; init; }

    /// <summary>The chatter's own words, when the notice carries any – a resub greeting, an announcement.</summary>
    public string? Message { get; init; }

    public IReadOnlyList<EmoteSpan> Emotes { get; init; } = Array.Empty<EmoteSpan>();

    /// <summary>Raw sub plan as Twitch writes it: "Prime", "1000", "2000" or "3000".</summary>
    public string? Tier { get; init; }

    /// <summary>Total months subscribed, which is the number a resub is actually about.</summary>
    public int? Months { get; init; }

    /// <summary>Consecutive months, only sent when the viewer chose to share the streak.</summary>
    public int? StreakMonths { get; init; }

    /// <summary>Number of subs in a community gift.</summary>
    public int? GiftCount { get; init; }

    public string? RecipientDisplayName { get; init; }

    /// <summary>Viewers brought along by a raid.</summary>
    public int? ViewerCount { get; init; }

    /// <summary>Bits behind a bits badge tier.</summary>
    public int? Bits { get; init; }

    /// <summary>Streams watched in a row, for a watch streak.</summary>
    public int? StreakValue { get; init; }

    /// <summary>Announcement colour Twitch picked: PRIMARY, BLUE, GREEN, ORANGE or PURPLE.</summary>
    public string? AnnouncementColor { get; init; }

    /// <summary>Twitch id of the redeemed reward. Matches the IRC custom-reward-id on the same redemption.</summary>
    public string? RewardId { get; init; }

    /// <summary>What the reward is called on stream – the part a GUID could never tell anyone.</summary>
    public string? RewardTitle { get; init; }

    /// <summary>Channel points the redemption cost.</summary>
    public int? RewardCost { get; init; }

    /// <summary>The level a hype train was on when it started or ended.</summary>
    public int? HypeLevel { get; init; }

    /// <summary>Points contributed to the hype train in total.</summary>
    public int? HypeTotal { get; init; }

    /// <summary>Twitch's own kind of train: "regular", "treasure" or "golden_kappa".</summary>
    public string? HypeKind { get; init; }

    /// <summary>Whether the train was being run together with other channels.</summary>
    public bool HypeIsShared { get; init; }

    /// <summary>
    /// Who carried the train. Only the first is put on the card – the overlay card is one line, and
    /// the full list belongs in the dock's strip where there is room for it.
    /// </summary>
    public IReadOnlyList<HypeTrainContribution> TopContributions { get; init; } = Array.Empty<HypeTrainContribution>();
}

/// <summary>
/// One line in reading order: either a chat message or a chat event. Both views keep their history
/// as a list of these so a sub notice cannot drift out of place when the dock reconnects.
/// </summary>
public readonly record struct ChatTimelineItem(ChatMessage? Message, ChatEvent? Event)
{
    public static ChatTimelineItem Of(ChatMessage message) => new(message, null);
    public static ChatTimelineItem Of(ChatEvent chatEvent) => new(null, chatEvent);
}
