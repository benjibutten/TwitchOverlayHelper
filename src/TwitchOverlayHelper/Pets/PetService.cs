using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Twitch;
using TwitchOverlayHelper.Web;

namespace TwitchOverlayHelper.Pets;

/// <summary>
/// What became of a redemption that asked for a pet. Only rewards this app created can act on the
/// unhappy answers – for every other reward the points are spent the moment Twitch takes them, and
/// the outcome is a note in the log rather than something anyone can put right.
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
    NoOverlay
}

/// <summary>
/// The answer to one redemption: what happened, and the pet it produced when something did.
/// <paramref name="Refundable"/> is carried along rather than worked out again later, because it
/// depends on the rule that matched – and that rule is only known here.
/// </summary>
public sealed record PetRedemptionResult(PetSpawnOutcome Outcome, PetState? Pet, bool Refundable)
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
    /// True once EventSub is delivering this channel's redemptions. The same redemption then also
    /// arrives over IRC whenever the reward asked the viewer to type something, and acting on both
    /// would spawn two pets for one purchase – so while this is on, IRC's copy is left alone.
    /// </summary>
    public bool RedemptionsFromEventSub { get; set; }

    /// <summary>
    /// Raised with the id of a pet that was pushed off a full lawn to make room for a new one. The
    /// redemption behind it, if there was one, has stopped being delivered and has to be paid back
    /// rather than left to be booked as a full life.
    /// </summary>
    public event Action<string>? PetEvicted;

    /// <summary>
    /// A redemption straight from EventSub. This is the only path that sees rewards with no text
    /// field, which never reach IRC at all and were invisible to the pets before.
    ///
    /// <para>Answers rather than returns quietly, because a reward this app created owes Twitch a
    /// verdict either way: a pet that was never spawned has to become a refund, and one that was
    /// has to stay in the queue until it has lived its time.</para>
    /// </summary>
    public PetRedemptionResult HandleRedemption(RewardRedemption redemption)
    {
        PetSettings pets = settings.Pets;
        PetRewardRule? rule = pets.RuleFor(redemption.RewardId, redemption.RewardTitle);

        // With no rules configured at all every redemption counts, at the default length – there is
        // no rule object to find, and nothing that could ever be refunded either.
        if (rule is null && pets.Rewards.Count > 0) return PetRedemptionResult.NotAPetReward;
        bool refundable = rule?.CanRefund == true;
        int minutes = rule?.Minutes ?? pets.LifetimeMinutes;

        if (!pets.Enabled) return new PetRedemptionResult(PetSpawnOutcome.Disabled, null, refundable);

        string id = redemption.UserId.Length > 0 ? redemption.UserId : redemption.UserLogin;
        if (id.Length == 0) return new PetRedemptionResult(PetSpawnOutcome.NotAPetReward, null, refundable);

        // A reward that can pay back should not spend the viewer's points on a lawn nobody is
        // watching. Everywhere else this is not knowable and not asked: an overlay that is up but
        // silent, or one connected from before this check existed, looks the same from here.
        if (refundable && hub.PetOverlayCount == 0) return new PetRedemptionResult(PetSpawnOutcome.NoOverlay, null, true);

        // What the viewer typed is what names a species; a silent reward simply gets the default.
        PetState? pet = Spawn(id, redemption.DisplayName, null, redemption.UserInput, minutes, evictWhenFull: !refundable);
        return pet is null
            ? new PetRedemptionResult(PetSpawnOutcome.Full, null, refundable)
            : new PetRedemptionResult(PetSpawnOutcome.Spawned, pet, refundable);
    }

    public void HandleMessage(ChatMessage message)
    {
        PetSettings pets = settings.Pets;
        if (!pets.Enabled) return;

        // Which reward was redeemed decides how long the pet stays, so a channel can sell five and
        // ten minutes as separate rewards.
        //
        // The reading reward is never a pet, on either route. EventSub claims it before the pets are
        // shown it at all, and the same has to be said here: this route only runs while EventSub is
        // down, and a channel with no pet rules configured spawns for every reward id it meets – the
        // reading's included, which would put a creature on the lawn for a purchase that was meant to
        // be read out loud.
        int? minutes = null;
        if (message.RewardId is { Length: > 0 }
            && !RedemptionsFromEventSub
            && !settings.Tts.MatchesReward(message.RewardId))
        {
            PetRewardRule? rule = pets.RuleFor(message.RewardId, message.RewardTitle);
            // A reward the app created is never spawned from here. IRC carries the reward id but
            // not the redemption's own id, so a pet handed out on this route could never be booked
            // as delivered – it would sit unanswered in the queue and be paid back by the next
            // sweep, giving the viewer both the pet and their points. Left alone, the redemption
            // stays in Twitch's queue where the streamer can see it, and the sweep settles it.
            //
            // Only reachable while EventSub is down in your own channel, which is the one case
            // where the app cannot vouch for anything it does with these rewards anyway.
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

        // "!pet boo" should not spawn whatever species happens to be called from the rest of the
        // sentence, so only the words after the command are searched.
        string wishText = minutes is null ? trimmed["!pet".Length..] : message.Text;
        Spawn(id, message.DisplayName, message.NameColor, wishText, minutes ?? pets.LifetimeMinutes);
    }

    /// <summary>Spawns a throwaway pet from the app's test button, cycling species so each click shows a new one.</summary>
    public void SpawnTest()
    {
        int number = Interlocked.Increment(ref _testCounter);
        IReadOnlyList<PetDefinition> species = catalog.Pets;
        PetDefinition chosen = species[(number - 1) % species.Count];
        // The label normally carries the name of the viewer who redeemed. On a test spawn it carries
        // the species instead, so the streamer can tell which pet they are looking at.
        Spawn($"test-{number}", $"{chosen.Name} (test)", null, chosen.Id, settings.Pets.LifetimeMinutes);
    }

    private PetState? Spawn(string id, string name, string? color, string? wishText, int minutes, bool evictWhenFull = true)
    {
        PetSettings pets = settings.Pets;
        PetDefinition species = catalog.Choose(wishText, pets.DefaultPet);
        PetSpawnResult? result = registry.Spawn(id, name, color, species.Id, TimeSpan.FromMinutes(minutes), pets.MaxPets, evictWhenFull);
        if (result is null) return null;
        hub.PublishPetSpawn(result);
        // After the frame, so the overlay is already taking the evicted pet down by the time
        // anything acts on it having gone.
        if (result.RemovedId is { Length: > 0 } removed) PetEvicted?.Invoke(removed);
        return result.Pet;
    }
}
