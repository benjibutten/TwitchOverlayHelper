using TwitchOverlayHelper.Diagnostics;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Pets;

/// <summary>
/// The two Twitch calls the ledger makes, behind an interface so the deciding can be tested without
/// a network. The broadcaster is the gateway's business, not the ledger's – there is only ever one
/// channel whose rewards this app owns.
/// </summary>
public interface IRedemptionGateway
{
    /// <summary>Fulfils or cancels one redemption; cancelling is what hands the points back.</summary>
    Task AnswerAsync(string rewardId, string redemptionId, RedemptionStatus status, CancellationToken token);

    /// <summary>What is still sitting in one reward's queue.</summary>
    Task<IReadOnlyList<QueuedRedemption>> GetUnfulfilledAsync(string rewardId, CancellationToken token);
}

/// <summary>
/// Something the ledger did, worded for the streamer rather than for the log.
/// <paramref name="Subject"/> is what the redemption bought – "pet" or "tts" – so the app can put
/// the sentence next to the feature it belongs to instead of reporting a reading under the pets.
/// </summary>
public sealed record RedemptionNotice(bool Refunded, string ViewerName, int Cost, string Reason, string Subject = "pet");

/// <summary>
/// How long the ledger waits before it calls something undelivered.
/// </summary>
/// <param name="AckGrace">
/// How long a spawned pet has to be reported drawn before the redemption is paid back. Long enough
/// for a slow first paint and a spritesheet fetch, short enough that the viewer meets the refund
/// while they still remember redeeming.
/// </param>
/// <param name="OverlayGrace">
/// How long every pet overlay may be gone before the pets on screen are declared unseen. A reload
/// in OBS or a scene change drops the socket for a second or two and must not cost anybody their
/// points; a source removed from the scene never comes back.
/// </param>
/// <param name="ReceiptWindow">
/// How stale a receipt may be and still vouch for an entry booked just after it. The spawn frame
/// leaves before the entry is written, so on one machine the receipt can beat the bookkeeping.
/// Short on purpose: pet ids are viewer ids and come round again, so a receipt from the viewer's
/// last redemption must not answer for this one.
/// </param>
public sealed record RedemptionLedgerTimings(TimeSpan AckGrace, TimeSpan OverlayGrace, TimeSpan Interval, TimeSpan ReceiptWindow)
{
    public static readonly RedemptionLedgerTimings Default =
        new(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
}

/// <summary>
/// Keeps a redemption open until the pet it bought has actually been delivered, and pays it back
/// when it has not.
///
/// <para><b>Why it is not simpler.</b> Twitch offers exactly one moment to refund: while the
/// redemption still sits unfulfilled in the channel's queue. Fulfilling at spawn would close that
/// door immediately – and spawn only means the server added a pet and sent a frame, which is still
/// true when the OBS browser source failed to come up and the lawn is empty. So the verdict waits
/// for the pet to live out its time, and every way it can fail to be seen in the meantime is a
/// refund instead.</para>
///
/// <para>Only rewards this app created ever get here. Everything else has no verdict to give:
/// Twitch answers 403 for a reward made in the dashboard, whatever the token carries.</para>
/// </summary>
public sealed class RedemptionLedger : IDisposable
{
    /// <summary>Twitch was there but unhappy this many times in a row; after that the entry is let go.</summary>
    private const int MaxAttempts = 5;

    private sealed class Entry(string redemptionId, string rewardId, string petId, string viewerName, int cost, DateTimeOffset expiresAt)
    {
        public string RedemptionId { get; } = redemptionId;
        public string RewardId { get; } = rewardId;
        public string PetId { get; } = petId;
        public string ViewerName { get; } = viewerName;
        public int Cost { get; } = cost;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public DateTimeOffset SpawnedAt { get; } = DateTimeOffset.UtcNow;
        public bool Shown { get; set; }
        public int Attempts { get; set; }

        /// <summary>
        /// The verdict already decided for this entry, set only when Twitch could not be told and
        /// the entry went back in the queue to be tried again. It has to survive the wait: an
        /// entry put back without it would be judged by the rules a second time, and a refund that
        /// failed to send would come back round as a fulfilment – the one mistake here that quietly
        /// keeps the viewer's points.
        /// </summary>
        public RedemptionStatus? Verdict { get; set; }

        public string Reason { get; set; } = string.Empty;

        /// <summary>What this redemption bought, carried so the notice can be worded for it.</summary>
        public string Subject { get; init; } = "pet";
    }

    private readonly IRedemptionGateway _gateway;
    private readonly PetRegistry _registry;
    private readonly Action<string> _despawn;
    private readonly RedemptionLedgerTimings _timings;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _pending = new(StringComparer.Ordinal);
    private readonly Timer _timer;

    /// <summary>
    /// Pet ids an overlay has reported drawing, and when. Kept because the receipt can beat the
    /// bookkeeping: the spawn frame goes out from inside the spawn, and the entry is only written
    /// once that call has returned, so a lawn on the same machine can answer in the gap. Without
    /// this the receipt would land on nothing, and a pet everybody watched would be paid back when
    /// the grace period ran out.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _recentlyShown = new(StringComparer.Ordinal);

    /// <summary>When every pet overlay went away, or null while at least one is connected.</summary>
    private DateTimeOffset? _overlayGoneSince;
    private int _ticking;
    private bool _disposed;

    public RedemptionLedger(IRedemptionGateway gateway, PetRegistry registry, Action<string> despawn, RedemptionLedgerTimings? timings = null)
    {
        _gateway = gateway;
        _registry = registry;
        _despawn = despawn;
        _timings = timings ?? RedemptionLedgerTimings.Default;
        _timer = new Timer(_ => _ = TickAsync(), null, _timings.Interval, _timings.Interval);
    }

    /// <summary>Raised after Twitch has been told, so the app can say what happened.</summary>
    public event Action<RedemptionNotice>? Answered;

    /// <summary>
    /// Asks whether some other part of the app is already holding a redemption, by id. Set by the
    /// reading queue, which owns its own redemptions and settles them itself.
    ///
    /// <para>Only the sweep consults this, and it is what keeps the sweep from undoing the very
    /// thing it exists to back up. A reconnect mid-stream runs the sweep again with a fresh cutoff,
    /// and every reading currently waiting for the streamer's yes was redeemed before that moment –
    /// so without this, coming back from a dropped connection would pay back the request sitting on
    /// screen while the streamer was still reading it.</para>
    /// </summary>
    public Func<string, bool>? ClaimedElsewhere { get; set; }

    /// <summary>How many redemptions are still waiting on a verdict. For the tests and the log.</summary>
    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>
    /// A pet is on the lawn and the redemption behind it stays in the queue until it has lived its
    /// time. Re-redeeming extends the same pet, so a second entry keeps its own expiry: the first
    /// purchase was delivered in full whatever happens to the extension.
    /// </summary>
    public void Track(string redemptionId, string rewardId, string petId, string viewerName, int cost, DateTimeOffset expiresAt)
    {
        if (redemptionId.Length == 0 || rewardId.Length == 0) return;
        var entry = new Entry(redemptionId, rewardId, petId, viewerName, cost, expiresAt);
        lock (_gate)
        {
            // The receipt for this pet may already have come back – the frame left before this call
            // did – so it is taken from what arrived rather than waited for all over again.
            if (_recentlyShown.TryGetValue(petId, out DateTimeOffset shownAt)
                && DateTimeOffset.UtcNow - shownAt < _timings.ReceiptWindow)
            {
                entry.Shown = true;
            }
            _pending[redemptionId] = entry;
        }
    }

    /// <summary>
    /// Pays back a redemption that never became a pet at all – pets switched off, the lawn full, no
    /// overlay to draw on. Nothing is tracked: there is no pet whose life could change the answer.
    /// </summary>
    public Task RefundNow(string redemptionId, string rewardId, string viewerName, int cost, string reason) =>
        AnswerNow(redemptionId, rewardId, viewerName, cost, RedemptionStatus.Canceled, reason);

    /// <summary>
    /// Answers one redemption whose verdict is already decided, and keeps trying until Twitch takes
    /// it. Nothing is tracked: unlike a pet there is no life left to run that could change the
    /// answer, and the only thing still in question is whether the message got through.
    ///
    /// <para><b>Why this is not a bare API call.</b> Twitch will only accept a verdict while the
    /// redemption is still unfulfilled, and a token being refreshed, a dropped connection or a bad
    /// minute at Twitch would otherwise turn a refusal into a viewer who paid and got neither the
    /// thing nor their points back. So it goes through the same retrying path as everything else
    /// here: put back with its verdict attached, tried again on the timer, given up on out loud.</para>
    /// </summary>
    public Task AnswerNow(
        string redemptionId, string rewardId, string viewerName, int cost, RedemptionStatus status, string reason,
        string subject = "pet") =>
        AnswerAsync(
            new Entry(redemptionId, rewardId, string.Empty, viewerName, cost, DateTimeOffset.UtcNow) { Subject = subject },
            status, reason);

    /// <summary>
    /// A pet was taken off the lawn before its time by something other than a refund – the lawn was
    /// full and an ordinary redemption or a test spawn pushed the oldest one home.
    ///
    /// <para>Rewards that can pay back refuse rather than evict, so they never do this to anybody.
    /// They can still be on the receiving end of it: an app-made pet is just a pet once it is out
    /// there, and the chat route and the test button both evict the oldest without asking whose it
    /// is. Without this the redemption would sit out the rest of its time and be booked as
    /// delivered, for a pet that left the screen minutes early.</para>
    /// </summary>
    public void PetEvicted(string petId)
    {
        if (petId.Length == 0) return;

        List<Entry> evicted = [];
        lock (_gate)
        {
            foreach (Entry entry in _pending.Values.ToArray())
            {
                if (!string.Equals(entry.PetId, petId, StringComparison.Ordinal)) continue;
                _pending.Remove(entry.RedemptionId);
                evicted.Add(entry);
            }
        }

        foreach (Entry entry in evicted)
            _ = AnswerAsync(entry, RedemptionStatus.Canceled, "peten fick gå hem i förtid");
    }

    /// <summary>
    /// Pays back everything still waiting, because the pets stopped being watchable for a reason
    /// that has nothing to do with any one of them – pets switched off in the app above all, which
    /// hides the whole lawn while the creatures on it go on living out their time.
    /// </summary>
    public void RefundAll(string reason)
    {
        Entry[] going;
        lock (_gate)
        {
            going = _pending.Values.ToArray();
            _pending.Clear();
        }

        foreach (Entry entry in going) _ = AnswerAsync(entry, RedemptionStatus.Canceled, reason);
    }

    /// <summary>
    /// The overlay reports it has drawn a pet. Every redemption riding on that pet is delivered –
    /// and the receipt is written down as well, for the entry that has not been booked yet.
    /// </summary>
    public void MarkShown(string petId)
    {
        if (petId.Length == 0) return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            foreach (Entry entry in _pending.Values)
                if (string.Equals(entry.PetId, petId, StringComparison.Ordinal)) entry.Shown = true;

            _recentlyShown[petId] = now;
            // The lawn reports every pet it draws, reconnects included, so this would otherwise
            // grow for as long as the app runs. Anything past the window can no longer vouch for
            // anybody.
            if (_recentlyShown.Count > 64)
            {
                foreach ((string id, DateTimeOffset at) in _recentlyShown.ToArray())
                    if (now - at >= _timings.ReceiptWindow) _recentlyShown.Remove(id);
            }
        }
    }

    /// <summary>
    /// How many pet overlays are connected. Zero starts the clock: the pets already on screen are
    /// being drawn for nobody, and once the grace period is out they are paid back.
    /// </summary>
    public void OverlayCountChanged(int count)
    {
        lock (_gate) _overlayGoneSince = count > 0 ? null : _overlayGoneSince ?? DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Twitch says a redemption changed status without us asking – the streamer worked the queue in
    /// the dashboard. A refund there has to reach the lawn too, or the pet stays up for a purchase
    /// that has been paid back.
    /// </summary>
    public void HandleExternalUpdate(string redemptionId, string status)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_pending.Remove(redemptionId, out entry)) return;
        }

        if (status.Equals("CANCELED", StringComparison.OrdinalIgnoreCase))
        {
            SendHome(entry);
            AppLog.Info($"Pets: {entry.ViewerName} fick tillbaka {entry.Cost} poäng via Twitchs egen kö.");
            Answered?.Invoke(new RedemptionNotice(true, entry.ViewerName, entry.Cost, "återbetalad i Twitch"));
        }
    }

    /// <summary>
    /// Clears the queue of everything redeemed while nobody was listening. The pets live in memory
    /// only, so anything still unfulfilled on one of our rewards from before we subscribed belongs
    /// to a pet that no longer exists – a clean exit, a crash mid-stream, a spell in another
    /// channel and the seconds it takes the socket to come up all read the same here, and the
    /// viewer is owed their points in every one of them.
    /// </summary>
    /// <param name="listeningSince">
    /// When EventSub confirmed it is delivering redemptions. Anything younger arrives through the
    /// normal path and is left alone.
    /// </param>
    /// <returns>
    /// Whether every reward's queue was actually read. False means at least one could not be, and
    /// the sweep is worth running again – its caller uses this to decide whether it may stop asking.
    /// A sweep that quietly counted a failed read as done would leave those redemptions in the
    /// queue for good.
    /// </returns>
    public async Task<bool> SweepAsync(IReadOnlyList<string> rewardIds, DateTimeOffset listeningSince, CancellationToken token = default)
    {
        bool complete = true;
        foreach (string rewardId in rewardIds)
        {
            IReadOnlyList<QueuedRedemption> queued;
            try
            {
                queued = await _gateway.GetUnfulfilledAsync(rewardId, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A reward we no longer own, Twitch having a bad minute, or a token being refreshed
                // underneath us. Only the first is permanent and we cannot tell them apart from
                // here, so it is reported as unfinished and tried again on the next connection.
                AppLog.Warn($"Pets: kunde inte läsa kön för belöning {rewardId}: {ex.Message}");
                complete = false;
                continue;
            }

            foreach (QueuedRedemption redemption in queued)
            {
                if (redemption.RedeemedAt >= listeningSince) continue;
                // Already ours, already alive on the lawn. The cutoff alone cannot rule this out:
                // EventSub can deliver a redemption made a moment before we finished subscribing,
                // and refunding a pet that is walking about right now would be the sweep undoing
                // the very path it exists to back up.
                lock (_gate)
                {
                    if (_pending.ContainsKey(redemption.Id)) continue;
                }
                // Somebody else's to settle – a reading still waiting on the streamer. Same reason
                // as the check above, for a queue this ledger cannot see into.
                if (ClaimedElsewhere?.Invoke(redemption.Id) == true) continue;
                // A refund that does not go through here is not lost: AnswerAsync puts the entry
                // back with its verdict attached, and the timer keeps trying.
                await AnswerAsync(
                    new Entry(redemption.Id, rewardId, string.Empty, redemption.UserName, redemption.Cost, DateTimeOffset.UtcNow),
                    RedemptionStatus.Canceled,
                    "appen var inte igång").ConfigureAwait(false);
            }
        }
        return complete;
    }

    /// <summary>
    /// Lets everything still waiting go without answering Twitch – the app is closing, or has left
    /// the channel. The redemptions stay in the queue, where the streamer can work them by hand,
    /// which is a better ending than a refund fired off while the window is disappearing.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _pending.Clear();
            _recentlyShown.Clear();
            _overlayGoneSince = null;
        }
    }

    /// <summary>
    /// One round of verdicts. Internal rather than private so the tests can drive it a step at a
    /// time instead of waiting on the timer.
    /// </summary>
    internal async Task TickAsync()
    {
        // The timer keeps firing while a slow round of API calls is still going; a second pass over
        // the same entries would answer each of them twice.
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<(Entry Entry, RedemptionStatus Status, string Reason)> due = [];

            lock (_gate)
            {
                bool overlayLost = _overlayGoneSince is { } since && now - since > _timings.OverlayGrace;
                foreach (Entry entry in _pending.Values.ToArray())
                {
                    if (entry.Verdict is { } verdict)
                    {
                        // Already judged; it is only here because telling Twitch did not go through.
                        due.Add((entry, verdict, entry.Reason));
                    }
                    else if (now >= entry.ExpiresAt)
                    {
                        // Lived its full time with somebody watching: the points are earned.
                        due.Add((entry, RedemptionStatus.Fulfilled, "peten levde klart"));
                    }
                    else if (overlayLost)
                    {
                        due.Add((entry, RedemptionStatus.Canceled, "pet-overlayen försvann"));
                    }
                    else if (!entry.Shown && now - entry.SpawnedAt > _timings.AckGrace)
                    {
                        // The frame went out and no overlay reported drawing it. This is the case
                        // that started all of this: a browser source that is connected but never
                        // came up properly.
                        due.Add((entry, RedemptionStatus.Canceled, "overlayen ritade aldrig peten"));
                    }
                    else
                    {
                        continue;
                    }
                    _pending.Remove(entry.RedemptionId);
                }
            }

            foreach ((Entry entry, RedemptionStatus status, string reason) in due)
                await AnswerAsync(entry, status, reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A timer callback that throws takes the process with it, and this one runs while the
            // streamer is live.
            AppLog.Error("Pets: fel i återbetalningsrundan", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private async Task AnswerAsync(Entry entry, RedemptionStatus status, string reason)
    {
        bool refund = status == RedemptionStatus.Canceled;
        try
        {
            await _gateway.AnswerAsync(entry.RewardId, entry.RedemptionId, status, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TwitchNotPermittedException ex)
        {
            // Not ours to answer: the reward was made in the dashboard after all, the client id has
            // changed, or the reward is gone. Retrying cannot fix any of those.
            AppLog.Warn($"Pets: Twitch nekade svaret på inlösen {entry.RedemptionId} – belöningen är inte skapad av appen längre. {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            // Everything else is put back and tried again next round: Twitch unhappy, Twitch not
            // there at all, or a token being refreshed underneath us – a logout mid-round throws
            // TwitchAuthException from a layer below this one, and it used to travel past a narrow
            // catch list and take the entry with it.
            //
            // Caught by type rather than by list on purpose. The entry has already been taken out
            // of the pending set by the time we get here, so anything that escapes loses it for
            // good, and what that costs is a viewer who paid for a pet nobody saw and never hears
            // about it again. A bug swallowed into a retry is the cheaper mistake, and the log
            // still carries it.
            entry.Attempts++;
            if (entry.Attempts < MaxAttempts)
            {
                entry.Verdict = status;
                entry.Reason = reason;
                lock (_gate) _pending.TryAdd(entry.RedemptionId, entry);
                AppLog.Warn($"Pets: kunde inte svara på inlösen {entry.RedemptionId} (försök {entry.Attempts}): {ex.Message}");
                return;
            }
            AppLog.Warn($"Pets: gav upp efter {MaxAttempts} försök att svara på inlösen {entry.RedemptionId}: {ex.Message}");
            return;
        }

        if (refund) SendHome(entry);
        AppLog.Info(refund
            ? $"Inlösen: {entry.ViewerName} fick tillbaka {entry.Cost} poäng – {reason}."
            : $"Inlösen: {entry.ViewerName}s köp markerat som klart – {reason}.");
        Answered?.Invoke(new RedemptionNotice(refund, entry.ViewerName, entry.Cost, reason, entry.Subject));
    }

    /// <summary>Takes the refunded pet off the lawn, so nothing paid back is left walking about.</summary>
    private void SendHome(Entry entry)
    {
        if (entry.PetId.Length == 0) return;
        if (_registry.Remove(entry.PetId)) _despawn(entry.PetId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
