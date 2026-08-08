using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Twitch;

/// <summary>
/// What this account may type into the joined channel, fetched once and kept.
///
/// <para>It lives in the app rather than in the dock because two very different things need the
/// same answer. The picker needs a list to show. And a line <em>we</em> wrote needs its emotes
/// worked out: Twitch decides which words in a message were emotes on its way to the viewers and
/// tells everyone except the sender, so our own line comes back with no emote spans at all. Filling
/// them in here means the overlay and the dock are both handed a finished message – if only the
/// dock knew how, the overlay over the game would be the one view still spelling "Kappa" out in
/// letters.</para>
///
/// <para>Only emotes we are sure of are ever in here, which is what makes it safe to draw from: an
/// emote this account cannot send reaches the chat as loose words, and a view showing it as a
/// picture would be telling the streamer something the viewers did not see.</para>
/// </summary>
public sealed class UsableEmoteCatalog(TwitchApiClient api)
{
    /// <summary>
    /// Held only for the length of a fetch, so two callers asking at once – the window when the room
    /// becomes known, the dock when the picker opens – ask Twitch once between them. It is never
    /// taken by anything that runs on the UI thread without awaiting.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// The answer and everything derived from it, as one value. Replaced whole, never edited in
    /// place, so the chat client's thread can read it without a lock while a fetch is running on
    /// another – and can never see a name table belonging to a different room than the catalogue.
    /// </summary>
    private sealed record Held(string Owner, EmoteCatalog Catalog, Dictionary<string, string> ByName);

    private Held? _held;

    /// <summary>
    /// Guards the swap of <see cref="_held"/> against <see cref="_generation"/> and nothing else, so
    /// it is held for a few instructions and never across a network call. <see cref="Forget"/> runs
    /// on the UI thread and must not wait for Twitch – taking <see cref="_gate"/> there would freeze
    /// the window for as long as an in-flight fetch takes to answer or time out.
    /// </summary>
    private readonly object _swap = new();

    /// <summary>
    /// Counts the times everything was forgotten. A fetch that was already on its way when that
    /// happened has an answer for a room or an account we have since left, and publishing it would
    /// put back exactly what <see cref="Forget"/> was called to get rid of.
    /// </summary>
    private int _generation;

    /// <summary>The room and account the held answer belongs to; a change in either invalidates it.</summary>
    private static string OwnerOf(string broadcasterId, string userId) => $"{userId}@{broadcasterId}";

    /// <summary>
    /// The catalogue for this channel, fetched if it is not already held. Callers that only want to
    /// resolve names never need to await this – <see cref="SpansIn"/> answers from what is there.
    /// </summary>
    public async Task<EmoteCatalog> GetAsync(string broadcasterId, string userId, CancellationToken cancellationToken = default)
    {
        string owner = OwnerOf(broadcasterId, userId);
        if (Volatile.Read(ref _held) is { } already && already.Owner == owner) return already.Catalog;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Asked again: the caller we queued behind may have fetched this very answer.
            if (Volatile.Read(ref _held) is { } held && held.Owner == owner) return held.Catalog;

            int generation = Volatile.Read(ref _generation);
            EmoteCatalog fetched = await api.GetUsableEmotesAsync(broadcasterId, cancellationToken).ConfigureAwait(false);
            var byName = new Dictionary<string, string>(fetched.Emotes.Count, StringComparer.Ordinal);
            foreach (UsableEmote emote in fetched.Emotes) byName[emote.Name] = emote.Id;

            // Kept only if it is still about where we are. Handed back either way: the caller asked
            // about this room, and answering it is not the same as drawing from it later.
            lock (_swap)
            {
                if (_generation == generation) Volatile.Write(ref _held, new Held(owner, fetched, byName));
            }
            return fetched;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Forgets everything. A channel change and a logout both make the held answer wrong, and a
    /// wrong answer here is worse than none: it would draw another room's emotes onto our own lines.
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
    /// The emotes in a line we wrote. Whole words only, because that is how Twitch matches them –
    /// "Kappa" inside "Kappagrejen" is six more letters and nothing else.
    ///
    /// <para>Empty when nothing has been fetched yet, which is the honest answer rather than a
    /// guess: the line is then shown as it was typed, exactly as it was before this existed.</para>
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
