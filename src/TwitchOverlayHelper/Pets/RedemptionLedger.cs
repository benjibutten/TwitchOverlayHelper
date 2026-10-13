using TwitchOverlayHelper.Diagnostics;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Pets;

/// <summary>
/// The two Twitch calls the ledger makes. The gateway supplies the broadcaster: there is only ever
/// one channel whose rewards this app owns.
/// </summary>
public interface IRedemptionGateway
{
    /// <summary>Fulfils or cancels one redemption; cancelling is what hands the points back.</summary>
    Task AnswerAsync(string rewardId, string redemptionId, RedemptionStatus status, CancellationToken token);

    /// <summary>What is still sitting in one reward's queue.</summary>
    Task<IReadOnlyList<QueuedRedemption>> GetUnfulfilledAsync(string rewardId, CancellationToken token);
}

/// <summary>
/// A verdict Twitch has taken, worded for the streamer.
/// <paramref name="Subject"/> is what the redemption bought: "pet", "tts" or "spin".
/// <paramref name="RedemptionId"/> lets a feature that keeps its own books clear them; the spin
/// settles its owed fulfilments on it.
/// </summary>
public sealed record RedemptionNotice(
    bool Refunded, string ViewerName, int Cost, string Reason, string Subject = "pet", string RedemptionId = "");

/// <summary>
/// How long the ledger waits before it calls something undelivered.
/// </summary>
/// <param name="AckGrace">
/// How long a spawned pet has to be reported drawn before the redemption is paid back. Has to
/// cover a slow first paint and a spritesheet fetch.
/// </param>
/// <param name="OverlayGrace">
/// How long every pet overlay may be gone before the pets on screen are declared unseen. A reload
/// in OBS or a scene change drops the socket for a second or two.
/// </param>
/// <param name="ReceiptWindow">
/// How stale a receipt may be and still vouch for an entry booked just after it. Short, because
/// pet ids are viewer ids and come round again: a receipt from the viewer's last redemption must
/// not answer for this one.
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
/// <para>Twitch only refunds a redemption that is still unfulfilled, and a spawn only means the
/// server sent a frame – true even when the OBS browser source never came up. So the verdict waits
/// until the pet has lived out its time, and every way it can fail to be seen before then is a
/// refund.</para>
///
/// <para>Only rewards this app created ever get here: Twitch answers 403 for a reward made in the
/// dashboard, whatever the token carries.</para>
/// </summary>
public sealed class RedemptionLedger : IDisposable
{
    /// <summary>How many times one verdict is sent to Twitch before the entry is given up on.</summary>
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
        /// The verdict already decided for this entry, set when Twitch could not be told and the
        /// entry was put back to be retried. Without it the entry would be judged again, and a
        /// refund that failed to send could come back as a fulfilment.
        /// </summary>
        public RedemptionStatus? Verdict { get; set; }

        public string Reason { get; set; } = string.Empty;

        /// <summary>What this redemption bought; see <see cref="RedemptionNotice"/>.</summary>
        public string Subject { get; init; } = "pet";
    }

    private readonly IRedemptionGateway _gateway;
    private readonly PetRegistry _registry;
    private readonly Action<string> _despawn;
    private readonly RedemptionLedgerTimings _timings;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _pending = new(StringComparer.Ordinal);
    private readonly Timer _timer;

    // Pet ids an overlay has reported drawing, and when. The spawn frame goes out before Track is
    // called, so an overlay on the same machine can send its receipt before the entry exists.
    private readonly Dictionary<string, DateTimeOffset> _recentlyShown = new(StringComparer.Ordinal);

    // When every pet overlay went away, or null while at least one is connected.
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

    /// <summary>Raised after Twitch has taken a verdict.</summary>
    public event Action<RedemptionNotice>? Answered;

    /// <summary>
    /// Tells <see cref="SweepAsync"/> that another part of the app holds a redemption (by id) and
    /// settles it itself, so the sweep leaves it alone. A reconnect mid-stream sweeps again with a
    /// fresh cutoff, which would otherwise refund a reading still waiting for the streamer's yes.
    /// </summary>
    public Func<string, bool>? ClaimedElsewhere { get; set; }

    /// <summary>How many redemptions are still waiting on a verdict.</summary>
    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>
    /// Starts tracking the redemption behind a pet on the lawn; it stays open until the pet has
    /// lived its time. Re-redeeming extends the same pet, and each redemption keeps its own expiry.
    /// </summary>
    public void Track(string redemptionId, string rewardId, string petId, string viewerName, int cost, DateTimeOffset expiresAt)
    {
        if (redemptionId.Length == 0 || rewardId.Length == 0) return;
        var entry = new Entry(redemptionId, rewardId, petId, viewerName, cost, expiresAt);
        lock (_gate)
        {
            // The overlay's receipt for this pet may already have arrived.
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
    /// overlay to draw on. Retries like <see cref="AnswerNow"/>.
    /// </summary>
    public Task RefundNow(string redemptionId, string rewardId, string viewerName, int cost, string reason) =>
        AnswerNow(redemptionId, rewardId, viewerName, cost, RedemptionStatus.Canceled, reason);

    /// <summary>
    /// Answers one redemption whose verdict is already decided. A failed call is retried on the
    /// timer, up to <see cref="MaxAttempts"/> times, so a token refresh or a dropped connection
    /// does not leave the viewer with neither the thing nor the points.
    /// </summary>
    public Task AnswerNow(
        string redemptionId, string rewardId, string viewerName, int cost, RedemptionStatus status, string reason,
        string subject = "pet") =>
        AnswerAsync(
            new Entry(redemptionId, rewardId, string.Empty, viewerName, cost, DateTimeOffset.UtcNow) { Subject = subject },
            status, reason);

    /// <summary>
    /// A pet was pushed off a full lawn before its time. Any redemption behind it is paid back
    /// rather than left to be booked as delivered.
    ///
    /// <para>A refundable reward never evicts, but its pet can still be evicted by one that does:
    /// a non-refundable redemption, the test button or a spin win.</para>
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
    /// Pays back everything still waiting, for when the whole lawn stops being visible – such as
    /// pets being switched off in the app, which hides the lawn while the pets go on living.
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
    /// The overlay reports it has drawn a pet. Marks every redemption riding on that pet as shown,
    /// and remembers the receipt for an entry that is tracked just after it.
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
            // The overlay reports every pet it draws, reconnects included, so receipts past the
            // window are pruned to keep this from growing for as long as the app runs.
            if (_recentlyShown.Count > 64)
            {
                foreach ((string id, DateTimeOffset at) in _recentlyShown.ToArray())
                    if (now - at >= _timings.ReceiptWindow) _recentlyShown.Remove(id);
            }
        }
    }

    /// <summary>
    /// How many pet overlays are connected. Zero starts the clock: once the overlay grace is out,
    /// the pets on the lawn are paid back.
    /// </summary>
    public void OverlayCountChanged(int count)
    {
        lock (_gate) _overlayGoneSince = count > 0 ? null : _overlayGoneSince ?? DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Twitch says a redemption changed status without us asking – the streamer worked the queue in
    /// the dashboard. The entry is dropped, and on a refund its pet is taken off the lawn.
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
            Answered?.Invoke(new RedemptionNotice(true, entry.ViewerName, entry.Cost, "återbetalad i Twitch", entry.Subject, entry.RedemptionId));
        }
    }

    /// <summary>
    /// Refunds everything redeemed on these rewards while nobody was listening. The pets live in
    /// memory only, so anything still unfulfilled from before the app subscribed (an exit, a crash,
    /// a spell in another channel) belongs to a pet that no longer exists.
    /// </summary>
    /// <param name="listeningSince">
    /// When EventSub confirmed it is delivering redemptions. Anything younger arrives through the
    /// normal path and is left alone.
    /// </param>
    /// <returns>
    /// True when every reward's queue was read. False means at least one could not be, and the
    /// sweep should be run again.
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
                // A reward we do not own, Twitch failing, or a token being refreshed. Only the first
                // is permanent and they cannot be told apart here, so the sweep reports unfinished.
                AppLog.Warn($"Pets: kunde inte läsa kön för belöning {rewardId}: {ex.Message}");
                complete = false;
                continue;
            }

            foreach (QueuedRedemption redemption in queued)
            {
                if (redemption.RedeemedAt >= listeningSince) continue;
                // Already tracked, with its pet on the lawn. The cutoff alone cannot rule this out:
                // EventSub can deliver a redemption made a moment before subscribing finished.
                lock (_gate)
                {
                    if (_pending.ContainsKey(redemption.Id)) continue;
                }
                if (ClaimedElsewhere?.Invoke(redemption.Id) == true) continue;
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
    /// the channel. The redemptions stay in Twitch's queue, where the streamer can work them by hand.
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

    /// <summary>One round of verdicts. A call that overlaps a round still running returns at once.</summary>
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
                        // Already judged; a retry of a verdict Twitch has not taken yet.
                        due.Add((entry, verdict, entry.Reason));
                    }
                    else if (now >= entry.ExpiresAt)
                    {
                        due.Add((entry, RedemptionStatus.Fulfilled, "peten levde klart"));
                    }
                    else if (overlayLost)
                    {
                        due.Add((entry, RedemptionStatus.Canceled, "pet-overlayen försvann"));
                    }
                    else if (!entry.Shown && now - entry.SpawnedAt > _timings.AckGrace)
                    {
                        // The frame went out and no overlay reported drawing it: a browser source
                        // that is connected but never came up properly.
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
            // A timer callback that throws takes the process with it.
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
            // Not ours to answer: the reward was made in the dashboard, the client id has changed,
            // or the reward is gone. Retrying cannot fix any of those.
            AppLog.Warn($"Pets: Twitch nekade svaret på inlösen {entry.RedemptionId} – belöningen är inte skapad av appen längre. {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            // Everything else is put back and retried next round. Deliberately a catch-all: the
            // entry has already left the pending set, so an exception that escaped (a logout
            // mid-round throws TwitchAuthException from a layer below) would lose it for good.
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
        Answered?.Invoke(new RedemptionNotice(refund, entry.ViewerName, entry.Cost, reason, entry.Subject, entry.RedemptionId));
    }

    /// <summary>Takes the refunded pet off the lawn.</summary>
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
