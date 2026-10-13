using System.IO;
using System.Net;
using System.Net.Http;

namespace TwitchOverlayHelper.Overlay;

/// <summary>
/// Downloads and deduplicates animated GIFs: Twitch's animated emote variants, and the GIFs tier 2
/// and tier 3 subscribers send into chat. A null result means there is no usable animation at that
/// address and is safe to cache negatively. Transient network failures are deliberately evicted so
/// a later message retries.
///
/// <para>The limits are constructor arguments because the two uses differ: an emote is a small
/// picture on a fixed CDN, while a chat GIF is somebody else's file and routinely a few megabytes.</para>
/// </summary>
internal sealed class AnimatedEmoteLoader
{
    internal const int MaxAnimationBytes = 2 * 1024 * 1024;
    private const int MaxCacheEntries = 256;

    /// <summary>
    /// The byte ceiling of the cache. The entry limit alone would allow half a gigabyte – 256
    /// animations at the per-animation limit – on the machine that is also playing the game and
    /// encoding the stream. Whichever ceiling is reached first evicts.
    /// </summary>
    internal const long DefaultMaxCacheBytes = 48L * 1024 * 1024;

    private static readonly byte[] Gif87Header = "GIF87a"u8.ToArray();
    private static readonly byte[] Gif89Header = "GIF89a"u8.ToArray();

    private readonly HttpClient _httpClient;
    private readonly int _maxAnimationBytes;
    private readonly long _maxCacheBytes;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Insertion order, for evicting the oldest. Holds one entry per insertion where the cache
    /// holds one per id, so entries in here go stale – see <see cref="DropStaleOrder"/>.
    /// </summary>
    private readonly Queue<(string Id, Entry Entry)> _insertionOrder = new();

    private long _cachedBytes;

    public AnimatedEmoteLoader(
        HttpClient httpClient,
        int maxAnimationBytes = MaxAnimationBytes,
        long maxCacheBytes = DefaultMaxCacheBytes)
    {
        _httpClient = httpClient;
        _maxAnimationBytes = maxAnimationBytes;
        _maxCacheBytes = maxCacheBytes;
    }

    /// <summary>
    /// One id's animation, and what it is counted as holding.
    ///
    /// <para>An object rather than a bare <see cref="Lazy{T}"/> so a finished download can be booked
    /// against the very entry that started it. A download can outlive its entry, and booking by id
    /// would charge its bytes to whatever entry holds the id by the time it lands.</para>
    /// </summary>
    private sealed class Entry
    {
        /// <summary>The download, started at most once however many messages ask for it.</summary>
        public Lazy<Task<byte[]?>> Animation { get; set; } = null!;

        /// <summary>What this one is counted as. Zero until its download has finished.</summary>
        public int Size { get; set; }
    }

    /// <summary>
    /// How many entries the eviction queue is holding. Read by the tests that guard it tracking the
    /// cache rather than the number of times the cache was written to.
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
        return GetAsync(
            emoteId,
            $"https://static-cdn.jtvnw.net/emoticons/v2/{Uri.EscapeDataString(emoteId)}/animated/dark/2.0",
            cancellationToken);
    }

    /// <summary>
    /// One chat GIF, fetched from the address Twitch handed over and cached under it: two messages
    /// carrying the same GIF carry the same URL.
    /// </summary>
    public Task<byte[]?> GetGifAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<byte[]?>(null);
        return GetAsync(url, url, cancellationToken);
    }

    private Task<byte[]?> GetAsync(string key, string url, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(key, out entry!))
            {
                // Built before the Lazy so the download can close over it. The factory only runs
                // when the first caller reads Value, which is below and outside this lock.
                entry = new Entry();
                entry.Animation = new Lazy<Task<byte[]?>>(
                    () => DownloadAnimationAsync(key, url, entry),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                Remember(key, entry);
            }
        }

        return AwaitEntryAsync(key, entry, cancellationToken);
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

    private async Task<byte[]?> DownloadAnimationAsync(string key, string url, Entry entry)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > _maxAnimationBytes) return null;
        await using Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        byte[] bytes = await ReadWithLimitAsync(source, _maxAnimationBytes).ConfigureAwait(false);
        byte[]? animation = HasGifHeader(bytes) ? bytes : null;
        // With the size known the byte ceiling applies. Whatever this download pushed over the limit
        // is evicted, oldest first – possibly this one, which the caller receives all the same.
        RecordSize(key, entry, animation?.Length ?? 0);
        return animation;
    }

    private static async Task<byte[]> ReadWithLimitAsync(Stream source, int limit)
    {
        using var destination = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) return destination.ToArray();
            if (destination.Length + read > limit) return [];
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
    /// Books a finished download against the entry that started it, if that entry is the current
    /// one for its id.
    /// </summary>
    private void RecordSize(string emoteId, Entry entry, int size)
    {
        lock (_cacheLock)
        {
            // The entry may have been evicted or replaced while the bytes were on the wire. Charging
            // them to whoever holds the id by then would count an animation the cache does not hold.
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
    /// Evicts down to the ceilings, and drops the entries in <see cref="_insertionOrder"/> that
    /// <see cref="EvictIfCurrent"/> and repeated <see cref="MarkUnavailable"/> calls left stale.
    /// </summary>
    private void TrimCache()
    {
        // On every trim, not only when the cache is full: a loader that never fills up – a channel
        // with a few dozen emotes – would otherwise grow the queue without bound.
        DropStaleOrder();
        while ((_cache.Count > MaxCacheEntries || _cachedBytes > _maxCacheBytes)
               && _insertionOrder.TryDequeue(out (string Id, Entry Entry) oldest))
        {
            if (IsCurrent(oldest)) Forget(oldest.Id);
            DropStaleOrder();
        }

        // The head drain cannot reach a stale entry sitting behind a current one, so an oversized
        // queue gets one filtering pass.
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
