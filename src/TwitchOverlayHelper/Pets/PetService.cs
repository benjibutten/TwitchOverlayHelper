using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Twitch;
using TwitchOverlayHelper.Web;

namespace TwitchOverlayHelper.Pets;

/// <summary>
/// What became of a redemption that asked for a pet. Only rewards this app created can be refunded
/// on the unhappy answers; for every other reward the points are spent whatever the outcome.
/// </summary>
public enum PetSpawnOutcome
{
    /// <summary>No rule claimed this reward; it is not a pet reward at all.</summary>
    NotAPetReward,
    /// <summary>The pet is on the lawn.</summary>
    Spawned,
    /// <summary>Pets are switched off in the app.</summary>
    Disabled,
    /// <summary>The lawn is full, and this reward would rather pay back than evict someone else's pet.</summary>
    Full,
    /// <summary>No pet overlay is connected, so nothing would have been drawn for anybody.</summary>
    NoOverlay,
    /// <summary>The viewer asked for a win-only pet they never won, so the points go back instead.</summary>
    NotOwned
}

/// <summary>
/// The answer to one redemption: what happened, and the pet it produced when something did.
/// <paramref name="Refundable"/> comes from the reward rule that matched.
/// <paramref name="Asked"/> is the species the text named, set only for
/// <see cref="PetSpawnOutcome.NotOwned"/>.
/// </summary>
public sealed record PetRedemptionResult(PetSpawnOutcome Outcome, PetState? Pet, bool Refundable, PetDefinition? Asked = null)
{
    public static readonly PetRedemptionResult NotAPetReward = new(PetSpawnOutcome.NotAPetReward, null, false);
}

/// <summary>
/// Decides when a chat line earns a pet in the overlay, and which species it becomes. Redemptions
/// are the real trigger; the "!pet" command exists so the streamer can rehearse without spending
/// anyone's channel points.
/// </summary>
public sealed class PetService(AppSettings settings, PetCatalog catalog, PetRegistry registry, ChatHub hub)
{
    private int _testCounter;

    /// <summary>
    /// True once EventSub is delivering this channel's redemptions. A redemption with a text field
    /// also arrives over IRC, so while this is on <see cref="HandleMessage"/> ignores IRC's copy.
    /// </summary>
    public bool RedemptionsFromEventSub { get; set; }

    /// <summary>
    /// Whether the viewer (user id) has won the pet (pet id) in the lucky spin, which is what makes
    /// a win-only pet theirs to ask for. Defaults to nobody owning anything.
    /// </summary>
    public Func<string, string, bool> OwnsWonPet { get; set; } = (_, _) => false;

    /// <summary>
    /// Raised with the id of a pet that was pushed off a full lawn to make room for a new one, so a
    /// redemption behind it can be paid back.
    /// </summary>
    public event Action<string>? PetEvicted;

    /// <summary>
    /// A redemption straight from EventSub, the only route that sees rewards with no text field.
    /// The result tells the caller which verdict a reward this app created owes Twitch.
    /// </summary>
    public PetRedemptionResult HandleRedemption(RewardRedemption redemption)
    {
        PetSettings pets = settings.Pets;
        PetRewardRule? rule = pets.RuleFor(redemption.RewardId, redemption.RewardTitle);

        // With no rules configured every redemption counts, at the default length and never
        // refundable.
        if (rule is null && pets.Rewards.Count > 0) return PetRedemptionResult.NotAPetReward;
        bool refundable = rule?.CanRefund == true;
        int minutes = rule?.Minutes ?? pets.LifetimeMinutes;

        if (!pets.Enabled) return new PetRedemptionResult(PetSpawnOutcome.Disabled, null, refundable);

        string id = redemption.UserId.Length > 0 ? redemption.UserId : redemption.UserLogin;
        if (id.Length == 0) return new PetRedemptionResult(PetSpawnOutcome.NotAPetReward, null, refundable);

        // A win-only pet named by somebody who has not won it is paid back where that is possible;
        // on a reward that cannot refund, the spawn goes ahead with another species. Checked before
        // the overlay, because this answer stays true after the overlay comes back.
        PetDefinition? asked = catalog.ResolveFromText(redemption.UserInput);
        if (refundable && asked is { WinOnly: true } && !OwnsWonPet(id, asked.Id))
            return new PetRedemptionResult(PetSpawnOutcome.NotOwned, null, true, asked);

        // A refundable reward does not spend the viewer's points on a lawn no overlay is showing.
        if (refundable && hub.PetOverlayCount == 0) return new PetRedemptionResult(PetSpawnOutcome.NoOverlay, null, true);

        // A refundable reward pays back on a full lawn rather than evicting someone else's pet.
        PetState? pet = Spawn(id, redemption.DisplayName, null, redemption.UserInput, minutes, evictWhenFull: !refundable);
        return pet is null
            ? new PetRedemptionResult(PetSpawnOutcome.Full, null, refundable)
            : new PetRedemptionResult(PetSpawnOutcome.Spawned, pet, refundable);
    }

    public void HandleMessage(ChatMessage message)
    {
        PetSettings pets = settings.Pets;
        if (!pets.Enabled) return;

        // The matching reward rule decides how long the pet stays. The TTS and spin rewards are
        // excluded explicitly: a channel with no pet rules spawns for every reward id it meets.
        int? minutes = null;
        if (message.RewardId is { Length: > 0 }
            && !RedemptionsFromEventSub
            && !settings.Tts.MatchesReward(message.RewardId)
            && !settings.Spin.MatchesReward(message.RewardId, message.RewardTitle))
        {
            PetRewardRule? rule = pets.RuleFor(message.RewardId, message.RewardTitle);
            // A reward the app created is never spawned from IRC, which carries the reward id but
            // not the redemption id: the pet could not be booked as delivered, and the next sweep
            // would refund it, giving the viewer both the pet and the points.
            if (rule?.CanRefund != true)
                minutes = pets.Rewards.Count == 0 ? pets.LifetimeMinutes : rule?.Minutes;
        }

        string trimmed = message.Text.Trim();
        bool testCommand = pets.AllowModTestCommand
            && (message.IsBroadcaster || message.IsModerator)
            && (string.Equals(trimmed, "!pet", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("!pet ", StringComparison.OrdinalIgnoreCase));
        if (minutes is null && !testCommand) return;

        // The user id keys the pet so re-redeeming extends rather than duplicates; the login is
        // the fallback for locally generated messages that never saw Twitch.
        string id = message.UserId.Length > 0 ? message.UserId : message.UserLogin;
        if (id.Length == 0) return;

        // For the test command only the words after "!pet" name a species, and any species may be
        // rehearsed, win-only included.
        string wishText = minutes is null ? trimmed["!pet".Length..] : message.Text;
        Spawn(id, message.DisplayName, message.NameColor, wishText, minutes ?? pets.LifetimeMinutes,
            mayUse: testCommand && minutes is null ? _ => true : null);
    }

    /// <summary>Spawns a throwaway pet from the app's test button, cycling species so each click shows a new one.</summary>
    public void SpawnTest()
    {
        int number = Interlocked.Increment(ref _testCounter);
        IReadOnlyList<PetDefinition> species = catalog.Pets;
        PetDefinition chosen = species[(number - 1) % species.Count];
        // A test pet is labelled with its species rather than a viewer's name.
        Spawn($"test-{number}", $"{chosen.Name} (test)", null, chosen.Id, settings.Pets.LifetimeMinutes, mayUse: _ => true);
    }

    /// <summary>
    /// Puts one known species on the lawn without checking ownership, evicting the oldest pet when
    /// the lawn is full. Does nothing when pets are switched off or the species is unknown.
    /// </summary>
    public void SpawnDirect(string id, string name, string petId)
    {
        PetSettings pets = settings.Pets;
        if (!pets.Enabled || catalog.Find(petId) is null) return;
        PetSpawnResult? result = registry.Spawn(id, name, null, petId, TimeSpan.FromMinutes(pets.LifetimeMinutes), pets.MaxPets, evictWhenFull: true);
        if (result is null) return;
        hub.PublishPetSpawn(result);
        if (result.RemovedId is { Length: > 0 } removed) PetEvicted?.Invoke(removed);
    }

    private PetState? Spawn(string id, string name, string? color, string? wishText, int minutes, bool evictWhenFull = true,
        Func<PetDefinition, bool>? mayUse = null)
    {
        PetSettings pets = settings.Pets;
        // By default a win-only pet is available only to the viewer who won it.
        mayUse ??= pet => !pet.WinOnly || OwnsWonPet(id, pet.Id);
        PetDefinition species = catalog.Choose(wishText, pets.DefaultPet, mayUse);
        PetSpawnResult? result = registry.Spawn(id, name, color, species.Id, TimeSpan.FromMinutes(minutes), pets.MaxPets, evictWhenFull);
        if (result is null) return null;
        hub.PublishPetSpawn(result);
        // Raised after the spawn frame is published, so the overlay is already taking the evicted
        // pet down.
        if (result.RemovedId is { Length: > 0 } removed) PetEvicted?.Invoke(removed);
        return result.Pet;
    }
}
