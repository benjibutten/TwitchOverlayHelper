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
    /// rather than instead of it: in a channel where we cannot read the rewards, the id is still
    /// the only thing a redemption carries.
    /// </summary>
    public string RewardName { get; set; } = string.Empty;

    public int Minutes { get; set; } = 5;

    /// <summary>
    /// This app created the reward on Twitch, which is the only thing that makes its redemptions
    /// answerable: Twitch lets an app fulfil or refund a redemption only on a reward its own client
    /// id made. A rule pointed at a reward set up by hand in the dashboard stays false forever, and
    /// keeps the old behaviour – the pet spawns and the points are simply spent.
    ///
    /// <para>Written by the app, never by hand. Setting it on a reward the app did not create only
    /// buys a 403 from Twitch at the moment a refund was supposed to happen.</para>
    /// </summary>
    public bool Managed { get; set; }

    /// <summary>
    /// The price the reward was created with, kept so the settings can show what a rule costs
    /// without a round trip. Twitch's own copy is the truth; this is a note.
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
}

/// <summary>
/// The channel point pets that walk around the OBS pet overlay. Redemptions only reach IRC when
/// the reward requires a viewer message, which is why the trigger is a reward id on a chat line.
/// </summary>
public sealed class PetSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Legacy single reward, from before rewards could differ in length. Migrated into
    /// <see cref="Rewards"/> on load and then left empty.
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

    /// <summary>When full, the oldest pet is sent home to make room for the newest redemption.</summary>
    public int MaxPets { get; set; } = 6;

    /// <summary>Lets the broadcaster and moderators spawn a pet with "!pet", to test without points.</summary>
    public bool AllowModTestCommand { get; set; } = true;

    /// <summary>
    /// Species used when the redemption text names no pet. Empty means a random one, which keeps
    /// the surprise for viewers who did not read the reward description.
    /// </summary>
    public string DefaultPet { get; set; } = string.Empty;

    /// <summary>
    /// The label under each pet, carrying the name of the viewer who redeemed it. Turning it off
    /// leaves only the creatures, for a cleaner overlay.
    /// </summary>
    public bool ShowNames { get; set; } = true;

    /// <summary>
    /// How many minutes a redemption of this reward is worth, or null when it should not spawn a
    /// pet at all. With no rules configured every redemption counts, at the default length.
    /// </summary>
    public int? LifetimeMinutesFor(string? rewardId, string? rewardTitle = null)
    {
        if (Rewards.Count == 0) return LifetimeMinutes;
        return RuleFor(rewardId, rewardTitle)?.Minutes;
    }

    /// <summary>
    /// Which rule a redemption falls under, or null when none claims it. Told apart from
    /// <see cref="LifetimeMinutesFor"/> because refunding needs the rule itself: only a rule holding
    /// a reward this app created may answer Twitch about the redemption.
    /// </summary>
    public PetRewardRule? RuleFor(string? rewardId, string? rewardTitle = null)
    {
        foreach (PetRewardRule rule in Rewards)
            if (rule.Matches(rewardId, rewardTitle))
                return rule;

        // A rule that names nothing is the catch-all, so "every other redemption" can still be
        // worth something without pasting every reward in the channel.
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
        MaxPets = Math.Clamp(MaxPets, 1, 20);

        // A hand-edited settings.json can hold "rewards": [null]; a missing rule is worth ignoring,
        // never worth refusing to start over.
        Rewards = Rewards?.Where(rule => rule is not null).ToList() ?? [];
        foreach (PetRewardRule rule in Rewards) rule.Normalize();

        // Settings written before rewards could differ in length carried a single id; it becomes
        // the first rule so an upgrade keeps spawning pets from the same reward.
        RewardId = RewardId?.Trim() ?? string.Empty;
        if (RewardId.Length > 0 && Rewards.Count == 0)
            Rewards.Add(new PetRewardRule { RewardId = RewardId, Minutes = LifetimeMinutes });
        RewardId = string.Empty;

        // Two rules for the same reward would make the second one dead weight, and two catch-alls
        // are the same trap; the first one written wins. The key joins id and name with a separator
        // neither can contain, so id "ab" with no name cannot collide with id "a" and name "b".
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Rewards = Rewards.Where(rule => seen.Add(RuleKey(rule))).ToList();
    }

    /// <summary>
    /// Identity of a rule, for dropping duplicates. The halves are joined by a unit separator –
    /// spelled as a character code rather than written into the string, because an invisible
    /// control character in source is a trap for whoever reads this next.
    /// </summary>
    private static string RuleKey(PetRewardRule rule) =>
        rule.RewardId + (char)0x1F + rule.RewardName;
}
