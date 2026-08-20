using System.Text.Json;
using TwitchOverlayHelper.Storage;

namespace TwitchOverlayHelper.Spins;

/// <summary>
/// One pet somebody won in the lucky spin. Keyed by the viewer's Twitch user id, never their name:
/// names change hands and get retyped, while the id follows the account. The names are carried
/// along purely for showing – and refreshed whenever the person is seen, so a rename catches up on
/// its own.
/// </summary>
/// <param name="RedemptionId">
/// The redemption that paid for the win, where there was one. Kept so a refund can take the prize
/// back with it: without it the streamer could hand the points back in Twitch's own queue and the
/// viewer would keep the pet as well. Null on wins booked before this was written down, and on
/// anything handed out without a purchase behind it.
/// </param>
public sealed record SpinWin(
    string UserId,
    string Login,
    string DisplayName,
    string PetId,
    DateTimeOffset WonAt,
    string? GiftedFrom = null,
    string? RedemptionId = null);

/// <summary>How a gift landing ended – the three answers <see cref="SpinWinStore.TryGive"/> can give.</summary>
public enum SpinGiftResult
{
    /// <summary>The prize changed hands: the gift is closed and the win is the recipient's.</summary>
    Given,
    /// <summary>The gift was no longer open – it timed out, or another "!ge" got there first.</summary>
    Gone,
    /// <summary>The recipient already has this pet. The gift is left open for another name.</summary>
    AlreadyOwned
}

/// <summary>
/// A duplicate win waiting for its winner to name someone to give it to. Persisted next to the wins
/// rather than held in memory: the redemption behind it is still open on Twitch, and a crash while
/// somebody was choosing a friend must not eat both the prize and the points.
/// </summary>
public sealed record SpinPendingGift(
    string RedemptionId,
    string RewardId,
    string WinnerUserId,
    string WinnerLogin,
    string WinnerName,
    string PetId,
    int Cost,
    bool Refundable,
    DateTimeOffset ExpiresAt);

/// <summary>
/// A fulfilment this app owes Twitch for a prize it has already handed out. Persisted for the same
/// reason as the gifts, and for a sharper one: the win is on disk the instant it is drawn, while
/// telling Twitch about it lives in the ledger's memory. Closing the app in that gap would leave the
/// redemption unfulfilled with the pet already won, and the next startup sweep would pay it back –
/// the viewer keeping both the prize and the points. So the debt outlives the process, is replayed
/// once the app is listening again, and shields the redemption from the sweep in the meantime.
/// </summary>
/// <param name="Channel">
/// The broadcaster the debt was run up in. A redemption can only be answered in its own channel, so
/// a debt from one is never replayed while the app is pointed at another – it waits for its own.
/// </param>
/// <param name="Attempts">
/// How many times it has been replayed across sessions. A reward deleted in the dashboard can never
/// be answered again, and without this its debt would be retried at every startup for ever.
/// </param>
public sealed record SpinOwedFulfilment(
    string RedemptionId,
    string RewardId,
    string Channel,
    string ViewerName,
    int Cost,
    string Reason,
    int Attempts = 0);

/// <summary>
/// Every spin win on disk. The one thing in this feature that cannot be reconstructed from anywhere
/// – Twitch has no record of who won what – so it lives in its own file with the same protection as
/// the nicknames: written atomically on every change, with a dated copy kept beside each save and
/// recovery from the newest copy that parses.
/// </summary>
public sealed class SpinWinStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly BackedUpJsonFile _file;
    private readonly Lock _lock = new();
    private List<SpinWin> _wins = [];
    private List<SpinPendingGift> _pending = [];
    private List<SpinOwedFulfilment> _owed = [];

    public SpinWinStore(string? path = null, int keepBackups = BackedUpJsonFile.DefaultKeep)
    {
        _file = new BackedUpJsonFile(path ?? ProfilePaths.File("spinwins.json"), keepBackups);
        if (_file.TryRead(JsonOptions, out SpinWinFile? saved) && saved is not null)
        {
            // A hand-edited file can hold nulls, in the list and in the fields alike; a row that
            // cannot say whose win it is or of what is dropped rather than crashed on.
            _wins = (saved.Wins ?? [])
                .Where(win => win is { UserId.Length: > 0, PetId.Length: > 0 })
                .Select(win => win with { Login = win.Login ?? string.Empty, DisplayName = win.DisplayName ?? string.Empty })
                .ToList();
            _pending = (saved.Pending ?? [])
                .Where(gift => gift is { WinnerUserId.Length: > 0, RedemptionId.Length: > 0, PetId.Length: > 0 })
                .Select(gift => gift with
                {
                    RewardId = gift.RewardId ?? string.Empty,
                    WinnerLogin = gift.WinnerLogin ?? string.Empty,
                    WinnerName = gift.WinnerName ?? string.Empty
                })
                .ToList();
            _owed = (saved.Owed ?? [])
                .Where(debt => debt is { RedemptionId.Length: > 0, RewardId.Length: > 0 })
                .Select(debt => debt with
                {
                    Channel = debt.Channel ?? string.Empty,
                    ViewerName = debt.ViewerName ?? string.Empty,
                    Reason = debt.Reason ?? string.Empty
                })
                .ToList();
        }
        RecoveredFromBackup = _file.RecoveredFromBackup;
    }

    public string FilePath => _file.FilePath;

    /// <summary>True when the load had to be answered from a copy, so the app can say so.</summary>
    public bool RecoveredFromBackup { get; }

    /// <summary>Why the last copy failed, if it did. The save itself still went through.</summary>
    public string? LastBackupError => _file.LastBackupError;

    /// <summary>Every win, for the settings window. Newest first, because that is the interesting end.</summary>
    public IReadOnlyList<SpinWin> All()
    {
        lock (_lock) return _wins.OrderByDescending(win => win.WonAt).ToArray();
    }

    /// <summary>One viewer's wins, oldest first – the order they built their collection in.</summary>
    public IReadOnlyList<SpinWin> WinsFor(string userId)
    {
        if (userId.Length == 0) return [];
        lock (_lock) return _wins.Where(win => Same(win.UserId, userId)).OrderBy(win => win.WonAt).ToArray();
    }

    public bool Owns(string userId, string petId)
    {
        if (userId.Length == 0 || petId.Length == 0) return false;
        lock (_lock) return _wins.Any(win => Same(win.UserId, userId) && Same(win.PetId, petId));
    }

    /// <summary>How many viewers own this pet. For the settings window and a future !samling.</summary>
    public int OwnerCount(string petId)
    {
        lock (_lock)
            return _wins.Where(win => Same(win.PetId, petId))
                .Select(win => win.UserId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    /// <summary>
    /// A pet was given a new id, so every win and every open gift of it follows along. Answers how
    /// many wins moved.
    ///
    /// <para>The alternative is losing them: a win names its prize by id and nothing else, so the
    /// moment the pet answers to a different one its owners no longer own anything – the pet drops
    /// out of their collection and out of what they may redeem. Ids are matched the way they are
    /// looked up everywhere else, ignoring case, which is why a change of casing alone still counts
    /// as a rename worth writing down.</para>
    /// </summary>
    public int RenamePet(string oldId, string newId)
    {
        if (oldId.Length == 0 || newId.Length == 0 || oldId == newId) return 0;
        lock (_lock)
        {
            int moved = 0;
            for (int i = 0; i < _wins.Count; i++)
            {
                if (!Same(_wins[i].PetId, oldId)) continue;
                _wins[i] = _wins[i] with { PetId = newId };
                moved++;
            }

            bool changed = moved > 0;
            for (int i = 0; i < _pending.Count; i++)
            {
                if (!Same(_pending[i].PetId, oldId)) continue;
                _pending[i] = _pending[i] with { PetId = newId };
                changed = true;
            }

            // Rows left over from an id this pet once had before could turn the move into two copies
            // of the same prize for the same viewer, which every other path here refuses outright.
            if (moved > 0)
            {
                var seen = new HashSet<(string, string)>();
                changed |= _wins.RemoveAll(win =>
                    !seen.Add((win.UserId.ToLowerInvariant(), win.PetId.ToLowerInvariant()))) > 0;
            }

            if (changed) Save();
            return moved;
        }
    }

    /// <summary>
    /// Books one win, and in the same write the fulfilment it leaves owed to Twitch. A second copy
    /// of a pet somebody already owns is refused quietly.
    /// </summary>
    /// <param name="debt">
    /// What the caller is about to tell Twitch about the redemption behind the win, or null when
    /// there is nothing answerable. Written here rather than by a second call because the two are
    /// one fact: a crash between them would leave the prize on disk with no debt beside it, and the
    /// next startup sweep would pay the redemption back on top of a pet the viewer keeps.
    /// </param>
    public void Add(SpinWin win, SpinOwedFulfilment? debt = null)
    {
        lock (_lock)
        {
            // The debt belongs to the redemption rather than to the win, so it goes down even if the
            // win itself turns out to be a copy: the caller raises its verdict either way, and a
            // verdict raised over a debt that was never written is the very gap this closes.
            bool changed = OweLocked(debt);
            if (!_wins.Any(existing => Same(existing.UserId, win.UserId) && Same(existing.PetId, win.PetId)))
            {
                _wins.Add(win);
                changed = true;
            }
            if (TouchLocked(win.UserId, win.Login, win.DisplayName)) changed = true;
            if (changed) Save();
        }
    }

    /// <summary>
    /// A gift landing, in one step: the recipient is checked, the open gift is closed, the win is
    /// booked in their name and the fulfilment owed for it is written down – all under one lock and
    /// one save.
    ///
    /// <para>One step because the gift flow is the one part of the spin that genuinely runs twice at
    /// once: "!ge" waits on Twitch for the name, so two winners can come back from that wait in the
    /// same moment. Done as separate calls both could pass the ownership check and close their own
    /// gifts, and the second win would be swallowed as a duplicate while its redemption was still
    /// answered as delivered and its gift announced – a prize the recipient never got.</para>
    /// </summary>
    public SpinGiftResult TryGive(string redemptionId, SpinWin win, SpinOwedFulfilment? debt)
    {
        lock (_lock)
        {
            // Asked before the gift is touched, so a name that cannot receive the pet leaves the
            // winner their gift and another chance to pick somebody.
            if (_wins.Any(existing => Same(existing.UserId, win.UserId) && Same(existing.PetId, win.PetId)))
                return SpinGiftResult.AlreadyOwned;
            if (_pending.RemoveAll(gift => Same(gift.RedemptionId, redemptionId)) == 0)
                return SpinGiftResult.Gone;

            _wins.Add(win);
            TouchLocked(win.UserId, win.Login, win.DisplayName);
            OweLocked(debt);
            Save();
            return SpinGiftResult.Given;
        }
    }

    /// <summary>
    /// The redemption that bought a win was paid back, so the win goes with it – handed back here so
    /// the caller can say which prize it was. A pet already walking on the lawn is left alone: it is
    /// on a timer of its own and leaves by itself, while this row is the prize proper, the thing that
    /// lets the viewer redeem the pet again for as long as it stands.
    /// </summary>
    public SpinWin? RevokeWin(string redemptionId)
    {
        if (redemptionId.Length == 0) return null;
        lock (_lock)
        {
            int at = _wins.FindIndex(win => win.RedemptionId is { Length: > 0 } bought && Same(bought, redemptionId));
            if (at < 0) return null;
            SpinWin revoked = _wins[at];
            _wins.RemoveAt(at);
            Save();
            return revoked;
        }
    }

    /// <summary>
    /// The viewer was just seen under these names. Saved only when something actually changed, so
    /// commands do not turn into disk writes.
    /// </summary>
    public void Touch(string userId, string login, string displayName)
    {
        lock (_lock)
        {
            if (TouchLocked(userId, login, displayName)) Save();
        }
    }

    /// <summary>
    /// Books an open gift, and hands back the one it pushed out, if there was one – a redemption
    /// still open on Twitch that now belongs to nobody, and whose points its caller owes back.
    /// </summary>
    public SpinPendingGift? AddPending(SpinPendingGift gift)
    {
        lock (_lock)
        {
            // One open gift per winner: the bot's prompt names one prize, and a second would leave
            // "!ge" ambiguous about which pet is being given away.
            SpinPendingGift? displaced = _pending.FirstOrDefault(existing =>
                Same(existing.WinnerUserId, gift.WinnerUserId) && !Same(existing.RedemptionId, gift.RedemptionId));
            _pending.RemoveAll(existing => Same(existing.WinnerUserId, gift.WinnerUserId));
            _pending.Add(gift);
            Save();
            return displaced;
        }
    }

    /// <summary>
    /// The streamer closed this gift's redemption by hand, so its points can no longer come back.
    /// The prize is left where it is: the winner still has a friend to name, and taking the pet away
    /// as well would punish them for something the dashboard did.
    /// </summary>
    public bool MarkUnrefundable(string redemptionId)
    {
        if (redemptionId.Length == 0) return false;
        lock (_lock)
        {
            int at = _pending.FindIndex(gift => Same(gift.RedemptionId, redemptionId));
            if (at < 0 || !_pending[at].Refundable) return false;
            _pending[at] = _pending[at] with { Refundable = false };
            Save();
            return true;
        }
    }

    /// <summary>
    /// Everything waiting on a winner goes, unanswered – the app has left the channel they were won
    /// in, where the redemptions stay in the queue for the streamer or for a later sweep. The wins
    /// themselves are untouched: they are the viewers' property, not the connection's.
    /// </summary>
    public void ClearPending()
    {
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            _pending.Clear();
            Save();
        }
    }

    public SpinPendingGift? PendingFor(string winnerUserId)
    {
        if (winnerUserId.Length == 0) return null;
        lock (_lock) return _pending.FirstOrDefault(gift => Same(gift.WinnerUserId, winnerUserId));
    }

    /// <summary>
    /// Whether a redemption is a gift still waiting for its winner. What keeps the startup sweep
    /// from paying back a redemption whose prize is already won and merely looking for a home.
    /// </summary>
    public bool HoldsPending(string redemptionId)
    {
        if (redemptionId.Length == 0) return false;
        lock (_lock) return _pending.Any(gift => Same(gift.RedemptionId, redemptionId));
    }

    public bool RemovePending(string redemptionId)
    {
        lock (_lock)
        {
            if (_pending.RemoveAll(gift => Same(gift.RedemptionId, redemptionId)) == 0) return false;
            Save();
            return true;
        }
    }

    /// <summary>Writes down a fulfilment owed to Twitch, before anybody sets about delivering it.</summary>
    public void Owe(SpinOwedFulfilment debt)
    {
        lock (_lock)
        {
            if (OweLocked(debt)) Save();
        }
    }

    /// <summary>Twitch took the verdict; the debt is paid and stops shielding the redemption.</summary>
    public bool Settled(string redemptionId)
    {
        if (redemptionId.Length == 0) return false;
        lock (_lock)
        {
            if (_owed.RemoveAll(debt => Same(debt.RedemptionId, redemptionId)) == 0) return false;
            Save();
            return true;
        }
    }

    /// <summary>Whether a fulfilment is still owed on this redemption, and it is therefore not the sweep's.</summary>
    public bool Owes(string redemptionId)
    {
        if (redemptionId.Length == 0) return false;
        lock (_lock) return _owed.Any(debt => Same(debt.RedemptionId, redemptionId));
    }

    /// <summary>
    /// This channel's unpaid fulfilments, counted as tried once more. Anything that has used up its
    /// attempts is dropped here rather than handed back: it can no longer be delivered, and leaving
    /// it would shield its redemption from the sweep for the rest of the app's life.
    /// </summary>
    public IReadOnlyList<SpinOwedFulfilment> ResumeOwed(string channel, int maxAttempts)
    {
        lock (_lock)
        {
            var due = new List<SpinOwedFulfilment>();
            bool changed = false;
            for (int i = _owed.Count - 1; i >= 0; i--)
            {
                SpinOwedFulfilment debt = _owed[i];
                // A debt from another channel waits for that channel: Twitch answers a redemption
                // only where it was made, and trying from here would spend its attempts on refusals.
                if (channel.Length > 0 && debt.Channel.Length > 0 && !Same(debt.Channel, channel)) continue;
                changed = true;
                if (debt.Attempts + 1 >= maxAttempts) { _owed.RemoveAt(i); continue; }
                _owed[i] = debt with { Attempts = debt.Attempts + 1 };
                due.Add(_owed[i]);
            }
            if (changed) Save();
            return due;
        }
    }

    /// <summary>Removes and returns every gift whose winner never answered, so each can be paid back once.</summary>
    public IReadOnlyList<SpinPendingGift> TakeExpired(DateTimeOffset now)
    {
        lock (_lock)
        {
            SpinPendingGift[] expired = _pending.Where(gift => now >= gift.ExpiresAt).ToArray();
            if (expired.Length == 0) return [];
            _pending.RemoveAll(gift => now >= gift.ExpiresAt);
            Save();
            return expired;
        }
    }

    /// <summary>The debt half of a write, so booking one alongside a win stays a single save.</summary>
    private bool OweLocked(SpinOwedFulfilment? debt)
    {
        if (debt is null || debt.RedemptionId.Length == 0 || debt.RewardId.Length == 0) return false;
        _owed.RemoveAll(existing => Same(existing.RedemptionId, debt.RedemptionId));
        _owed.Add(debt);
        return true;
    }

    private bool TouchLocked(string userId, string login, string displayName)
    {
        if (userId.Length == 0) return false;
        bool changed = false;
        for (int i = 0; i < _wins.Count; i++)
        {
            SpinWin win = _wins[i];
            if (!Same(win.UserId, userId)) continue;
            string newLogin = login.Length > 0 ? login : win.Login;
            string newName = displayName.Length > 0 ? displayName : win.DisplayName;
            if (win.Login == newLogin && win.DisplayName == newName) continue;
            _wins[i] = win with { Login = newLogin, DisplayName = newName };
            changed = true;
        }
        return changed;
    }

    private void Save() => _file.Write(new SpinWinFile(_wins, _pending, _owed), JsonOptions);

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>The document on disk. Lists rather than the live fields, so a hand-edit with nulls stays harmless.</summary>
    private sealed record SpinWinFile(List<SpinWin>? Wins, List<SpinPendingGift>? Pending, List<SpinOwedFulfilment>? Owed);
}
