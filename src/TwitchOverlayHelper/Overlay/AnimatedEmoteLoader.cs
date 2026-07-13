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
    private static readonly byte[] Gif87Header = "GIF87a"u8.ToArray();
    private static readonly byte[] Gif89Header = "GIF89a"u8.ToArray();

    private readonly HttpClient _httpClient;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, Lazy<Task<byte[]?>>> _cache = new(StringComparer.Ordinal);
    private readonly Queue<(string Id, Lazy<Task<byte[]?>> Entry)> _insertionOrder = new();

    public AnimatedEmoteLoader(HttpClient httpClient) => _httpClient = httpClient;

    public Task<byte[]?> GetAnimationAsync(string emoteId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emoteId)) return Task.FromResult<byte[]?>(null);

        Lazy<Task<byte[]?>> entry;
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(emoteId, out entry!))
            {
                TrimCache();
                entry = new Lazy<Task<byte[]?>>(
                    () => DownloadAnimationAsync(emoteId),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _cache.Add(emoteId, entry);
                _insertionOrder.Enqueue((emoteId, entry));
            }
        }

        return AwaitEntryAsync(emoteId, entry, cancellationToken);
    }

    public void MarkUnavailable(string emoteId)
    {
        lock (_cacheLock)
        {
            if (!_cache.ContainsKey(emoteId)) TrimCache();
            var unavailable = new Lazy<Task<byte[]?>>(
                () => Task.FromResult<byte[]?>(null),
                LazyThreadSafetyMode.ExecutionAndPublication);
            _cache[emoteId] = unavailable;
            _insertionOrder.Enqueue((emoteId, unavailable));
        }
    }

    private async Task<byte[]?> AwaitEntryAsync(
        string emoteId,
        Lazy<Task<byte[]?>> entry,
        CancellationToken cancellationToken)
    {
        try
        {
            return await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<byte[]?> DownloadAnimationAsync(string emoteId)
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
        return HasGifHeader(bytes) ? bytes : null;
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

    private void EvictIfCurrent(string emoteId, Lazy<Task<byte[]?>> entry)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(emoteId, out Lazy<Task<byte[]?>>? current)
                && ReferenceEquals(current, entry))
                _cache.Remove(emoteId);
        }
    }

    private void TrimCache()
    {
        while (_cache.Count >= MaxCacheEntries
               && _insertionOrder.TryDequeue(out (string Id, Lazy<Task<byte[]?>> Entry) oldest))
        {
            if (_cache.TryGetValue(oldest.Id, out Lazy<Task<byte[]?>>? current)
                && ReferenceEquals(current, oldest.Entry))
                _cache.Remove(oldest.Id);
        }

        if (_cache.Count >= MaxCacheEntries)
            _cache.Remove(_cache.Keys.First());
    }
}
