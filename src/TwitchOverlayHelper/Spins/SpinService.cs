using TwitchOverlayHelper.Diagnostics;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Pets;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Twitch;
using TwitchOverlayHelper.Web;

namespace TwitchOverlayHelper.Spins;

/// <summary>What became of a redemption that asked for a spin.</summary>
public enum SpinOutcome
{
    /// <summary>Not the spin reward at all.</summary>
    NotASpinReward,
    /// <summary>The prize is drawn and booked; the reel is queued on the overlay.</summary>
    Spinning,
    /// <summary>The spin is switched off in the app.</summary>
    Disabled,
    /// <summary>The pets are switched off, so nothing the spin wins could step onto a lawn.</summary>
    PetsOff,
    /// <summary>No pet overlay is connected, so the reel would have played to nobody.</summary>
    NoOverlay,
    /// <summary>No pet is marked winnable, so there is nothing the reel could land on.</summary>
    NothingToWin,
    /// <summary>The viewer already owns every winnable pet, and no bot is on to run the gift flow.</summary>
    AllOwned
}

/// <summary>
/// The answer to one spin redemption. <paramref name="Refundable"/> travels with it for the same
/// reason it does on the pets: whether the points can come back is decided here and nowhere else.
/// </summary>
public sealed record SpinRedemptionResult(SpinOutcome Outcome, bool Refundable)
{
    public static readonly SpinRedemptionResult NotASpinReward = new(SpinOutcome.NotASpinReward, false);
}

/// <summary>
/// A verdict the spin owes Twitch about one redemption. An event rather than a call into the ledger,
/// so the deciding stays testable without a network – the app wires it to
/// <c>RedemptionLedger.AnswerNow</c>, which retries until Twitch takes it.
/// </summary>
public sealed record SpinVerdict(string RedemptionId, string RewardId, string ViewerName, int Cost, bool Refund, string Reason);

/// <summary>
/// Lyckosnurren. Decides what a spin redemption wins, books the win, and stages the reel on the pet
/// overlay – strictly in that order, because the animation is theatre: the outcome must already be
/// on disk when the first frame plays, so a browser source dying mid-spin can cost the show but
/// never the prize.
///
/// <para><b>Duplicates become gifts.</b> The draw runs over every winnable pet, rarity-weighted, so
/// a full-pocketed viewer can absolutely win something they already own – and then the bot asks them
/// to name someone to give it to, or to release it onto the lawn themselves. A duplicate is a win
/// like any other, points and all: the redemption is answered as delivered the moment it is drawn,
/// and what it bought is one chance to place the pet. Nobody named before the timeout, or a name
/// that turns out to own it too, and that chance is spent – there is no path back to the points
/// from here. With no bot to run the
/// conversation the draw simply skips what they own instead, and only somebody who owns everything
/// is paid back outright.</para>
/// </summary>
/// <param name="overlayCount">
/// How many pet overlays are connected right now. Defaults to asking the hub; a parameter so the
/// tests can put an audience in the room, which the hub only does for a real WebSocket.
/// </param>
/// <param name="canGift">
/// Whether the bot can actually hold the gift conversation right now. Not the same question as
/// whether the bot is switched on: a mode that is not Off still says nothing at all while the
/// account is logged out, the chat connection is down, or the duplicate message itself is disabled –
/// and a gift nobody is told about is a prize the winner never learns they have to claim.
/// </param>
public sealed class SpinService(
    AppSettings settings,
    PetCatalog catalog,
    SpinWinStore store,
    PetService pets,
    ChatHub hub,
    Func<string, Task<TwitchUser?>>? resolveUser = null,
    Func<int>? overlayCount = null,
    Func<bool>? canGift = null) : IDisposable
{
    /// <summary>
    /// How many startups may try to deliver the same owed fulfilment before it is written off. A
    /// reward deleted in the dashboard can never be answered, and the debt must not shield its
    /// redemption from the sweep for ever.
    /// </summary>
    private const int MaxFulfilmentAttempts = 5;

    /// <summary>
    /// How much longer than its own duration a spin may hold the stage before the queue moves on.
    /// Generous, because it only matters when the overlay accepted the frame and never answered.
    /// </summary>
    private static readonly TimeSpan AckMargin = TimeSpan.FromSeconds(12);

    private readonly Lock _gate = new();
    private readonly Queue<QueuedSpin> _queue = new();
    private QueuedSpin? _current;
    private DateTimeOffset _currentDeadline;
    private Timer? _timer;
    private int _testCounter;
    private bool _disposed;

    /// <summary>A verdict is due on Twitch – a win delivered, a gift landed, a gift nobody claimed.</summary>
    public event Action<SpinVerdict>? Verdict;

    /// <summary>
    /// A line for the bot: the flow, its values, and whether the flow's cooldown should be ignored.
    /// True for the lines that answer one person about their own points or prize; false for the ones
    /// viewers can set off on purpose.
    /// </summary>
    public event Action<BotFlow, IReadOnlyDictionary<string, string>, bool>? Announced;

    /// <summary>Starts the once-a-second round. Separate from construction so tests can drive <see cref="Tick"/> themselves.</summary>
    public void Start() => _timer ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    /// <summary>How many spins are staged or waiting. For the tests and the log.</summary>
    public int QueueLength
    {
        get { lock (_gate) return _queue.Count + (_current is null ? 0 : 1); }
    }

    /// <summary>The spin holding the stage, for the tests to acknowledge as the overlay would.</summary>
    internal string? CurrentSpinId
    {
        get { lock (_gate) return _current?.SpinId; }
    }

    /// <summary>
    /// Whether this redemption is the spin's own business rather than the startup sweep's – a gift
    /// still waiting for its winner, or a win already handed out whose fulfilment Twitch has not
    /// taken yet. Both would be paid back by a sweep that could not tell, and the second of them
    /// would hand the viewer their points back on top of a pet they keep.
    /// </summary>
    public bool Holds(string redemptionId) => store.HoldsPending(redemptionId) || store.Owes(redemptionId);

    /// <summary>Twitch accepted a verdict; anything owed on that redemption is settled.</summary>
    public void Settled(string redemptionId) => store.Settled(redemptionId);

    /// <summary>
    /// Raises every fulfilment this channel still owes, so a win booked while the app was closing is
    /// answered once it is listening again. Called from the same place as the sweep, which is the
    /// first moment redemptions can be answered at all.
    /// </summary>
    public void ResumeOwed()
    {
        foreach (SpinOwedFulfilment debt in store.ResumeOwed(hub.BroadcasterId, MaxFulfilmentAttempts))
        {
            AppLog.Info($"Lyckosnurren: {debt.ViewerName}s vinst var bokförd men obesvarad – försöker igen.");
            Verdict?.Invoke(new SpinVerdict(debt.RedemptionId, debt.RewardId, debt.ViewerName, debt.Cost, Refund: false, debt.Reason));
        }
    }

    /// <summary>A spin redemption straight from EventSub. Everything is decided and durable before this returns.</summary>
    public SpinRedemptionResult HandleRedemption(RewardRedemption redemption)
    {
        SpinSettings spin = settings.Spin;
        if (!spin.MatchesReward(redemption.RewardId, redemption.RewardTitle)) return SpinRedemptionResult.NotASpinReward;
        bool refundable = spin.CanRefund;

        if (!spin.Enabled) return new SpinRedemptionResult(SpinOutcome.Disabled, refundable);
        // The pets being off is the spin being off in every way that matters: the reel plays onto a
        // hidden lawn and the winner's prize never walks on, because SpawnDirect refuses. Asked here
        // rather than left to the curtain call, which is far too late to hand the points back.
        if (!settings.Pets.Enabled) return new SpinRedemptionResult(SpinOutcome.PetsOff, refundable);

        string id = redemption.UserId.Length > 0 ? redemption.UserId : redemption.UserLogin;
        if (id.Length == 0) return SpinRedemptionResult.NotASpinReward;
        string name = redemption.DisplayName.Length > 0 ? redemption.DisplayName : redemption.UserLogin;

        // Same reasoning as the pets: a reward that can pay back should not spend points on a show
        // nobody is watching. Where refunding is impossible the spin goes ahead – the points are
        // spent either way, and the win at least is real.
        if (refundable && (overlayCount?.Invoke() ?? hub.PetOverlayCount) == 0)
            return new SpinRedemptionResult(SpinOutcome.NoOverlay, true);

        IReadOnlyList<PetDefinition> winnable = catalog.Winnable;
        if (winnable.Count == 0) return new SpinRedemptionResult(SpinOutcome.NothingToWin, refundable);

        // The gift flow is a conversation, and only the bot can hold one. Without it, duplicates
        // are dodged instead of gifted – and a completed collection is the one case with nothing
        // left to draw.
        bool giftable = canGift?.Invoke() ?? settings.Bot.Speaks(BotFlow.SpinDuplicate);
        IReadOnlyList<PetDefinition> pool = giftable
            ? winnable
            : winnable.Where(pet => !store.Owns(id, pet.Id)).ToArray();
        if (pool.Count == 0) return new SpinRedemptionResult(SpinOutcome.AllOwned, refundable);

        PetDefinition prize = Draw(pool, Random.Shared);
        bool duplicate = store.Owns(id, prize.Id);

        // The booking comes before the show, always. A crash from here on can eat the animation
        // and the announcement, never the prize or the points.
        if (duplicate)
        {
            // Answered as delivered right here, exactly as a new win is. Nothing that can happen to
            // the gift afterwards hands the points back, so leaving the redemption open while its
            // winner chooses would only be a promise this flow no longer makes.
            SpinOwedFulfilment? debt = Debt(
                refundable, redemption.Id, redemption.RewardId, name, redemption.RewardCost ?? 0, "dubbletten är bokförd");
            SpinPendingGift? displaced = store.AddPending(
                new SpinPendingGift(redemption.Id, id, name, prize.Id,
                    DateTimeOffset.UtcNow + TimeSpan.FromMinutes(spin.GiftTimeoutMinutes)),
                debt);
            Raise(debt);
            // The winner spun again while their last duplicate was still looking for a home. Its
            // redemption was answered when it was drawn, so nothing is owed on it – but the chance
            // it bought is over, and the winner is owed the word: they are about to be handed a
            // fresh prompt, and two prompts with one gift between them is the confusing version.
            if (displaced is not null)
            {
                AppLog.Info($"Lyckosnurren: {name}s öppna gåva av {PrizeName(displaced.PetId)} ersattes av en ny snurr.");
                Announced?.Invoke(BotFlow.SpinGiftExpired, Values(
                    ("viewer", displaced.WinnerName), ("prize", PrizeName(displaced.PetId))), true);
            }
        }
        else
        {
            // The prize and the debt for it land in one write. Two would leave a crash between them
            // looking like a win nobody owes anything for, and the next startup sweep would pay the
            // redemption back on top of a pet the viewer keeps.
            SpinOwedFulfilment? debt = Debt(refundable, redemption.Id, redemption.RewardId, name, redemption.RewardCost ?? 0, "vinsten är bokförd");
            store.Add(
                new SpinWin(id, redemption.UserLogin, name, prize.Id, DateTimeOffset.UtcNow, RedemptionId: Kept(redemption.Id)),
                debt);
            Raise(debt);
        }

        AppLog.Info($"Lyckosnurren: {name} {(duplicate ? "vann en dubblett av" : "vann")} {prize.Name}.");
        Enqueue(new QueuedSpin(
            Guid.NewGuid().ToString("n"), redemption.Id, id, name, prize.Id, PetRarity.Normalize(prize.Rarity), duplicate, Test: false));
        return new SpinRedemptionResult(SpinOutcome.Spinning, refundable);
    }

    /// <summary>
    /// The spin's own chat commands, tried before the bot's static ones. True means the line is
    /// answered – or deliberately not – and nothing else should touch it.
    /// </summary>
    public bool HandleChatMessage(ChatMessage message)
    {
        SpinSettings spin = settings.Spin;
        // Switched off, the words go back to being ordinary chat: a streamer who turned the spin
        // off and has their own "!mina" command should get their own answer, not silence from a
        // feature that is not running. Gifts left open time out and pay back on their own.
        if (!spin.Enabled) return false;

        string text = message.Text.Trim();
        if (text.Length < 2 || text[0] != '!') return false;

        string id = message.UserId.Length > 0 ? message.UserId : message.UserLogin;
        if (id.Length == 0) return false;

        if (Matches(text, spin.ListCommand))
        {
            store.Touch(id, message.UserLogin, message.DisplayName);
            IReadOnlyList<SpinWin> wins = store.WinsFor(id);
            if (wins.Count == 0)
            {
                Announced?.Invoke(BotFlow.SpinListEmpty, Values(("viewer", message.DisplayName)), false);
            }
            else
            {
                Announced?.Invoke(BotFlow.SpinList, Values(
                    ("viewer", message.DisplayName),
                    ("list", ListOf(wins)),
                    ("count", wins.Count.ToString())), false);
            }
            return true;
        }

        if (Matches(text, spin.PoolCommand))
        {
            IReadOnlyList<PetDefinition> pool = catalog.Winnable;
            if (pool.Count == 0)
                Announced?.Invoke(BotFlow.SpinPoolEmpty, Values(("viewer", message.DisplayName)), false);
            else
                Announced?.Invoke(BotFlow.SpinPool, Values(
                    ("viewer", message.DisplayName),
                    ("list", PoolOf(pool)),
                    ("count", pool.Count.ToString())), false);
            return true;
        }

        if (Matches(text, spin.GiveCommand))
        {
            SpinPendingGift? pending = store.PendingFor(id);
            // Nothing of ours to give away. Left unhandled rather than answered: the word may be a
            // command the streamer defined for something else entirely.
            if (pending is null) return false;

            store.Touch(id, message.UserLogin, message.DisplayName);
            string target = text[spin.GiveCommand.Length..].Trim().TrimStart('@');
            int space = target.IndexOf(' ');
            if (space >= 0) target = target[..space];

            if (target.Length == 0)
            {
                // A bare "!ge" is somebody who lost the instructions; hand them back.
                Announced?.Invoke(BotFlow.SpinDuplicate, DuplicateValues(message.DisplayName, PrizeName(pending.PetId), PrizeRarity(pending.PetId), spin), true);
                return true;
            }

            _ = GiveAsync(pending, message.DisplayName, target);
            return true;
        }

        if (Matches(text, spin.ReleaseCommand))
        {
            SpinPendingGift? pending = store.PendingFor(id);
            // Same as the give command: nothing open is somebody else's command. With the pets off,
            // or the species gone from the folder, there is nothing to release onto the lawn, so the
            // gift stays open for "!ge" instead.
            if (pending is null || !settings.Pets.Enabled || catalog.Find(pending.PetId) is null) return false;

            store.Touch(id, message.UserLogin, message.DisplayName);
            // Closing the gift is what decides who acted on it: a "!ge" still waiting on Twitch for
            // the recipient's name may have closed it a moment ago, and then it has nothing to release.
            if (!store.RemovePending(pending.RedemptionId)) return true;

            string prize = PrizeName(pending.PetId);
            pets.SpawnDirect(id, message.DisplayName, pending.PetId);
            AppLog.Info($"Lyckosnurren: {message.DisplayName} släppte ut sin dubblett av {prize} på skärmen.");
            Announced?.Invoke(BotFlow.SpinReleased, Values(("viewer", message.DisplayName), ("prize", prize)), true);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Twitch says a redemption changed without us asking – the streamer worked the queue in the
    /// dashboard. Both endings have to be heard: a refund made by hand takes the gift with it, and a
    /// purchase marked complete by hand is a verdict this app no longer owes and can no longer give.
    /// </summary>
    public void HandleExternalUpdate(string redemptionId, string status)
    {
        if (status.Equals("CANCELED", StringComparison.OrdinalIgnoreCase))
        {
            if (store.RemovePending(redemptionId))
                AppLog.Info("Lyckosnurren: en väntande gåva betalades tillbaka via Twitchs egen kö.");
            // The points are back, so the prize goes back too. The row in the store is what the
            // redemption actually bought – it is what lets the viewer redeem that pet from now on –
            // and left standing it would be a win kept on top of a refund.
            if (store.RevokeWin(redemptionId) is { } revoked)
                AppLog.Info($"Lyckosnurren: {revoked.DisplayName}s vinst {PrizeName(revoked.PetId)} togs tillbaka – inlösen betalades tillbaka i Twitchs egen kö.");
            // And the reel it bought goes with it: one still waiting is dropped, one already on
            // screen plays out but takes its prize and its announcement with it.
            Revoke(redemptionId);
            // A win whose fulfilment was still owed has just been paid back instead. Nothing is left
            // to deliver, and the debt must go or it would shield a closed redemption for ever.
            store.Settled(redemptionId);
            return;
        }

        if (!status.Equals("FULFILLED", StringComparison.OrdinalIgnoreCase)) return;

        // Our own verdict coming back, or the streamer closing the redemption by hand; either way
        // Twitch has its answer and nothing more is owed. A gift still open is left exactly as it is:
        // it was never holding the points, only the chance to place the pet.
        store.Settled(redemptionId);
    }

    /// <summary>A rehearsal spin from the app's test button: a real reel, nothing booked, nobody told.</summary>
    public void SpinTest()
    {
        // Before anything is marked winnable the whole catalog stands in, so the button always has
        // something to show while the streamer is still setting up.
        IReadOnlyList<PetDefinition> winnable = catalog.Winnable;
        IReadOnlyList<PetDefinition> pool = winnable.Count > 0 ? winnable : catalog.Pets;
        if (pool.Count == 0) return;

        int number = Interlocked.Increment(ref _testCounter);
        PetDefinition prize = Draw(pool, Random.Shared);
        Enqueue(new QueuedSpin(
            Guid.NewGuid().ToString("n"), string.Empty, $"spin-test-{number}", $"{prize.Name} (test)", prize.Id,
            PetRarity.Normalize(prize.Rarity), Duplicate: false, Test: true));
    }

    /// <summary>The overlay reports the reel has stopped, so the stage is free.</summary>
    public void OnSpinShown(string spinId)
    {
        QueuedSpin? done;
        lock (_gate)
        {
            if (_current is null || !string.Equals(_current.SpinId, spinId, StringComparison.Ordinal)) return;
            done = _current;
            _current = null;
        }
        Finish(done);
        StartNext();
    }

    /// <summary>
    /// Everything staged goes without playing – the app is closing, or the reels have nowhere to
    /// run. Nothing on disk is touched: the wins are the viewers', and a gift whose winner is still
    /// choosing must survive a restart, which is the whole reason it is written down.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _queue.Clear();
            _current = null;
        }
    }

    /// <summary>
    /// The app has left the channel these gifts were won in, so they go too – unanswered, the way
    /// the ledger and the readings let theirs go. A redemption can only be answered where it was
    /// made, and a gift carried across would be claimed or paid back in a room where its purchase
    /// never happened; left behind it stays in its own channel's queue, for the streamer or for the
    /// sweep that runs on the way back. The wins themselves stay: they are nobody's connection.
    /// </summary>
    public void LeaveChannel()
    {
        Reset();
        store.ClearPending();
    }

    /// <summary>
    /// One round: a spin the overlay never answered for, and gifts whose winners never chose.
    /// Internal so the tests can drive it a step at a time instead of waiting on the timer.
    /// </summary>
    internal void Tick()
    {
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;

            QueuedSpin? overdue = null;
            lock (_gate)
            {
                if (_current is not null && now >= _currentDeadline)
                {
                    overdue = _current;
                    _current = null;
                }
            }
            if (overdue is not null)
            {
                // The win is long since booked; only the announcement was waiting on the curtain
                // call, and the winner should not lose it to a browser source that froze.
                AppLog.Warn("Lyckosnurren: overlayen kvitterade aldrig snurren – vinsten gäller ändå.");
                Finish(overdue);
            }
            StartNext();

            // Nothing to answer and nothing to hand back – the redemption was settled when the
            // duplicate was drawn. All that is left is telling the winner their chance has run out,
            // which is the whole of what the timeout does now.
            foreach (SpinPendingGift gift in store.TakeExpired(now))
            {
                string expired = PrizeName(gift.PetId);
                AppLog.Info($"Lyckosnurren: {gift.WinnerName}s gåva av {expired} förföll.");
                Announced?.Invoke(BotFlow.SpinGiftExpired, Values(("viewer", gift.WinnerName), ("prize", expired)), true);
            }
        }
        catch (Exception ex)
        {
            // A timer callback that throws takes the process with it, and this one runs while the
            // streamer is live.
            AppLog.Error("Lyckosnurren: fel i rundan", ex);
        }
    }

    /// <summary>
    /// One rarity-weighted draw. Internal and fed its own randomness so a test can prove the
    /// weighting instead of trusting it.
    /// </summary>
    internal static PetDefinition Draw(IReadOnlyList<PetDefinition> pool, Random random)
    {
        int total = 0;
        foreach (PetDefinition pet in pool) total += PetRarity.Weight(pet.Rarity);
        int roll = random.Next(total);
        foreach (PetDefinition pet in pool)
        {
            roll -= PetRarity.Weight(pet.Rarity);
            if (roll < 0) return pet;
        }
        return pool[^1];
    }

    /// <summary>
    /// The fulfilment a booking is about to leave owed to Twitch, or null where there is nothing this
    /// app could ever answer. Handed to the store so it is written in the same breath as the prize,
    /// and only raised afterwards: a prize already handed out must never be left looking like a
    /// redemption nobody answered. The debt is cleared when Twitch takes the verdict; until then it
    /// survives a restart and keeps <see cref="Holds"/> true.
    /// </summary>
    private SpinOwedFulfilment? Debt(bool refundable, string redemptionId, string rewardId, string viewerName, int cost, string reason) =>
        refundable && redemptionId.Length > 0
            ? new SpinOwedFulfilment(redemptionId, rewardId, hub.BroadcasterId, viewerName, cost, reason)
            : null;

    /// <summary>Raises a debt already on disk. Nothing owed, nothing to say.</summary>
    private void Raise(SpinOwedFulfilment? debt)
    {
        if (debt is null) return;
        Verdict?.Invoke(new SpinVerdict(debt.RedemptionId, debt.RewardId, debt.ViewerName, debt.Cost, Refund: false, debt.Reason));
    }

    /// <summary>
    /// Strikes a refunded redemption's reel out of the show. One still waiting simply goes; one
    /// already on screen cannot be unplayed, so it runs to the end and is silenced instead – no pet
    /// walks on and nothing is said, because there is no longer a prize to announce.
    /// </summary>
    private void Revoke(string redemptionId)
    {
        if (redemptionId.Length == 0) return;
        lock (_gate)
        {
            if (_current is not null && SameId(_current.RedemptionId, redemptionId)) _current.Revoked = true;
            if (!_queue.Any(queued => SameId(queued.RedemptionId, redemptionId))) return;
            QueuedSpin[] keep = _queue.Where(queued => !SameId(queued.RedemptionId, redemptionId)).ToArray();
            _queue.Clear();
            foreach (QueuedSpin queued in keep) _queue.Enqueue(queued);
        }
    }

    private async Task GiveAsync(SpinPendingGift pending, string giverName, string targetName)
    {
        string prize = PrizeName(pending.PetId);
        try
        {
            TwitchUser? target = resolveUser is null ? null : await resolveUser(targetName).ConfigureAwait(false);
            if (target is null)
            {
                Announced?.Invoke(BotFlow.SpinGiftUnknown, Values(("viewer", giverName), ("target", targetName)), true);
                return;
            }

            // Whether the recipient can take it, closing the gift and booking the prize are one step
            // in the store. Two winners can come back from this await in the same moment holding the
            // same pet and the same friend's name, and only one of them can be the one who gave it –
            // asked apart, the other would announce a gift the recipient never received.
            SpinGiftResult given = store.TryGive(
                pending.RedemptionId,
                new SpinWin(target.Id, target.Login, target.DisplayName, pending.PetId, DateTimeOffset.UtcNow,
                    GiftedFrom: pending.WinnerUserId, RedemptionId: Kept(pending.RedemptionId)));

            // The winner themselves is caught here too: a duplicate means they own it already. The
            // gift closes on this answer just as it does on a successful one – naming somebody who
            // cannot take the pet is how the chance is spent, not a first attempt at spending it.
            if (given == SpinGiftResult.AlreadyOwned)
            {
                AppLog.Info($"Lyckosnurren: {giverName}s gåva av {prize} gick förlorad – {target.DisplayName} hade den redan.");
                Announced?.Invoke(BotFlow.SpinGiftOwned, Values(("viewer", giverName), ("target", target.DisplayName), ("prize", prize)), true);
                return;
            }

            // Gone in the meantime – the timeout fired, or a second "!ge" beat this one. Whoever
            // took it was also announced for it, so there is nothing left to say here.
            if (given == SpinGiftResult.Gone) return;

            AppLog.Info($"Lyckosnurren: {giverName} skänkte {prize} till {target.DisplayName}.");
            Announced?.Invoke(BotFlow.SpinGifted, Values(("viewer", giverName), ("target", target.DisplayName), ("prize", prize)), true);
        }
        catch (Exception ex)
        {
            // Twitch could not be asked. The gift stays pending, so trying again is the honest
            // advice – which is what the unknown-name line already says.
            AppLog.Warn($"Lyckosnurren: kunde inte slå upp mottagaren \"{targetName}\": {ex.Message}");
            Announced?.Invoke(BotFlow.SpinGiftUnknown, Values(("viewer", giverName), ("target", targetName)), true);
        }
    }

    private void Enqueue(QueuedSpin spin)
    {
        lock (_gate) _queue.Enqueue(spin);
        StartNext();
    }

    private void StartNext()
    {
        QueuedSpin? starting;
        lock (_gate)
        {
            if (_current is not null || _queue.Count == 0) return;
            _current = _queue.Dequeue();
            _currentDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(settings.Spin.SpinSeconds) + AckMargin;
            starting = _current;
        }

        SpinSettings spin = settings.Spin;
        // The reel loops through every winnable pet. The prize is appended if a reload just
        // unmarked it – the reel must be able to land on what was actually won.
        var reel = catalog.Winnable.Select(pet => pet.Id).ToList();
        if (!reel.Contains(starting.PetId, StringComparer.OrdinalIgnoreCase)) reel.Add(starting.PetId);
        hub.PublishSpin(new DockSpin(
            starting.SpinId, reel, starting.PetId, starting.DisplayName,
            spin.Side, spin.SpinSeconds, spin.Title, starting.Duplicate));
    }

    /// <summary>The curtain call: the winner's pet steps onto the lawn and the bot says what happened.</summary>
    private void Finish(QueuedSpin spin)
    {
        // The redemption behind it was paid back while the reel was turning. The prize is already
        // struck from the store, so there is nothing to put on the lawn and nothing to say.
        if (spin.Revoked) return;

        string prize = PrizeName(spin.PetId);

        // A duplicate stays off the lawn – its story continues in the gift flow – and a rehearsal
        // walks on quietly without a word in chat.
        if (!spin.Duplicate) pets.SpawnDirect(spin.UserId, spin.DisplayName, spin.PetId);
        if (spin.Test) return;

        // The tier the reel was drawn on, not the one the pet wears now: a rehearsal or a queued spin
        // can outlive a change in the settings window, and the announcement belongs to the draw.
        string rarity = spin.Rarity;
        if (spin.Duplicate)
            Announced?.Invoke(BotFlow.SpinDuplicate, DuplicateValues(spin.DisplayName, prize, rarity, settings.Spin), true);
        else
            Announced?.Invoke(BotFlow.SpinWin, Values(("viewer", spin.DisplayName), ("prize", prize), ("rarity", rarity)), true);
    }

    private string PrizeName(string petId) => catalog.Find(petId)?.Name ?? petId;

    /// <summary>
    /// The tier a pet sits in today, worded for chat. A pet the catalog no longer knows reads as
    /// common, which is the same thing <see cref="Draw"/> assumes about an unknown tier.
    ///
    /// <para>This is the live answer, not the one a win was drawn on: what a spin in flight was
    /// announced with is carried on the reel itself. A win is stored by id alone, so a collection
    /// listed long afterwards can only be read against the catalog as it stands – and should be,
    /// since a re-tiered art is worth what it is worth now to everybody who owns it.</para>
    /// </summary>
    private string PrizeRarity(string petId) => PetRarity.Normalize(catalog.Find(petId)?.Rarity);

    /// <summary>
    /// The viewer's wins as one readable line, cut before it can crowd out the template around it.
    /// Each art carries the tier it sits in today – a collection is worth showing off for what is
    /// rare in it, not only for how long it is.
    /// </summary>
    private string ListOf(IReadOnlyList<SpinWin> wins)
    {
        string list = string.Join(", ", wins.Select(win => $"{PrizeName(win.PetId)} ({PrizeRarity(win.PetId)})"));
        return list.Length <= 300 ? list : list[..299] + "…";
    }

    /// <summary>
    /// Everything the reel can land on, gathered under the tier it sits in rather than repeated art
    /// by art: a pool is read as a rarity guide, and grouping keeps a channel with twenty arts inside
    /// the line the collection has to fit in too. Rarest first, so a list long enough to be cut loses
    /// the commons at the end instead of the legendaries nobody asked about the commons for.
    /// </summary>
    private static string PoolOf(IReadOnlyList<PetDefinition> pool)
    {
        var groups = new List<string>(PetRarity.All.Count);
        foreach (string tier in PetRarity.All.Reverse())
        {
            string[] names = pool
                .Where(pet => string.Equals(PetRarity.Normalize(pet.Rarity), tier, StringComparison.Ordinal))
                .Select(pet => pet.Name)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            if (names.Length > 0) groups.Add($"{tier}: {string.Join(", ", names)}");
        }

        string list = string.Join(" · ", groups);
        return list.Length <= 300 ? list : list[..299] + "…";
    }

    private static Dictionary<string, string> DuplicateValues(string viewer, string prize, string rarity, SpinSettings spin) => Values(
        ("viewer", viewer),
        ("prize", prize),
        ("rarity", rarity),
        ("command", spin.GiveCommand),
        ("release", spin.ReleaseCommand),
        ("minutes", spin.GiftTimeoutMinutes.ToString()));

    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in pairs) values[key] = value;
        return values;
    }

    private static bool Matches(string text, string command) =>
        text.Equals(command, StringComparison.OrdinalIgnoreCase)
        || text.StartsWith(command + " ", StringComparison.OrdinalIgnoreCase);

    private static bool SameId(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>An id worth writing down, or nothing at all – a win with no purchase behind it.</summary>
    private static string? Kept(string redemptionId) => redemptionId.Length > 0 ? redemptionId : null;

    /// <param name="RedemptionId">
    /// What bought the reel, so a refund can find it again. Empty for a rehearsal.
    /// </param>
    /// <param name="Rarity">
    /// The tier the prize was actually drawn on, carried rather than looked up again at the curtain
    /// call: the streamer can re-tier a pet while its reel is still turning, and the word chat hears
    /// has to be the odds the win was won at, not the ones the next spin will use.
    /// </param>
    private sealed record QueuedSpin(
        string SpinId, string RedemptionId, string UserId, string DisplayName, string PetId, string Rarity, bool Duplicate, bool Test)
    {
        /// <summary>
        /// Set when the redemption behind this reel was paid back. The show is already running and
        /// cannot be taken back, but its curtain call can be: the prize is gone, so nothing walks on
        /// and nothing is said.
        /// </summary>
        public bool Revoked { get; set; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
    }
}
