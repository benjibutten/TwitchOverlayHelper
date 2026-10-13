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
/// The answer to one spin redemption. <paramref name="Refundable"/> says whether the points can come
/// back at all.
/// </summary>
public sealed record SpinRedemptionResult(SpinOutcome Outcome, bool Refundable)
{
    public static readonly SpinRedemptionResult NotASpinReward = new(SpinOutcome.NotASpinReward, false);
}

/// <summary>
/// A verdict the spin owes Twitch about one redemption. The app wires it to
/// <c>RedemptionLedger.AnswerNow</c>, which retries until Twitch takes it.
/// </summary>
public sealed record SpinVerdict(string RedemptionId, string RewardId, string ViewerName, int Cost, bool Refund, string Reason);

/// <summary>
/// Lyckosnurren. Decides what a spin redemption wins, books the win, and stages the reel on the pet
/// overlay – in that order, so the outcome is on disk before the first frame plays and a browser
/// source dying mid-spin can cost the show but never the prize.
///
/// <para>The rarity-weighted draw runs over every winnable pet, so a viewer can win one they already
/// own. A duplicate is answered as delivered like any other win, and buys one chance to place the
/// pet: the bot asks the winner to name someone to give it to, or to release it onto the lawn. A
/// timeout, or a recipient who owns it too, spends that chance; the points never come back. Without
/// a bot to hold that conversation the draw skips what the viewer owns, and only someone who owns
/// everything is paid back.</para>
/// </summary>
/// <param name="overlayCount">
/// How many pet overlays are connected right now. Defaults to asking the hub.
/// </param>
/// <param name="canGift">
/// Whether the bot can hold the gift conversation right now: logged in, connected and with the
/// duplicate message enabled. Defaults to the bot's settings alone, which say nothing about the
/// connection.
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
    /// reward deleted in the dashboard can never be answered.
    /// </summary>
    private const int MaxFulfilmentAttempts = 5;

    /// <summary>
    /// How much longer than its own duration a spin may hold the stage before the queue moves on.
    /// Only matters when the overlay accepted the frame and never answered.
    /// </summary>
    private static readonly TimeSpan AckMargin = TimeSpan.FromSeconds(12);

    private readonly Lock _gate = new();
    private readonly Queue<QueuedSpin> _queue = new();
    private QueuedSpin? _current;
    private DateTimeOffset _currentDeadline;
    private Timer? _timer;
    private int _testCounter;
    private bool _disposed;

    /// <summary>A verdict is due on Twitch for a booked win.</summary>
    public event Action<SpinVerdict>? Verdict;

    /// <summary>
    /// A line for the bot: the flow, its values, and whether the flow's cooldown should be ignored.
    /// True for the lines that answer one person about their own points or prize; false for the ones
    /// viewers can set off on purpose.
    /// </summary>
    public event Action<BotFlow, IReadOnlyDictionary<string, string>, bool>? Announced;

    /// <summary>Starts the once-a-second round.</summary>
    public void Start() => _timer ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    /// <summary>How many spins are staged or waiting.</summary>
    public int QueueLength
    {
        get { lock (_gate) return _queue.Count + (_current is null ? 0 : 1); }
    }

    /// <summary>The id of the spin holding the stage.</summary>
    internal string? CurrentSpinId
    {
        get { lock (_gate) return _current?.SpinId; }
    }

    /// <summary>
    /// True when the startup sweep must leave this redemption alone: it is a gift still waiting for
    /// its winner, or a win already handed out whose fulfilment Twitch has not taken yet.
    /// </summary>
    public bool Holds(string redemptionId) => store.HoldsPending(redemptionId) || store.Owes(redemptionId);

    /// <summary>Twitch accepted a verdict; anything owed on that redemption is settled.</summary>
    public void Settled(string redemptionId) => store.Settled(redemptionId);

    /// <summary>
    /// Raises every fulfilment this channel still owes, so a win booked while the app was closing is
    /// answered once redemptions can be answered again.
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
        // With the pets off the reel would play onto a hidden lawn and SpawnDirect would refuse the
        // prize. Checked here, while the points can still be handed back.
        if (!settings.Pets.Enabled) return new SpinRedemptionResult(SpinOutcome.PetsOff, refundable);

        string id = redemption.UserId.Length > 0 ? redemption.UserId : redemption.UserLogin;
        if (id.Length == 0) return SpinRedemptionResult.NotASpinReward;
        string name = redemption.DisplayName.Length > 0 ? redemption.DisplayName : redemption.UserLogin;

        // A refundable reward does not spend points on a show no overlay is showing. Where refunding
        // is impossible the spin goes ahead: the points are spent either way, and the win is real.
        if (refundable && (overlayCount?.Invoke() ?? hub.PetOverlayCount) == 0)
            return new SpinRedemptionResult(SpinOutcome.NoOverlay, true);

        IReadOnlyList<PetDefinition> winnable = catalog.Winnable;
        if (winnable.Count == 0) return new SpinRedemptionResult(SpinOutcome.NothingToWin, refundable);

        // Without a bot to hold the gift conversation, the draw skips what the viewer already owns.
        bool giftable = canGift?.Invoke() ?? settings.Bot.Speaks(BotFlow.SpinDuplicate);
        IReadOnlyList<PetDefinition> pool = giftable
            ? winnable
            : winnable.Where(pet => !store.Owns(id, pet.Id)).ToArray();
        if (pool.Count == 0) return new SpinRedemptionResult(SpinOutcome.AllOwned, refundable);

        PetDefinition prize = Draw(pool, Random.Shared);
        bool duplicate = store.Owns(id, prize.Id);

        // Booked before the show: a crash from here on can cost the animation and the announcement,
        // never the prize or the points.
        if (duplicate)
        {
            // Answered as delivered right away, like a new win: nothing that happens to the gift
            // afterwards hands the points back.
            SpinOwedFulfilment? debt = Debt(
                refundable, redemption.Id, redemption.RewardId, name, redemption.RewardCost ?? 0, "dubbletten är bokförd");
            SpinPendingGift? displaced = store.AddPending(
                new SpinPendingGift(redemption.Id, id, name, prize.Id,
                    DateTimeOffset.UtcNow + TimeSpan.FromMinutes(spin.GiftTimeoutMinutes)),
                debt);
            Raise(debt);
            // The winner spun again while their last duplicate was still open. Nothing is owed on the
            // displaced gift, but its chance is over and the winner is told before the new prompt.
            if (displaced is not null)
            {
                AppLog.Info($"Lyckosnurren: {name}s öppna gåva av {PrizeName(displaced.PetId)} ersattes av en ny snurr.");
                Announced?.Invoke(BotFlow.SpinGiftExpired, Values(
                    ("viewer", displaced.WinnerName), ("prize", PrizeName(displaced.PetId))), true);
            }
        }
        else
        {
            // The prize and the debt for it land in one write. A crash between two writes would let
            // the next startup sweep refund the redemption on top of a pet the viewer keeps.
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
    /// The spin's own chat commands, tried before the bot's static ones. Returns true when the line
    /// is handled and nothing else should touch it.
    /// </summary>
    public bool HandleChatMessage(ChatMessage message)
    {
        SpinSettings spin = settings.Spin;
        // Switched off, the command words are ordinary chat, so the streamer's own "!mina" command
        // can answer. Gifts left open time out on their own.
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
            // Nothing to give away. Left unhandled: the word may be a command the streamer defined
            // for something else.
            if (pending is null) return false;

            store.Touch(id, message.UserLogin, message.DisplayName);
            string target = text[spin.GiveCommand.Length..].Trim().TrimStart('@');
            int space = target.IndexOf(' ');
            if (space >= 0) target = target[..space];

            if (target.Length == 0)
            {
                // A bare "!ge" gets the instructions again.
                Announced?.Invoke(BotFlow.SpinDuplicate, DuplicateValues(message.DisplayName, PrizeName(pending.PetId), PrizeRarity(pending.PetId), spin), true);
                return true;
            }

            _ = GiveAsync(pending, message.DisplayName, target);
            return true;
        }

        if (Matches(text, spin.ReleaseCommand))
        {
            SpinPendingGift? pending = store.PendingFor(id);
            // Unhandled when nothing is open, as for the give command. With the pets off or the
            // species gone from the folder there is nothing to release, so the gift stays open for
            // "!ge".
            if (pending is null || !settings.Pets.Enabled || catalog.Find(pending.PetId) is null) return false;

            store.Touch(id, message.UserLogin, message.DisplayName);
            // Closing the gift decides who acted on it: a "!ge" that was waiting on Twitch for the
            // recipient may have closed it a moment ago.
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
    /// Twitch reports a redemption's status changed, possibly by the streamer working the queue in
    /// the dashboard. A refund takes the win or the open gift with it; a fulfilment clears whatever
    /// verdict the app still owed.
    /// </summary>
    public void HandleExternalUpdate(string redemptionId, string status)
    {
        if (status.Equals("CANCELED", StringComparison.OrdinalIgnoreCase))
        {
            if (store.RemovePending(redemptionId))
                AppLog.Info("Lyckosnurren: en väntande gåva betalades tillbaka via Twitchs egen kö.");
            // The points are back, so the win goes too; left standing it would be a prize kept on top
            // of a refund.
            if (store.RevokeWin(redemptionId) is { } revoked)
                AppLog.Info($"Lyckosnurren: {revoked.DisplayName}s vinst {PrizeName(revoked.PetId)} togs tillbaka – inlösen betalades tillbaka i Twitchs egen kö.");
            Revoke(redemptionId);
            // A fulfilment still owed on a refunded redemption can never be delivered, and the debt
            // would shield the closed redemption from the sweep.
            store.Settled(redemptionId);
            return;
        }

        if (!status.Equals("FULFILLED", StringComparison.OrdinalIgnoreCase)) return;

        // Our own verdict coming back, or the streamer closing the redemption by hand. An open gift
        // is left alone: it holds the chance to place the pet, not the points.
        store.Settled(redemptionId);
    }

    /// <summary>A rehearsal spin from the app's test button: a real reel, nothing booked, nobody told.</summary>
    public void SpinTest()
    {
        // Before anything is marked winnable the whole catalog stands in.
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
    /// Drops everything staged without playing it. Nothing on disk is touched: wins and open gifts
    /// survive.
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
    /// The app has left the channel: drops the staged reels and the open gifts, which belong to the
    /// channel they were won in. The wins stay.
    /// </summary>
    public void LeaveChannel()
    {
        Reset();
        store.ClearPending();
    }

    /// <summary>
    /// One round: a spin the overlay never answered for, and gifts whose winners never chose.
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
                // The win is already booked; only the announcement was waiting on the overlay.
                AppLog.Warn("Lyckosnurren: overlayen kvitterade aldrig snurren – vinsten gäller ändå.");
                Finish(overdue);
            }
            StartNext();

            // The redemption was settled when the duplicate was drawn, so an expired gift only needs
            // its winner told.
            foreach (SpinPendingGift gift in store.TakeExpired(now))
            {
                string expired = PrizeName(gift.PetId);
                AppLog.Info($"Lyckosnurren: {gift.WinnerName}s gåva av {expired} förföll.");
                Announced?.Invoke(BotFlow.SpinGiftExpired, Values(("viewer", gift.WinnerName), ("prize", expired)), true);
            }
        }
        catch (Exception ex)
        {
            // A timer callback that throws takes the process with it.
            AppLog.Error("Lyckosnurren: fel i rundan", ex);
        }
    }

    /// <summary>One rarity-weighted draw from a non-empty pool.</summary>
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
    /// The fulfilment a booking leaves owed to Twitch, or null when the redemption cannot be
    /// answered. It is written together with the prize and raised afterwards; until Twitch takes the
    /// verdict it survives a restart and keeps <see cref="Holds"/> true.
    /// </summary>
    private SpinOwedFulfilment? Debt(bool refundable, string redemptionId, string rewardId, string viewerName, int cost, string reason) =>
        refundable && redemptionId.Length > 0
            ? new SpinOwedFulfilment(redemptionId, rewardId, hub.BroadcasterId, viewerName, cost, reason)
            : null;

    /// <summary>Raises the verdict for a debt already on disk; does nothing for null.</summary>
    private void Raise(SpinOwedFulfilment? debt)
    {
        if (debt is null) return;
        Verdict?.Invoke(new SpinVerdict(debt.RedemptionId, debt.RewardId, debt.ViewerName, debt.Cost, Refund: false, debt.Reason));
    }

    /// <summary>
    /// Strikes a refunded redemption's reel out of the show. One still waiting is dropped; one
    /// already on screen plays out, but no pet walks on and nothing is announced.
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

            // Checking the recipient, closing the gift and booking the prize are one step in the
            // store: two winners giving the same pet to the same person can return from the await
            // above at once, and only one of them may succeed.
            SpinGiftResult given = store.TryGive(
                pending.RedemptionId,
                new SpinWin(target.Id, target.Login, target.DisplayName, pending.PetId, DateTimeOffset.UtcNow,
                    GiftedFrom: pending.WinnerUserId, RedemptionId: Kept(pending.RedemptionId)));

            // Also catches the winner naming themselves. The gift is closed on this answer too:
            // naming somebody who already owns the pet spends the chance.
            if (given == SpinGiftResult.AlreadyOwned)
            {
                AppLog.Info($"Lyckosnurren: {giverName}s gåva av {prize} gick förlorad – {target.DisplayName} hade den redan.");
                Announced?.Invoke(BotFlow.SpinGiftOwned, Values(("viewer", giverName), ("target", target.DisplayName), ("prize", prize)), true);
                return;
            }

            // Gone in the meantime – the timeout fired, or another command beat this one and has
            // already been announced.
            if (given == SpinGiftResult.Gone) return;

            AppLog.Info($"Lyckosnurren: {giverName} skänkte {prize} till {target.DisplayName}.");
            Announced?.Invoke(BotFlow.SpinGifted, Values(("viewer", giverName), ("target", target.DisplayName), ("prize", prize)), true);
        }
        catch (Exception ex)
        {
            // Twitch could not be asked. The gift stays pending, and the unknown-name line tells the
            // winner to try again.
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
        // The reel loops through every winnable pet. The prize is appended in case a catalog reload
        // unmarked it: the reel must be able to land on what was won.
        var reel = catalog.Winnable.Select(pet => pet.Id).ToList();
        if (!reel.Contains(starting.PetId, StringComparer.OrdinalIgnoreCase)) reel.Add(starting.PetId);
        hub.PublishSpin(new DockSpin(
            starting.SpinId, reel, starting.PetId, starting.DisplayName,
            spin.Side, spin.SpinSeconds, spin.Title, starting.Duplicate));
    }

    /// <summary>The curtain call: the winner's pet steps onto the lawn and the bot says what happened.</summary>
    private void Finish(QueuedSpin spin)
    {
        if (spin.Revoked) return;

        string prize = PrizeName(spin.PetId);

        // A duplicate stays off the lawn until the gift flow places it; a rehearsal walks on
        // without a word in chat.
        if (!spin.Duplicate) pets.SpawnDirect(spin.UserId, spin.DisplayName, spin.PetId);
        if (spin.Test) return;

        // The tier the prize was drawn at, which may differ from the pet's current one.
        string rarity = spin.Rarity;
        if (spin.Duplicate)
            Announced?.Invoke(BotFlow.SpinDuplicate, DuplicateValues(spin.DisplayName, prize, rarity, settings.Spin), true);
        else
            Announced?.Invoke(BotFlow.SpinWin, Values(("viewer", spin.DisplayName), ("prize", prize), ("rarity", rarity)), true);
    }

    private string PrizeName(string petId) => catalog.Find(petId)?.Name ?? petId;

    /// <summary>
    /// The tier a pet sits in in the current catalog, worded for chat. A pet the catalog does not
    /// know reads as common.
    /// </summary>
    private string PrizeRarity(string petId) => PetRarity.Normalize(catalog.Find(petId)?.Rarity);

    // Twitch refuses a message past 500 characters, and the template around the list needs the rest.
    private const int ListBudget = 380;

    /// <summary>The viewer's wins, each under its current tier.</summary>
    private string ListOf(IReadOnlyList<SpinWin> wins) =>
        ByTier(wins.Select(win => (PrizeName(win.PetId), catalog.Find(win.PetId)?.Rarity)));

    /// <summary>Everything the reel can land on, under the tier it is drawn at.</summary>
    private static string PoolOf(IReadOnlyList<PetDefinition> pool) =>
        ByTier(pool.Select(pet => (pet.Name, (string?)pet.Rarity)));

    /// <summary>
    /// Arts as one chat line, gathered under their tier with the rarest first:
    /// "legendarisk: A · vanlig: B, C". More arts than <see cref="ListBudget"/> has room for end on
    /// a whole name followed by how many were left out – the commons, never the legendaries. The
    /// rarest art is always named, whatever its length.
    /// </summary>
    private static string ByTier(IEnumerable<(string Name, string? Rarity)> arts)
    {
        // The rarest tier is the one with the smallest share of the draw.
        (string Name, string Rarity)[] ordered = arts
            .Select(art => (art.Name, Rarity: PetRarity.Normalize(art.Rarity)))
            .OrderBy(art => PetRarity.Weight(art.Rarity))
            .ThenBy(art => art.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        string line = string.Empty;
        string? tier = null;
        for (int i = 0; i < ordered.Length; i++)
        {
            (string name, string rarity) = ordered[i];
            string piece = rarity == tier ? $", {name}" : $"{(tier is null ? string.Empty : " · ")}{rarity}: {name}";
            // A piece only fits if the count of what follows it still fits behind it.
            int after = ordered.Length - i - 1;
            if (i > 0 && line.Length + piece.Length + LeftOut(after).Length > ListBudget) return line + LeftOut(after + 1);
            line += piece;
            tier = rarity;
        }
        return line;
    }

    private static string LeftOut(int count) => count == 0 ? string.Empty : $" … +{count} till";

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

    /// <summary>The id, or null when it is empty (a win with no purchase behind it).</summary>
    private static string? Kept(string redemptionId) => redemptionId.Length > 0 ? redemptionId : null;

    /// <param name="RedemptionId">
    /// What bought the reel, so a refund can find it again. Empty for a rehearsal.
    /// </param>
    /// <param name="Rarity">
    /// The tier the prize was drawn at. The streamer can re-tier a pet while its reel is turning,
    /// and the announcement names the odds the win was won at.
    /// </param>
    private sealed record QueuedSpin(
        string SpinId, string RedemptionId, string UserId, string DisplayName, string PetId, string Rarity, bool Duplicate, bool Test)
    {
        /// <summary>
        /// Set when the redemption behind this reel was paid back while it was on screen: the reel
        /// plays out, but nothing walks on and nothing is announced.
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
