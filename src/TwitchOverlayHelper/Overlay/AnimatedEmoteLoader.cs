using System.IO;
using System.Net;
using System.Net.Http;

namespace TwitchOverlayHelper.Overlay;

/// <summary>
/// Downloads and deduplicates Twitch's animated GIF variants. A null result means
/// that the emote has no usable animated variant and is safe to cache negatively.
/// Transient network failures are deliberately evicted so a later message retries.
/// </summary>
internal sealed class AnimatedEmoteLoader
{
    internal const int MaxAnimationBytes = 2 * 1024 * 1024;
    private const int MaxCacheEntries = 256;

    /// <summary>
    /// What the cache may actually hold. Counting entries is the wrong measure on its own: 256 of
    /// them at the per-animation limit is half a gigabyte, and this runs on the machine that is
    /// also playing the game and encoding the stream. Whichever ceiling is reached first evicts.
    /// </summary>
    private const long MaxCacheBytes = 48L * 1024 * 1024;

    private static readonly byte[] Gif87Header = "GIF87a"u8.ToArray();
    private static readonly byte[] Gif89Header = "GIF89a"u8.ToArray();

    private readonly HttpClient _httpClient;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Insertion order, for evicting the oldest. Holds one entry per insertion where the cache
    /// holds one per id, so entries in here go stale – see <see cref="DropStaleOrder"/>.
    /// </summary>
    private readonly Queue<(string Id, Entry Entry)> _insertionOrder = new();

    private long _cachedBytes;

    public AnimatedEmoteLoader(HttpClient httpClient) => _httpClient = httpClient;

    /// <summary>
    /// One id's animation, and what it is counted as holding.
    ///
    /// <para>An object rather than a bare <see cref="Lazy{T}"/> with the size kept alongside so the
    /// download can be handed the very entry it belongs to. Booking a finished download against the
    /// id alone is wrong: a download can outlive the entry that started it – the emote is marked
    /// unavailable while it is still in flight, or it is evicted and asked for again – and its bytes
    /// would then be charged to whatever entry holds the id by the time it lands. The budget would
    /// drift away from what is actually cached, in either direction, and never come back.</para>
    /// </summary>
    private sealed class Entry
    {
        /// <summary>The download, started at most once however many messages ask for it.</summary>
        public Lazy<Task<byte[]?>> Animation { get; set; } = null!;

        /// <summary>What this one is counted as. Zero until its download has finished.</summary>
        public int Size { get; set; }
    }

    /// <summary>
    /// How many entries the eviction queue is holding. Only the tests read it, and what they are
    /// guarding is that it tracks the cache rather than the number of times the cache was written
    /// to – which is what it used to do.
    /// </summary>
    internal int TrackedOrderEntries
    {
        get { lock (_cacheLock) return _insertionOrder.Count; }
    }

    /// <summary>
    /// What the cache is counted as holding. Read by the tests that guard the byte budget against
    /// downloads finishing after the entry that started them has gone.
    /// </summary>
    internal long TrackedBytes
    {
        get { lock (_cacheLock) return _cachedBytes; }
    }

    public Task<byte[]?> GetAnimationAsync(string emoteId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emoteId)) return Task.FromResult<byte[]?>(null);

        Entry entry;
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(emoteId, out entry!))
            {
                // Built before the Lazy so the download can close over it. The factory only runs
                // when the first caller reads Value, which is below and outside this lock.
                entry = new Entry();
                entry.Animation = new Lazy<Task<byte[]?>>(
                    () => DownloadAnimationAsync(emoteId, entry),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                Remember(emoteId, entry);
            }
        }

        return AwaitEntryAsync(emoteId, entry, cancellationToken);
    }

    public void MarkUnavailable(string emoteId)
    {
        lock (_cacheLock)
        {
            // An id can be marked more than once – several messages carrying the same broken emote
            // can each fail their decode before the first mark lands. Each mark replaces the cached
            // entry, which leaves the previous one stale in the queue; TrimCache drops it.
            Remember(emoteId, new Entry
            {
                Animation = new Lazy<Task<byte[]?>>(
                    () => Task.FromResult<byte[]?>(null),
                    LazyThreadSafetyMode.ExecutionAndPublication)
            });
        }
    }

    private async Task<byte[]?> AwaitEntryAsync(
        string emoteId,
        Entry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            return await entry.Animation.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            EvictIfCurrent(emoteId, entry);
            return null;
        }
        catch (TaskCanceledException)
        {
            EvictIfCurrent(emoteId, entry);
            return null;
        }
        catch (IOException)
        {
            EvictIfCurrent(emoteId, entry);
            return null;
        }
    }

    private async Task<byte[]?> DownloadAnimationAsync(string emoteId, Entry entry)
    {
        string escapedId = Uri.EscapeDataString(emoteId);
        string url = $"https://static-cdn.jtvnw.net/emoticons/v2/{escapedId}/animated/dark/2.0";
        using HttpResponseMessage response = await _httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxAnimationBytes) return null;
        await using Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        byte[] bytes = await ReadWithLimitAsync(source).ConfigureAwait(false);
        byte[]? animation = HasGifHeader(bytes) ? bytes : null;
        // Now that the size is known the byte ceiling can be applied. Anything this download pushed
        // over the limit goes, oldest first – possibly including this one, which is the right
        // answer: it has already been handed to the caller and only the cache copy is dropped.
        RecordSize(emoteId, entry, animation?.Length ?? 0);
        return animation;
    }

    private static async Task<byte[]> ReadWithLimitAsync(Stream source)
    {
        using var destination = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) return destination.ToArray();
            if (destination.Length + read > MaxAnimationBytes) return [];
            destination.Write(buffer, 0, read);
        }
    }

    private static bool HasGifHeader(byte[] bytes) =>
        bytes.AsSpan().StartsWith(Gif87Header) || bytes.AsSpan().StartsWith(Gif89Header);

    /// <summary>Puts an entry in as the current one for its id, and trims back to the ceilings.</summary>
    private void Remember(string emoteId, Entry entry)
    {
        Forget(emoteId);
        _cache[emoteId] = entry;
        _insertionOrder.Enqueue((emoteId, entry));
        TrimCache();
    }

    /// <summary>
    /// Books a finished download against the entry that started it. The identity check is the whole
    /// point: this runs on a continuation, so the entry may have been evicted or replaced while the
    /// bytes were on the wire – by a decode failure marking the emote unavailable, or by a retry
    /// after a transient error. Charging those bytes to whoever holds the id now would leave the
    /// budget counting an animation the cache is not holding, and it would never be given back.
    /// </summary>
    private void RecordSize(string emoteId, Entry entry, int size)
    {
        lock (_cacheLock)
        {
            if (!IsCurrent((emoteId, entry))) return;
            _cachedBytes += size - entry.Size;
            entry.Size = size;
            TrimCache();
        }
    }

    private void EvictIfCurrent(string emoteId, Entry entry)
    {
        lock (_cacheLock)
        {
            if (IsCurrent((emoteId, entry))) Forget(emoteId);
        }
    }

    /// <summary>Drops one id from the cache and gives its bytes back to the budget.</summary>
    private void Forget(string emoteId)
    {
        if (_cache.Remove(emoteId, out Entry? gone)) _cachedBytes -= gone.Size;
    }

    /// <summary>
    /// Makes room, and – just as importantly – keeps <see cref="_insertionOrder"/> from growing on
    /// its own. The queue holds one entry per insertion where the cache holds one per id, so every
    /// eviction by <see cref="EvictIfCurrent"/> and every repeated <see cref="MarkUnavailable"/>
    /// leaves an entry in here that no longer names anything. Those used to be dropped only while
    /// the cache was full, which meant a loader that never filled up – the ordinary case, a channel
    /// with a few dozen emotes – grew a queue that never shrank.
    /// </summary>
    private void TrimCache()
    {
        DropStaleOrder();
        while ((_cache.Count > MaxCacheEntries || _cachedBytes > MaxCacheBytes)
               && _insertionOrder.TryDequeue(out (string Id, Entry Entry) oldest))
        {
            if (IsCurrent(oldest)) Forget(oldest.Id);
            DropStaleOrder();
        }

        // A stale entry sitting behind a current one is only reached once that current one is
        // itself replaced, which happens in every pattern worth worrying about but is not something
        // the head drain above can promise. One filtering pass settles it outright.
        if (_insertionOrder.Count > MaxCacheEntries * 2) CompactOrder();
    }

    /// <summary>Throws away queue entries at the front that no longer name anything cached.</summary>
    private void DropStaleOrder()
    {
        while (_insertionOrder.TryPeek(out (string Id, Entry Entry) oldest) && !IsCurrent(oldest))
            _insertionOrder.Dequeue();
    }

    /// <summary>Rebuilds the queue with the stale entries left out, keeping the order of the rest.</summary>
    private void CompactOrder()
    {
        (string Id, Entry Entry)[] all = _insertionOrder.ToArray();
        _insertionOrder.Clear();
        foreach ((string Id, Entry Entry) entry in all)
            if (IsCurrent(entry)) _insertionOrder.Enqueue(entry);
    }

    private bool IsCurrent((string Id, Entry Entry) candidate) =>
        _cache.TryGetValue(candidate.Id, out Entry? current)
        && ReferenceEquals(current, candidate.Entry);
}
