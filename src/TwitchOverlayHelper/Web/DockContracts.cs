using System.Text.Json;
using System.Text.Json.Serialization;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Nicknames;
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
    string? MessageEffect = null,
    /// <summary>Set only when the id is a local invention, so the dock hides pin and delete.</summary>
    bool? LocalEcho = null);

/// <summary>
/// A sub, raid or announcement as the dock sees it. The headline is worded once on this side so
/// both chat views say the same thing; the numbers ride along for the detail line.
/// </summary>
internal sealed record DockEvent(
    string Id,
    string Kind,
    // Which switch in the reading settings decides whether this card is drawn at all. Sent along
    // rather than worked out in the dock, so the mapping exists once and not once per language.
    string Group,
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

/// <summary>
/// The hype train strip. A train is a state that runs for minutes rather than a line in a log, so
/// it travels as the whole current picture: each frame replaces the last one, and a dock that
/// connects mid-train is handed the same shape in its hello. The contributors arrive already
/// worded, because turning a subscription's tier price into readable Swedish is not the dock's job.
/// </summary>
internal sealed record DockHypeTrain(
    string Id,
    string Phase,
    string Headline,
    string? Detail,
    int Level,
    int Progress,
    int Goal,
    IReadOnlyList<string> Top,
    long? ExpiresAt);

/// <summary>
/// One nickname on the wire. Sent as a small book of its own rather than baked into every message,
/// because a name given now has to reach the lines that are already on screen – including the
/// replayed history – and a lookup the dock holds is the only version of that which cannot drift.
/// A missing text means the nickname was taken away.
/// </summary>
internal sealed record DockNickname(string UserId, string Login, string? Text);

internal sealed record DockStatus(string Text, string State);

/// <summary>
/// Who the dock is writing as, and where. <paramref name="Room"/> is the joined channel's Twitch id
/// – empty until it is known – and rides along because it is the other half of what makes a cached
/// emote list valid: that list belongs to one account in one channel, and a switch of either has to
/// throw it away. It is published on every change to both, so the dock needs nothing else to notice.
/// </summary>
internal sealed record DockAuth(bool LoggedIn, string Login, bool CanSend, bool CanRaid, string Room, string? Error);

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
    IReadOnlyList<DockPet> Pets,
    DockHypeTrain? HypeTrain,
    IReadOnlyList<DockNickname> Nicknames);

/// <summary>
/// What a socket from the stream overlay is handed when it opens. Deliberately not
/// <see cref="DockHello"/> with a couple of fields blanked: this page is on the broadcast, and the
/// safest version of "the viewers must never see the nicknames" is that the page they are rendered
/// on never receives them. The same goes for who is logged in and what the dock looks like – none of
/// it has a use here, and all of it is the streamer's own business.
///
/// <para>The history is cut to what the overlay could show anyway. A page that draws a dozen lines
/// has no use for two hundred, and this is the frame that would otherwise be the largest thing the
/// server ever sends.</para>
///
/// <para><paramref name="Samples"/> says the lines are the preview rather than chat, which is the
/// difference between "put these back if they are still recent" and "draw these and leave them
/// there".</para>
/// </summary>
internal sealed record DockStreamHello(
    string Type, StreamSettings Stream, IReadOnlyList<DockHistoryItem> History, bool Samples);

/// <summary>
/// The preview lines, as a preview. Its own frame rather than the messages they imitate: a page has
/// to be able to tell invented lines from chat, or it applies the rules for chat to them – dropping
/// them for being old, fading them out – and takes down the only thing on screen while nothing is
/// connected.
/// </summary>
internal sealed record DockSamples(string Type, IReadOnlyList<DockHistoryItem> Items);

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

/// <summary>
/// The pet overlay's greeting: what it looks like, what it can draw, and what is already on the
/// lawn. Cut to those three the same way the stream overlay's is – it is a browser source on the
/// broadcast machine, and the chat history and nicknames in the dock's hello are the streamer's own
/// business.
/// </summary>
internal sealed record DockPetsHello(
    string Type,
    DockPetSettings PetSettings,
    IReadOnlyList<DockPetDefinition> PetCatalog,
    IReadOnlyList<DockPet> Pets);

/// <summary>A spawn can bump the oldest pet out; the overlay despawns it in the same breath.</summary>
internal sealed record DockPetSpawn(DockPet Pet, string? RemovedId, bool Extended);

/// <summary>One pet sent home early, because the redemption that bought it was paid back.</summary>
internal sealed record DockPetRemoved(string Id);

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
            message.MessageEffectId,
            // Sent only when it is true: every ordinary line would otherwise carry a "no" that says
            // nothing, and there is one of these per message during a raid.
            message.IsLocalEcho ? true : null);
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
        ChatEventVisibility.Group(chatEvent.Type),
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

    /// <summary>
    /// A hype train for the strip. The bar is about the level being climbed right now, so a train
    /// that has ended sends no detail line and a goal of zero – there is nothing left to climb.
    /// </summary>
    public static DockHypeTrain ToDock(HypeTrainState train) => new(
        train.Id,
        train.HasEnded ? "ended" : "running",
        ChatEventText.DescribeHypeTrain(train),
        train.HasEnded ? null : ChatEventText.DescribeHypeProgress(train),
        train.Level,
        train.Progress,
        train.HasEnded ? 0 : train.Goal,
        // Three fit on one line in a narrow dock; a fourth would push the row into an ellipsis.
        train.TopContributions.Take(3).Select(ChatEventText.DescribeContribution).ToArray(),
        train.ExpiresAt?.ToUnixTimeMilliseconds());

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
        // Only the overlay draws these; the dock has the strip instead. Named all the same, so a
        // card that ever did reach the dock would not arrive labelled as something unknown.
        ChatEventType.HypeTrainBegin => "hypeTrainBegin",
        ChatEventType.HypeTrainEnd => "hypeTrainEnd",
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
