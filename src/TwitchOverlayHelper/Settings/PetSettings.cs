using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchOverlayHelper.Settings;

/// <summary>
/// One channel point reward that spawns a pet, and how long that reward buys. Separate rewards let
/// a channel sell "pet i 5 minuter" and "pet i 10 minuter" side by side.
/// </summary>
public sealed class PetRewardRule
{
    /// <summary>The streamer's own note about which reward this is; never shown on stream.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Twitch reward id. Empty means every redemption that no other rule claimed.</summary>
    public string RewardId { get; set; } = string.Empty;

    /// <summary>
    /// The reward's name on stream. Only EventSub knows it, so it is matched alongside the id
    /// rather than instead of it: where the rewards cannot be read, the id is all a redemption carries.
    /// </summary>
    public string RewardName { get; set; } = string.Empty;

    public int Minutes { get; set; } = 5;

    /// <summary>
    /// Whether this app created the reward on Twitch. Twitch lets an app fulfil or refund a
    /// redemption only on a reward its own client id made; for a reward set up by hand in the
    /// dashboard the pet spawns and the points are simply spent.
    ///
    /// <para>Written by the app, never by hand: set on a reward the app did not create, it turns a
    /// refund into a 403 from Twitch.</para>
    /// </summary>
    public bool Managed { get; set; }

    /// <summary>
    /// The price the reward was created with, so the settings can show it without a round trip.
    /// Twitch's own copy is the truth.
    /// </summary>
    public int Cost { get; set; } = 500;

    /// <summary>True when this rule claims every redemption no other rule named.</summary>
    public bool IsCatchAll => RewardId.Length == 0 && RewardName.Length == 0;

    /// <summary>
    /// Whether a redemption of this reward can be paid back. A catch-all never can, whatever the
    /// flag says: it claims redemptions of rewards this app never made.
    /// </summary>
    public bool CanRefund => Managed && RewardId.Length > 0;

    /// <summary>Shown in the settings list, so a row says at a glance whether it can pay back.</summary>
    public string StatusGlyph => CanRefund ? "🔒" : "—";

    public bool Matches(string? rewardId, string? rewardTitle) =>
        (RewardId.Length > 0 && string.Equals(RewardId, rewardId, StringComparison.OrdinalIgnoreCase))
        || (RewardName.Length > 0 && string.Equals(RewardName, rewardTitle, StringComparison.OrdinalIgnoreCase));

    public void Normalize()
    {
        Label = Label?.Trim() ?? string.Empty;
        RewardId = RewardId?.Trim() ?? string.Empty;
        RewardName = RewardName?.Trim() ?? string.Empty;
        Minutes = Math.Clamp(Minutes, 1, 60);
        Cost = Math.Clamp(Cost, 1, 10_000_000);
        // A hand-edited file could claim a reward is ours without an id to answer on. The flag is
        // dropped rather than trusted: it decides whether viewers get their points back.
        if (RewardId.Length == 0) Managed = false;
    }

    /// <inheritdoc cref="AppSettings.Unknown"/>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>
/// The channel point pets that walk around the OBS pet overlay. Redemptions only reach IRC when
/// the reward requires a viewer message, which is why the trigger is a reward id on a chat line.
/// </summary>
public sealed class PetSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The single reward id of older settings files. Migrated into <see cref="Rewards"/> on load
    /// and then left empty.
    /// </summary>
    public string RewardId { get; set; } = string.Empty;

    /// <summary>
    /// The rewards that spawn pets, each with its own time on screen. An empty list means every
    /// redemption counts, for <see cref="LifetimeMinutes"/>.
    /// </summary>
    public List<PetRewardRule> Rewards { get; set; } = [];

    /// <summary>Time on screen for redemptions no rule pins a length to, and for the !pet command.</summary>
    public int LifetimeMinutes { get; set; } = 5;

    /// <summary>Render scale in the overlay; 1.0 is roughly 90 px tall.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>
    /// How fast the creatures are animated, as a multiple of their natural pace. Scales every
    /// behaviour together; 1.0 is the balance the overlay was drawn for. Adjustable because the
    /// right speed depends on how many frames a pet's sheet has.
    /// </summary>
    public double AnimationSpeed { get; set; } = 1.0;

    /// <summary>When full, the oldest pet is sent home to make room for the newest redemption.</summary>
    public int MaxPets { get; set; } = 6;

    /// <summary>Lets the broadcaster and moderators spawn a pet with "!pet", to test without points.</summary>
    public bool AllowModTestCommand { get; set; } = true;

    /// <summary>Species used when the redemption text names no pet. Empty means a random one.</summary>
    public string DefaultPet { get; set; } = string.Empty;

    /// <summary>The label under each pet, carrying the name of the viewer who redeemed it.</summary>
    public bool ShowNames { get; set; } = true;

    /// <summary>
    /// The aura every pet wears in its rarity's colour – a breathing halo, and a sheen sweeping
    /// across the rarer ones.
    /// </summary>
    public bool RarityEffects { get; set; } = true;

    /// <summary>
    /// How long the aura burns before it fades away, so a lawn of lit pets does not compete with
    /// the game behind it. Zero keeps every pet lit for as long as it lives.
    /// </summary>
    public int RarityFadeSeconds { get; set; } = 10;

    /// <summary>
    /// How many minutes a redemption of this reward is worth, or null when it should not spawn a
    /// pet at all. With no rules configured every redemption counts, at the default length.
    /// </summary>
    public int? LifetimeMinutesFor(string? rewardId, string? rewardTitle = null)
    {
        if (Rewards.Count == 0) return LifetimeMinutes;
        return RuleFor(rewardId, rewardTitle)?.Minutes;
    }

    /// <summary>Which rule a redemption falls under, or null when none claims it.</summary>
    public PetRewardRule? RuleFor(string? rewardId, string? rewardTitle = null)
    {
        foreach (PetRewardRule rule in Rewards)
            if (rule.Matches(rewardId, rewardTitle))
                return rule;

        // A rule that names nothing is the catch-all for redemptions no other rule claimed.
        foreach (PetRewardRule rule in Rewards)
            if (rule.IsCatchAll) return rule;

        return null;
    }

    /// <summary>The rewards this app created, which are the ones whose queue it may clean up.</summary>
    public IReadOnlyList<string> ManagedRewardIds =>
        Rewards.Where(rule => rule.CanRefund).Select(rule => rule.RewardId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public void Normalize()
    {
        DefaultPet = DefaultPet?.Trim() ?? string.Empty;
        LifetimeMinutes = Math.Clamp(LifetimeMinutes, 1, 60);
        Scale = Math.Clamp(double.IsFinite(Scale) ? Scale : 1.0, 0.4, 2.5);
        // Outside this range the animations stop reading as movement: a slideshow or a blur.
        AnimationSpeed = Math.Clamp(double.IsFinite(AnimationSpeed) ? AnimationSpeed : 1.0, 0.5, 1.5);
        MaxPets = Math.Clamp(MaxPets, 1, 20);

        // A hand-edited settings.json can hold "rewards": [null]; such entries are ignored.
        Rewards = Rewards?.Where(rule => rule is not null).ToList() ?? [];
        foreach (PetRewardRule rule in Rewards) rule.Normalize();

        // Older settings files carry a single reward id; it becomes the first rule so the same
        // reward keeps spawning pets.
        RewardId = RewardId?.Trim() ?? string.Empty;
        if (RewardId.Length > 0 && Rewards.Count == 0)
            Rewards.Add(new PetRewardRule { RewardId = RewardId, Minutes = LifetimeMinutes });
        RewardId = string.Empty;

        // A second rule for the same reward, or a second catch-all, could never match; the first wins.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Rewards = Rewards.Where(rule => seen.Add(RuleKey(rule))).ToList();
    }

    /// <summary>
    /// Identity of a rule, for dropping duplicates. Id and name are joined by a unit separator,
    /// which neither can contain, so id "ab" with no name cannot collide with id "a" and name "b".
    /// It is spelled as a character code because an invisible control character in source is a trap.
    /// </summary>
    private static string RuleKey(PetRewardRule rule) =>
        rule.RewardId + (char)0x1F + rule.RewardName;

    /// <inheritdoc cref="AppSettings.Unknown"/>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
