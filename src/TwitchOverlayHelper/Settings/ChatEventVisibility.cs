using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Settings;

/// <summary>
/// Which kinds of event card a chat view draws. Both views own one of these: the dock is read at
/// rest and can afford every card, while the overlay lies over a game and is usually where the
/// selection gets made.
///
/// Grouped rather than one switch per <see cref="ChatEventType"/>. Sixteen checkboxes would be a
/// wall to read through, and nobody wants gift subs without ordinary subs – the groups are the
/// distinctions people actually make. Everything is on by default, so turning this on for the first
/// time changes nothing until a box is unticked.
///
/// Only the cards are affected. A redemption still reaches the pets and a cheer is still a marker on
/// the message that carried it: those are not event cards and hiding one was never about them.
/// </summary>
public sealed record ChatEventVisibility
{
    /// <summary>Subs, resubs, gift subs and continued gift or Prime subs.</summary>
    public bool Subs { get; set; } = true;
    /// <summary>Raids in and out.</summary>
    public bool Raids { get; set; } = true;
    /// <summary>Announcements the streamer or a moderator posts with /announce.</summary>
    public bool Announcements { get; set; } = true;
    /// <summary>Bits badge tiers and the Celebration power-up.</summary>
    public bool Bits { get; set; } = true;
    /// <summary>Watch streaks and first-time chatters.</summary>
    public bool Milestones { get; set; } = true;
    /// <summary>Channel point redemptions without a text field, which have no message to ride on.</summary>
    public bool Rewards { get; set; } = true;
    public bool Shoutouts { get; set; } = true;
    /// <summary>The dock's hype train strip, and the start and end cards in the overlay.</summary>
    public bool HypeTrain { get; set; } = true;
    /// <summary>Notices Twitch sends that we have no card of our own for, shown as their system-msg.</summary>
    public bool Other { get; set; } = true;

    public bool Allows(ChatEventType type) => IsOn(Group(type));

    /// <summary>
    /// Whether a group is shown, by the same name the dock sees on the wire. Keeping the lookup on
    /// this side is what lets the dock filter without knowing a single msg-id.
    /// </summary>
    public bool IsOn(string group) => group switch
    {
        "subs" => Subs,
        "raids" => Raids,
        "announcements" => Announcements,
        "bits" => Bits,
        "milestones" => Milestones,
        "rewards" => Rewards,
        "shoutouts" => Shoutouts,
        "hypeTrain" => HypeTrain,
        _ => Other
    };

    /// <summary>
    /// The group a type belongs to. The single source for both views: the overlay asks
    /// <see cref="Allows"/>, and the dock is handed this name on every event so its filter is the
    /// same one rather than a second copy of the mapping written in JavaScript.
    /// </summary>
    public static string Group(ChatEventType type) => type switch
    {
        ChatEventType.Subscription or ChatEventType.SubGift
            or ChatEventType.CommunityGift or ChatEventType.SubUpgrade => "subs",
        ChatEventType.Raid or ChatEventType.Unraid => "raids",
        ChatEventType.Announcement => "announcements",
        ChatEventType.BitsBadge or ChatEventType.Celebration => "bits",
        ChatEventType.WatchStreak or ChatEventType.NewChatter => "milestones",
        ChatEventType.RewardRedemption => "rewards",
        ChatEventType.ShoutoutSent or ChatEventType.ShoutoutReceived => "shoutouts",
        ChatEventType.HypeTrainBegin or ChatEventType.HypeTrainEnd => "hypeTrain",
        _ => "other"
    };
}
