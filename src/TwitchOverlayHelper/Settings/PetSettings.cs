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

    public int Minutes { get; set; } = 5;

    public void Normalize()
    {
        Label = Label?.Trim() ?? string.Empty;
        RewardId = RewardId?.Trim() ?? string.Empty;
        Minutes = Math.Clamp(Minutes, 1, 60);
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
    public int? LifetimeMinutesFor(string? rewardId)
    {
        if (Rewards.Count == 0) return LifetimeMinutes;

        foreach (PetRewardRule rule in Rewards)
            if (rule.RewardId.Length > 0 && string.Equals(rule.RewardId, rewardId, StringComparison.OrdinalIgnoreCase))
                return rule.Minutes;

        // A rule with no id is the catch-all, so "every other redemption" can still be worth
        // something without pasting every reward id in the channel.
        foreach (PetRewardRule rule in Rewards)
            if (rule.RewardId.Length == 0) return rule.Minutes;

        return null;
    }

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
        // are the same trap; the first one written wins.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Rewards = Rewards.Where(rule => seen.Add(rule.RewardId)).ToList();
    }
}
