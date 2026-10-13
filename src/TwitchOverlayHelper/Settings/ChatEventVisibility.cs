using System.Text.Json;
using System.Text.Json.Serialization;
using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Settings;

/// <summary>
/// Which kinds of event card a chat view draws; the dock and the overlay each own one. Grouped
/// rather than one switch per <see cref="ChatEventType"/>: the groups are the distinctions people
/// actually make.
///
/// <para>Only the cards are affected. A redemption still reaches the pets, and a cheer is still a
/// marker on the message that carried it.</para>
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

    /// <summary>Whether a group is shown, by the name the dock sees on the wire.</summary>
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
    /// The group a type belongs to. The single source for both views: the dock is handed this name
    /// on every event rather than keeping its own copy of the mapping.
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

    /// <inheritdoc cref="AppSettings.Unknown"/>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>
    /// Compares the switches, and only the switches.
    ///
    /// <para>Hand-written because record equality would compare <see cref="Unknown"/> by reference,
    /// and the overlay rebuilds every card on screen when its copy of these settings stops matching.</para>
    /// </summary>
    public bool Equals(ChatEventVisibility? other) =>
        other is not null
        && Subs == other.Subs
        && Raids == other.Raids
        && Announcements == other.Announcements
        && Bits == other.Bits
        && Milestones == other.Milestones
        && Rewards == other.Rewards
        && Shoutouts == other.Shoutouts
        && HypeTrain == other.HypeTrain
        && Other == other.Other;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Subs);
        hash.Add(Raids);
        hash.Add(Announcements);
        hash.Add(Bits);
        hash.Add(Milestones);
        hash.Add(Rewards);
        hash.Add(Shoutouts);
        hash.Add(HypeTrain);
        hash.Add(Other);
        return hash.ToHashCode();
    }
}
