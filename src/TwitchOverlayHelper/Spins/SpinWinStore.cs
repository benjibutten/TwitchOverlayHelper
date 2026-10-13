using System.Text.Json;
using System.Text.Json.Serialization;
using TwitchOverlayHelper.Storage;

namespace TwitchOverlayHelper.Spins;

/// <summary>
/// One pet somebody won in the lucky spin. Keyed by the viewer's Twitch user id, which survives a
/// rename; the names are for display only and are refreshed whenever the person is seen.
/// </summary>
/// <param name="RedemptionId">
/// The redemption that paid for the win, so a refund can take the prize back with it. Null when the
/// file does not record one or the win had no purchase behind it.
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
    /// <summary>
    /// The recipient already has this pet, so nobody receives it – and the gift closes anyway: the
    /// spin bought one chance to place the duplicate.
    /// </summary>
    AlreadyOwned
}

/// <summary>
/// A duplicate win waiting for its winner to name someone to give it to. Persisted, so a crash
/// while the winner is choosing does not lose the chance the redemption bought.
/// </summary>
/// <param name="RedemptionId">
/// The redemption that bought the spin, and the gift's identity. The redemption itself is already
/// answered as delivered.
/// </param>
public sealed record SpinPendingGift(
    string RedemptionId,
    string WinnerUserId,
    string WinnerName,
    string PetId,
    DateTimeOffset ExpiresAt);

/// <summary>
/// A fulfilment this app owes Twitch for a prize it has already handed out. Persisted because the
/// win is on disk the instant it is drawn while the verdict to Twitch only lives in the ledger's
/// memory: closing the app in between would let the next startup sweep refund a redemption whose
/// prize the viewer keeps. The debt is replayed on the next start and shields the redemption from
/// the sweep until then.
/// </summary>
/// <param name="Channel">
/// The broadcaster the debt was run up in. A redemption can only be answered in its own channel, so
/// the debt is only replayed there. Empty matches any channel.
/// </param>
/// <param name="Attempts">
/// How many times it has been replayed across sessions; the cap stops a reward deleted in the
/// dashboard from being retried at every startup.
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
/// Every spin win on disk. Twitch has no record of who won what, so the file cannot be
/// reconstructed: it is written atomically on every change, with a dated copy kept beside each save
/// and recovery from the newest copy that parses.
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
            // A hand-edited file can hold nulls, in the lists and in the fields; a row missing its
            // identifying fields is dropped.
            _wins = (saved.Wins ?? [])
                .Where(win => win is { UserId.Length: > 0, PetId.Length: > 0 })
                .Select(win => win with { Login = win.Login ?? string.Empty, DisplayName = win.DisplayName ?? string.Empty })
                .ToList();
            List<PendingRow> rows = (saved.Pending ?? [])
                .Where(gift => gift is { WinnerUserId.Length: > 0, RedemptionId.Length: > 0, PetId.Length: > 0 })
                .Select(gift => gift with { WinnerName = gift.WinnerName ?? string.Empty })
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
            // Runs after _owed is loaded: it reads the debts and writes to both lists.
            if (Migrate(rows)) Save();
        }
        RecoveredFromBackup = _file.RecoveredFromBackup;
    }

    public string FilePath => _file.FilePath;

    /// <summary>True when the file was unreadable and a backup copy was loaded instead.</summary>
    public bool RecoveredFromBackup { get; }

    /// <summary>Why the last backup copy failed, if it did. The save itself still went through.</summary>
    public string? LastBackupError => _file.LastBackupError;

    /// <summary>Every win, newest first.</summary>
    public IReadOnlyList<SpinWin> All()
    {
        lock (_lock) return _wins.OrderByDescending(win => win.WonAt).ToArray();
    }

    /// <summary>One viewer's wins, oldest first.</summary>
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

    /// <summary>How many viewers own this pet.</summary>
    public int OwnerCount(string petId)
    {
        lock (_lock)
            return _wins.Where(win => Same(win.PetId, petId))
                .Select(win => win.UserId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    /// <summary>
    /// Moves every win and every open gift of a pet to its new id, since a win names its prize by id
    /// alone. Returns how many wins moved. Ids match ignoring case, but a change of casing alone is
    /// still written down.
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

            // A viewer who already had a win under the new id would end up with two copies of the
            // same prize.
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
    /// The fulfilment owed for the redemption behind the win, or null when it cannot be answered.
    /// It is saved even when the win itself is refused as a copy.
    /// </param>
    public void Add(SpinWin win, SpinOwedFulfilment? debt = null)
    {
        lock (_lock)
        {
            // Win and debt go down in one save: a crash between two would let the startup sweep
            // refund a redemption whose prize is kept. The debt belongs to the redemption, so it is
            // written even when the win is a copy – the caller raises its verdict either way.
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
    /// Lands a gift atomically: the open gift is closed, and unless the recipient already owns the
    /// pet the win is booked in their name. Returns <see cref="SpinGiftResult.Gone"/> without
    /// changing anything when the gift is no longer open.
    /// </summary>
    public SpinGiftResult TryGive(string redemptionId, SpinWin win)
    {
        // One lock and one save, because "!ge" awaits Twitch for the recipient: two winners giving
        // the same pet to the same person can arrive together, and checked separately both would
        // pass the ownership test.
        lock (_lock)
        {
            // A gift that is no longer open answers Gone, whoever owns the pet.
            bool owned = _wins.Any(existing => Same(existing.UserId, win.UserId) && Same(existing.PetId, win.PetId));
            if (_pending.RemoveAll(gift => Same(gift.RedemptionId, redemptionId)) == 0)
                return SpinGiftResult.Gone;
            if (owned)
            {
                Save();
                return SpinGiftResult.AlreadyOwned;
            }

            _wins.Add(win);
            TouchLocked(win.UserId, win.Login, win.DisplayName);
            Save();
            return SpinGiftResult.Given;
        }
    }

    /// <summary>
    /// Removes and returns the win a refunded redemption bought, or null when there is none. A pet
    /// already on the lawn is not affected.
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
    /// Updates the names on the viewer's wins. Writes to disk only when one changed.
    /// </summary>
    public void Touch(string userId, string login, string displayName)
    {
        lock (_lock)
        {
            if (TouchLocked(userId, login, displayName)) Save();
        }
    }

    /// <summary>
    /// Books an open gift, and in the same write the fulfilment its redemption leaves owed to
    /// Twitch. A winner has one open gift at a time; returns the one this replaced, if any.
    /// </summary>
    public SpinPendingGift? AddPending(SpinPendingGift gift, SpinOwedFulfilment? debt = null)
    {
        lock (_lock)
        {
            OweLocked(debt);
            // One open gift per winner: a second would leave "!ge" ambiguous about which pet is
            // being given away.
            SpinPendingGift? displaced = _pending.FirstOrDefault(existing =>
                Same(existing.WinnerUserId, gift.WinnerUserId) && !Same(existing.RedemptionId, gift.RedemptionId));
            _pending.RemoveAll(existing => Same(existing.WinnerUserId, gift.WinnerUserId));
            _pending.Add(gift);
            Save();
            return displaced;
        }
    }

    /// <summary>
    /// Drops every open gift, for when the app leaves the channel they were won in. The wins are
    /// untouched.
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

    /// <summary>Whether a redemption is a gift still waiting for its winner.</summary>
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

    /// <summary>Writes down a fulfilment owed to Twitch.</summary>
    public void Owe(SpinOwedFulfilment debt)
    {
        lock (_lock)
        {
            if (OweLocked(debt)) Save();
        }
    }

    /// <summary>Clears the debt on a redemption Twitch has an answer for. Returns true when there was one.</summary>
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

    /// <summary>Whether a fulfilment is still owed on this redemption.</summary>
    public bool Owes(string redemptionId)
    {
        if (redemptionId.Length == 0) return false;
        lock (_lock) return _owed.Any(debt => Same(debt.RedemptionId, redemptionId));
    }

    /// <summary>
    /// This channel's unpaid fulfilments, each counted as tried once more. A debt that has used up
    /// <paramref name="maxAttempts"/> is dropped instead of returned, which also stops it shielding
    /// its redemption from the sweep.
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
                // A debt from another channel waits for that channel: trying from here would spend
                // its attempts on refusals.
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

    /// <summary>Removes and returns every gift whose winner did not answer in time.</summary>
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

    /// <summary>
    /// Takes in the gifts as they were read and converts legacy rows. Returns true when the file
    /// has to be written back.
    ///
    /// <para>A legacy row comes from a file format in which a duplicate's redemption stayed open on
    /// Twitch while its winner chose. One still open keeps its chance and gets the owed fulfilment
    /// written beside it; without that, <see cref="HoldsPending"/> would shield its redemption from
    /// the sweep and nothing would ever answer it. One that has expired is dropped, which leaves its
    /// redemption to the startup sweep to refund.</para>
    /// </summary>
    private bool Migrate(IReadOnlyList<PendingRow> rows)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool rewrite = false;
        foreach (PendingRow row in rows)
        {
            if (!row.Legacy)
            {
                _pending.Add(row.Gift);
                continue;
            }

            // Written back either way, so the legacy row is not converted again on the next load.
            rewrite = true;
            if (now >= row.ExpiresAt) continue;
            _pending.Add(row.Gift);

            // A verdict needs a refundable redemption and its reward id. A debt already on disk is
            // kept: replacing it would reset its attempts.
            if (row.Refundable != true || row.RewardId is not { Length: > 0 } reward) continue;
            if (_owed.Any(debt => Same(debt.RedemptionId, row.RedemptionId))) continue;
            // A legacy row records no channel, so the debt is replayed in whichever channel the app
            // comes back to. Leaving a channel clears its gifts, so that is the channel it was won in.
            _owed.Add(new SpinOwedFulfilment(
                row.RedemptionId, reward, string.Empty, row.WinnerName, row.Cost ?? 0, "dubbletten är bokförd"));
        }
        return rewrite;
    }

    /// <summary>Records the debt without saving. Returns true when something changed.</summary>
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

    private void Save() => _file.Write(
        new SpinWinFile(_wins, _pending.Select(PendingRow.Of).ToList(), _owed), JsonOptions);

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>The document on disk. The lists are nullable because a hand-edited file can omit them.</summary>
    private sealed record SpinWinFile(List<SpinWin>? Wins, List<PendingRow>? Pending, List<SpinOwedFulfilment>? Owed);

    /// <summary>
    /// A gift the way it sits in the file. The last three fields exist only in legacy rows and are
    /// read so <see cref="Migrate"/> can convert them; they are never written.
    /// </summary>
    private sealed record PendingRow(
        string RedemptionId,
        string WinnerUserId,
        string WinnerName,
        string PetId,
        DateTimeOffset ExpiresAt,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RewardId = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Cost = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Refundable = null)
    {
        /// <summary>True when the row carries any of the legacy fields.</summary>
        internal bool Legacy => RewardId is not null || Cost is not null || Refundable is not null;

        internal SpinPendingGift Gift => new(RedemptionId, WinnerUserId, WinnerName, PetId, ExpiresAt);

        internal static PendingRow Of(SpinPendingGift gift) =>
            new(gift.RedemptionId, gift.WinnerUserId, gift.WinnerName, gift.PetId, gift.ExpiresAt);
    }
}
