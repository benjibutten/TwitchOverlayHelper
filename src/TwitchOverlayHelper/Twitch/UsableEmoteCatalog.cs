using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Twitch;

/// <summary>
/// What this account may type into the joined channel, fetched once and kept.
///
/// <para>It serves two needs: the dock's picker needs a list to show, and a line we wrote needs
/// its emotes worked out, because Twitch reports emote positions to everyone except the sender.
/// Resolving them here hands the overlay and the dock the same finished message.</para>
///
/// <para>Only emotes this account can send are ever in here: an emote it cannot send reaches the
/// chat as plain words, and must not be drawn as a picture.</para>
/// </summary>
public sealed class UsableEmoteCatalog(TwitchApiClient api)
{
    // Held for the length of a fetch, so callers asking at once ask Twitch once between them.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // The answer and everything derived from it, as one value. Replaced whole, never edited in
    // place, so the chat client's thread can read it without a lock during a fetch and never
    // sees a name table from a different room than the catalogue.
    private sealed record Held(string Owner, EmoteCatalog Catalog, Dictionary<string, string> ByName);

    private Held? _held;

    // Guards only the swap of _held against _generation, never a network call. Forget runs on
    // the UI thread and must not wait for Twitch, so it takes this rather than _gate.
    private readonly object _swap = new();

    // Counts the times everything was forgotten. A fetch in flight at that moment answers for a
    // room or account we have since left, and must not be published.
    private int _generation;

    /// <summary>The room and account the held answer belongs to; a change in either invalidates it.</summary>
    private static string OwnerOf(string broadcasterId, string userId) => $"{userId}@{broadcasterId}";

    /// <summary>
    /// The catalogue for this channel, fetched if it is not already held. <see cref="SpansIn"/>
    /// answers from what is held without fetching.
    /// </summary>
    public async Task<EmoteCatalog> GetAsync(string broadcasterId, string userId, CancellationToken cancellationToken = default)
    {
        string owner = OwnerOf(broadcasterId, userId);
        if (Volatile.Read(ref _held) is { } already && already.Owner == owner) return already.Catalog;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The caller we queued behind may have fetched this very answer.
            if (Volatile.Read(ref _held) is { } held && held.Owner == owner) return held.Catalog;

            int generation = Volatile.Read(ref _generation);
            EmoteCatalog fetched = await api.GetUsableEmotesAsync(broadcasterId, cancellationToken).ConfigureAwait(false);
            var byName = new Dictionary<string, string>(fetched.Emotes.Count, StringComparer.Ordinal);
            foreach (UsableEmote emote in fetched.Emotes) byName[emote.Name] = emote.Id;

            // Kept only if nothing was forgotten meanwhile, but returned either way: the caller
            // asked about this room.
            lock (_swap)
            {
                if (_generation == generation) Volatile.Write(ref _held, new Held(owner, fetched, byName));
            }
            return fetched;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Forgets everything. Call on a channel change and on logout, so another room's emotes are
    /// not drawn onto our own lines. Does not block on a fetch in flight.
    /// </summary>
    public void Forget()
    {
        lock (_swap)
        {
            _generation++;
            Volatile.Write(ref _held, null);
        }
    }

    /// <summary>
    /// The emotes in a line we wrote. Whole words only, because that is how Twitch matches them:
    /// "Kappa" inside "Kappagrejen" is not an emote. Empty when nothing has been fetched yet.
    /// </summary>
    public IReadOnlyList<EmoteSpan> SpansIn(string text)
    {
        if (Volatile.Read(ref _held) is not { ByName: { Count: > 0 } byName } || string.IsNullOrEmpty(text)) return [];

        List<EmoteSpan>? spans = null;
        int index = 0;
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index])) { index++; continue; }
            int start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index])) index++;

            if (byName.TryGetValue(text[start..index], out string? id))
                (spans ??= []).Add(new EmoteSpan(id, start, index - start));
        }
        return (IReadOnlyList<EmoteSpan>?)spans ?? [];
    }
}
