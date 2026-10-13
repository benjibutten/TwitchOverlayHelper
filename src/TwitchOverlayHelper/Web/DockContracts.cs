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

/// <summary>
/// One GIF and the stretch of the message text it stands in for. The address is Twitch's own and
/// travels unchanged – see <see cref="GifSpan"/>.
/// </summary>
internal sealed record DockGif(string Id, string Url, int Start, int Length);

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
    IReadOnlyList<DockGif> Gifs,
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
    // Which switch in the reading settings decides whether this card is drawn. Sent along so the
    // mapping exists only on this side.
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
/// The hype train strip, as the whole current picture: each frame replaces the last one, and a
/// dock that connects mid-train gets the same shape in its hello. The contributors in
/// <paramref name="Top"/> arrive already worded.
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
/// One nickname on the wire. Sent separately rather than on every message, so a name given now
/// also reaches the lines already on screen. A missing text means the nickname was taken away.
/// </summary>
internal sealed record DockNickname(string UserId, string Login, string? Text);

internal sealed record DockStatus(string Text, string State);

/// <summary>
/// Who the dock is writing as, and where. <paramref name="Room"/> is the joined channel's Twitch id,
/// empty until it is known. The dock's cached emote list belongs to one account in one channel,
/// so a change of either here invalidates it.
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
    IReadOnlyList<DockNickname> Nicknames,
    IReadOnlyList<Speech.TtsEntry> Tts);

/// <summary>
/// What a socket from the stream overlay is handed when it opens. A shape of its own rather than
/// <see cref="DockHello"/>: this page is on the broadcast, so it never receives the nicknames, who
/// is logged in or the dock's settings.
///
/// <para><paramref name="History"/> is only the tail of the timeline. <paramref name="Samples"/>
/// says the lines are the preview rather than chat, so the page does not drop them for being
/// old.</para>
/// </summary>
internal sealed record DockStreamHello(
    string Type, StreamSettings Stream, IReadOnlyList<DockHistoryItem> History, bool Samples);

/// <summary>
/// The preview lines. Their own frame rather than the messages they imitate, so a page does not
/// apply the rules for chat to them, such as dropping them for being old or fading them out.
/// </summary>
internal sealed record DockSamples(string Type, IReadOnlyList<DockHistoryItem> Items);

internal sealed record DockPet(string Id, string Name, string? Color, string Species, long SpawnedAt, long ExpiresAt);

internal sealed record DockPetSettings(
    bool Enabled,
    double Scale,
    int LifetimeMinutes,
    int MaxPets,
    bool ShowNames,
    bool RarityEffects,
    int RarityFadeSeconds,
    double AnimationSpeed);

/// <summary>
/// One species the overlay can render: an SVG body fetched from the pets folder, or a spritesheet
/// for pets in the hatch-pet format. SpriteVersion 2 is the extended sheet whose two extra rows
/// hold the sixteen look directions; null for SVG pets. Rarity is a
/// <see cref="TwitchOverlayHelper.Pets.PetRarity"/> tier, which the overlay uses for the
/// brightness of the creature's aura.
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
    string Rarity,
    int? SpriteVersion = null);

/// <summary>
/// The pet overlay's greeting: what it looks like, what it can draw, and what is already on the
/// lawn. Like the stream overlay's it leaves out the chat history and nicknames, because the page
/// is a browser source on the broadcast machine.
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

/// <summary>
/// One lucky spin for the pet overlay to act out. The winner is already drawn and booked: the
/// overlay slows down onto <paramref name="WinnerPetId"/> and reports <c>spinDone</c> with
/// <paramref name="Id"/> when it is over. <paramref name="PetIds"/> are the candidates to loop
/// through, in the order sent; their portraits come from the catalog the overlay already holds.
/// </summary>
internal sealed record DockSpin(
    string Id,
    IReadOnlyList<string> PetIds,
    string WinnerPetId,
    string WinnerName,
    string Side,
    int Seconds,
    string Title,
    bool Duplicate);

/// <summary>Whether the speaker button next to every name has anything to call.</summary>
internal sealed record DockSpeech(bool Enabled);

/// <summary>
/// One clip for the reading page to play. The address carries a token from
/// <see cref="TtsAudioStore"/>, never a path.
///
/// <para>Everything from <paramref name="Viewer"/> on is what the card draws, and is left out when
/// the card is switched off, so a viewer's name and words are not sent to the broadcast machine
/// without a use.</para>
/// </summary>
internal sealed record DockTtsPlay(
    string Id,
    string Url,
    double Volume,
    string? Viewer = null,
    string? Text = null,
    int? Cost = null,
    /// <summary>"powerUp" or "reward", so the card can word the price in bits or in points.</summary>
    string? Source = null);

/// <summary>
/// A card with nothing to play, for the preview button in the settings. Its own frame rather than a
/// clip with no address: the page reports back on every clip it is handed, and a preview must
/// never reach the code that decides refunds.
/// </summary>
internal sealed record DockTtsPreview(string Viewer, string Text, int Cost, string Source, int Milliseconds);

/// <summary>
/// What the reading page opens onto: only the card's appearance. The page has no other state to
/// restore.
/// </summary>
internal sealed record DockTtsHello(string Type, DockTtsWidget Widget);

/// <summary>
/// The card, as the page needs it. Its own shape rather than <see cref="TtsWidgetSettings"/> on the
/// wire, so the two enums travel as words rather than as numbers that could change between builds.
/// </summary>
internal sealed record DockTtsWidget(
    bool Enabled,
    string Position,
    double OffsetX,
    double OffsetY,
    double Width,
    double FontSize,
    string FontFamily,
    string AccentColor,
    double BackgroundOpacity,
    double CornerRadius,
    string Label,
    bool ShowName,
    bool ShowText,
    bool ShowCost,
    bool ShowWave,
    bool TextOutline,
    string Animation,
    int LingerMilliseconds)
{
    public static DockTtsWidget From(TtsSettings tts)
    {
        TtsWidgetSettings widget = tts.Widget;
        return new DockTtsWidget(
            // Also off when the readings play on the desktop only: no clip reaches this page then.
            tts.ShowsWidget,
            Word(widget.Position),
            widget.OffsetX,
            widget.OffsetY,
            widget.Width,
            widget.FontSize,
            widget.FontFamily,
            widget.AccentColor,
            widget.BackgroundOpacity,
            widget.CornerRadius,
            widget.Label,
            widget.ShowName,
            widget.ShowText,
            widget.ShowCost,
            widget.ShowWave,
            widget.TextOutline,
            Word(widget.Animation),
            widget.LingerMilliseconds);
    }

    /// <summary>The position as words the page can key its CSS off.</summary>
    private static string Word(TtsWidgetPosition position) => position switch
    {
        TtsWidgetPosition.TopLeft => "top-left",
        TtsWidgetPosition.TopCenter => "top-center",
        TtsWidgetPosition.TopRight => "top-right",
        TtsWidgetPosition.MiddleLeft => "middle-left",
        TtsWidgetPosition.MiddleCenter => "middle-center",
        TtsWidgetPosition.MiddleRight => "middle-right",
        TtsWidgetPosition.BottomLeft => "bottom-left",
        TtsWidgetPosition.BottomRight => "bottom-right",
        _ => "bottom-center"
    };

    private static string Word(TtsWidgetAnimation animation) => animation switch
    {
        TtsWidgetAnimation.None => "none",
        TtsWidgetAnimation.Fade => "fade",
        TtsWidgetAnimation.Pop => "pop",
        _ => "slide"
    };
}

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
            ToDock(message.Gifs),
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
            // Which span to enlarge is decided on this side, so the dock and the overlay agree.
            message.GigantifiedEmoteIndex >= 0 ? message.GigantifiedEmoteIndex : (int?)null,
            message.MessageEffectId,
            // Null rather than false, so ordinary lines do not carry the field at all.
            message.IsLocalEcho ? true : null);
    }

    /// <summary>
    /// What the reward marker on a message should say, or null when no reward was redeemed. Falls
    /// back to a bare "belöning" when the title is unknown: IRC only carries the reward's GUID,
    /// and in someone else's channel the rewards cannot be read.
    /// </summary>
    private static string? RewardLabel(ChatMessage message)
    {
        if (message.RewardId is not { Length: > 0 }) return null;
        if (message.RewardTitle is not { Length: > 0 } title) return "belöning";
        return message.RewardCost is > 0 ? $"{title} · {message.RewardCost}" : title;
    }

    /// <summary>
    /// Maps an event to its card. Events carry no badge images, which would compete with the
    /// headline.
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
    /// A hype train for the strip. A train that has ended sends no detail line and a goal of zero.
    /// </summary>
    public static DockHypeTrain ToDock(HypeTrainState train) => new(
        train.Id,
        train.HasEnded ? "ended" : "running",
        ChatEventText.DescribeHypeTrain(train),
        train.HasEnded ? null : ChatEventText.DescribeHypeProgress(train),
        train.Level,
        train.Progress,
        train.HasEnded ? 0 : train.Goal,
        // Three fit on one line in a narrow dock.
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
        // Only the overlay draws these; the dock has the strip instead.
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

    private static IReadOnlyList<DockGif> ToDock(IReadOnlyList<GifSpan> gifs)
    {
        var result = new List<DockGif>(gifs.Count);
        foreach (GifSpan gif in gifs)
            result.Add(new DockGif(gif.GifId, gif.Url, gif.Start, gif.Length));
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

/// <summary>
/// One look of a fighter: its code, what the streamer called it, and its strip. <c>Special</c> is
/// set only when the outfit has a special of its own; otherwise it fights with the fighter's.
/// </summary>
internal sealed record DockOutfit(int Code, string Name, string Sprite, DockSpecial? Special = null);

/// <summary>
/// One fighter as the wait screen knows it. <c>Outfit</c> is the look in use when this stands in a
/// corner; in the roster it is 1.
/// </summary>
internal sealed record DockFighter(string Id, string Name, string Sprite, double Scale, int Outfit,
    IReadOnlyList<DockOutfit> Outfits, DockSpecial? Special = null);

/// <summary>A fighter's special attack: what it is called, how it plays out, its poses and its prop.</summary>
internal sealed record DockSpecial(string Name, string Style, string? Sprite, string? Prop, string Color);

/// <summary>Where the fight happens. A null image is the transparent arena.</summary>
internal sealed record DockArena(string Id, string? Image, double Floor, double Left, double Right);

/// <summary>
/// The words the chat can use, so the screen can show them, say what they did, and count down
/// each viewer's cooldown the same way the app does. <c>SoloAssist</c> is what one viewer alone in a
/// corner is worth; the screen scales it down by how many are helping there.
/// </summary>
internal sealed record DockFightCommands(bool Enabled, string Cheer, string Heal, bool ShowHint,
    int Cooldown, string Pick1, string Pick2, double SoloAssist);

/// <summary>How a match is played: a character select first or not, and how many rounds win it.</summary>
internal sealed record DockFightMatch(bool CharacterSelect, int SelectSeconds, int WinsToWin, bool ShowSupporters);

/// <summary>
/// Everything the wait screen needs from the app: the streamer's two fighters (what a match starts
/// with, and what a corner nobody voted for falls back to), every fighter the chat can vote for, the
/// arena, the commands, the match rules and the two lines of the banner.
/// </summary>
internal sealed record DockFightSetup(DockFighter? P1, DockFighter? P2, IReadOnlyList<DockFighter> Roster,
    DockArena Arena, DockFightCommands Commands, DockFightMatch Match, string Headline, string Subline);

/// <summary>A viewer taking a side. <c>Kind</c> is "cheer" or "heal".</summary>
internal sealed record DockFightAssist(int Player, string Kind, string Viewer, string? Color);

/// <summary>
/// A vote in the character select. <c>Voter</c> is an opaque key per viewer, so a second vote
/// replaces the first instead of counting twice; it is not the viewer's Twitch id.
/// </summary>
internal sealed record DockFightPick(int Player, string Fighter, int Outfit, string Viewer, string Voter, string? Color);
