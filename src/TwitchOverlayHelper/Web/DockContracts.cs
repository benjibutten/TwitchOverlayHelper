using System.Text.Json;
using System.Text.Json.Serialization;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Settings;

namespace TwitchOverlayHelper.Web;

/// <summary>Wire shapes for the dock. Kept flat and explicit so the browser code stays readable.</summary>
internal static class DockJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

internal sealed record DockBadge(string SetId, string Version, string? ImageUrl, string? Title);

internal sealed record DockEmote(string Id, int Start, int Length);

/// <summary>The answered message, shown as one quiet line above the reply. The id lets the dock
/// jump to it when it is still on screen.</summary>
internal sealed record DockReply(string MessageId, string Login, string DisplayName, string Text);

internal sealed record DockMessage(
    string Id,
    string DisplayName,
    string Login,
    string UserId,
    string Text,
    string? Color,
    IReadOnlyList<DockBadge> Badges,
    IReadOnlyList<DockEmote> Emotes,
    bool IsFirstMessage,
    bool IsHighlighted,
    bool IsAction,
    bool IsBroadcaster,
    bool IsModerator,
    long SentAt,
    int? Bits = null,
    string? RewardLabel = null,
    DockReply? Reply = null,
    int? GiantEmote = null,
    string? MessageEffect = null);

/// <summary>
/// A sub, raid or announcement as the dock sees it. The headline is worded once on this side so
/// both chat views say the same thing; the numbers ride along for the detail line.
/// </summary>
internal sealed record DockEvent(
    string Id,
    string Kind,
    string Headline,
    string DisplayName,
    string Login,
    string UserId,
    string? Color,
    IReadOnlyList<DockBadge> Badges,
    string? Message,
    IReadOnlyList<DockEmote> Emotes,
    long At,
    string? AnnouncementColor);

/// <summary>One replayed line, tagged so the dock can hand it to the same code as a live frame.</summary>
internal sealed record DockHistoryItem(string Type, DockMessage? Message, DockEvent? Event);

internal sealed record DockStatus(string Text, string State);

internal sealed record DockAuth(bool LoggedIn, string Login, bool CanSend, bool CanRaid, string? Error);

internal sealed record DockHello(
    string Type,
    DockSettings Settings,
    DockStatus Status,
    DockAuth Auth,
    string Channel,
    string MentionName,
    bool SpeechEnabled,
    IReadOnlyList<DockHistoryItem> History,
    DockPetSettings PetSettings,
    IReadOnlyList<DockPetDefinition> PetCatalog,
    IReadOnlyList<DockPet> Pets);

internal sealed record DockPet(string Id, string Name, string? Color, string Species, long SpawnedAt, long ExpiresAt);

internal sealed record DockPetSettings(bool Enabled, double Scale, int LifetimeMinutes, int MaxPets, bool ShowNames);

/// <summary>
/// One species the overlay can render: an SVG body fetched from the pets folder, or a spritesheet
/// for pets in the hatch-pet format. SpriteVersion 2 is the extended sheet whose two extra rows
/// hold the sixteen look directions; null for SVG pets.
/// </summary>
internal sealed record DockPetDefinition(
    string Id,
    string Name,
    string Description,
    string Kind,
    string? BodyUrl,
    string? SpriteUrl,
    double Fps,
    IReadOnlyList<string> Emoji,
    int? SpriteVersion = null);

/// <summary>A spawn can bump the oldest pet out; the overlay despawns it in the same breath.</summary>
internal sealed record DockPetSpawn(DockPet Pet, string? RemovedId, bool Extended);

/// <summary>Whether the speaker button next to every name has anything to call.</summary>
internal sealed record DockSpeech(bool Enabled);

internal sealed record DockEnvelope<T>(string Type, T Payload);

internal sealed record DockModerationPayload(string Kind, string? MessageId, string? UserId, string? Login, int? DurationSeconds);

internal static class DockMapper
{
    public static DockMessage ToDock(ChatMessage message, Func<ChatBadge, (string? Url, string? Title)> badgeLookup)
    {
        var badges = new List<DockBadge>(message.Badges.Count);
        foreach (ChatBadge badge in message.Badges)
        {
            (string? url, string? title) = badgeLookup(badge);
            badges.Add(new DockBadge(badge.SetId, badge.Version, url, title));
        }

        return new DockMessage(
            message.Id,
            message.DisplayName,
            message.UserLogin,
            message.UserId,
            message.Text,
            message.NameColor,
            badges,
            ToDock(message.Emotes),
            message.IsFirstMessage,
            message.IsHighlighted,
            message.IsAction,
            message.IsBroadcaster,
            message.IsModerator,
            message.SentAt.ToUnixTimeMilliseconds(),
            message.Bits,
            RewardLabel(message),
            message.Reply is { } reply
                ? new DockReply(reply.ParentMessageId, reply.ParentLogin, reply.ParentDisplayName, reply.ParentText)
                : null,
            // Which span to blow up is decided on this side, so the dock and the overlay enlarge the
            // same emote rather than each applying the convention on its own.
            message.GigantifiedEmoteIndex >= 0 ? message.GigantifiedEmoteIndex : (int?)null,
            message.MessageEffectId);
    }

    /// <summary>
    /// What the reward marker on a message should say. Falls all the way back to a bare "belöning":
    /// IRC hands out the reward's GUID and nothing else, so in a channel whose rewards we may not
    /// read, saying that a reward was redeemed is the most that can honestly be shown.
    /// </summary>
    private static string? RewardLabel(ChatMessage message)
    {
        if (message.RewardId is not { Length: > 0 }) return null;
        if (message.RewardTitle is not { Length: > 0 } title) return "belöning";
        return message.RewardCost is > 0 ? $"{title} · {message.RewardCost}" : title;
    }

    /// <summary>
    /// Events carry no badge images: the card is about what happened, and a row of badges next to
    /// a "gave 20 subs" line competes with the one thing worth reading.
    /// </summary>
    public static DockEvent ToDock(ChatEvent chatEvent) => new(
        chatEvent.Id,
        Kind(chatEvent.Type),
        ChatEventText.Describe(chatEvent),
        chatEvent.DisplayName,
        chatEvent.UserLogin,
        chatEvent.UserId,
        chatEvent.NameColor,
        Array.Empty<DockBadge>(),
        chatEvent.Message,
        ToDock(chatEvent.Emotes),
        chatEvent.At.ToUnixTimeMilliseconds(),
        chatEvent.AnnouncementColor);

    /// <summary>Kept as camelCase strings rather than numbers so the dock's CSS can key off them.</summary>
    private static string Kind(ChatEventType type) => type switch
    {
        ChatEventType.Subscription => "subscription",
        ChatEventType.SubGift => "subGift",
        ChatEventType.CommunityGift => "communityGift",
        ChatEventType.SubUpgrade => "subUpgrade",
        ChatEventType.Raid => "raid",
        ChatEventType.Unraid => "unraid",
        ChatEventType.Announcement => "announcement",
        ChatEventType.BitsBadge => "bitsBadge",
        ChatEventType.WatchStreak => "watchStreak",
        ChatEventType.NewChatter => "newChatter",
        ChatEventType.RewardRedemption => "reward",
        ChatEventType.ShoutoutSent => "shoutoutSent",
        ChatEventType.ShoutoutReceived => "shoutoutReceived",
        ChatEventType.Celebration => "celebration",
        _ => "other"
    };

    private static IReadOnlyList<DockEmote> ToDock(IReadOnlyList<EmoteSpan> emotes)
    {
        var result = new List<DockEmote>(emotes.Count);
        foreach (EmoteSpan emote in emotes)
            result.Add(new DockEmote(emote.EmoteId, emote.Start, emote.Length));
        return result;
    }

    public static DockModerationPayload ToDock(ChatModerationEvent moderation) => new(
        moderation.Kind switch
        {
            ChatEventKind.MessageDeleted => "messageDeleted",
            ChatEventKind.UserPurged => "userPurged",
            _ => "chatCleared"
        },
        moderation.TargetMessageId,
        moderation.TargetUserId,
        moderation.TargetLogin,
        moderation.DurationSeconds);
}
