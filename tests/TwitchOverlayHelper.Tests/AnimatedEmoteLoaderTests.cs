using System.Net;
using TwitchOverlayHelper.Overlay;

namespace TwitchOverlayHelper.Tests;

public sealed class AnimatedEmoteLoaderTests
{
    private static readonly byte[] ValidGif = "GIF89a-test-animation"u8.ToArray();

    [Fact]
    public async Task ConcurrentRequestsAreDownloadedOnce()
    {
        var handler = new StubHandler((_, _) => GifResponse());
        var loader = new AnimatedEmoteLoader(new HttpClient(handler));

        Task<byte[]?>[] requests = Enumerable.Range(0, 12)
            .Select(_ => loader.GetAnimationAsync("animated-id"))
            .ToArray();
        byte[]?[] results = await Task.WhenAll(requests);

        Assert.Equal(1, handler.RequestCount);
        Assert.All(results, bytes => Assert.Equal(ValidGif, bytes));
    }

    [Fact]
    public async Task NotFoundIsNegativelyCached()
    {
        var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        var loader = new AnimatedEmoteLoader(new HttpClient(handler));

        Assert.Null(await loader.GetAnimationAsync("static-id"));
        Assert.Null(await loader.GetAnimationAsync("static-id"));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task TransientFailureIsRetried()
    {
        var handler = new StubHandler((requestNumber, _) => requestNumber == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : GifResponse());
        var loader = new AnimatedEmoteLoader(new HttpClient(handler));

        Assert.Null(await loader.GetAnimationAsync("eventually-animated"));
        Assert.Equal(ValidGif, await loader.GetAnimationAsync("eventually-animated"));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task NonGifResponseIsNegativelyCached()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("not-a-gif"u8.ToArray())
        });
        var loader = new AnimatedEmoteLoader(new HttpClient(handler));

        Assert.Null(await loader.GetAnimationAsync("invalid-animation"));
        Assert.Null(await loader.GetAnimationAsync("invalid-animation"));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task DecoderFailureCanMarkAnimationUnavailable()
    {
        var handler = new StubHandler((_, _) => GifResponse());
        var loader = new AnimatedEmoteLoader(new HttpClient(handler));

        Assert.Equal(ValidGif, await loader.GetAnimationAsync("bad-gif"));
        loader.MarkUnavailable("bad-gif");
        Assert.Null(await loader.GetAnimationAsync("bad-gif"));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task OversizedAnimationIsRejectedBeforeDownload()
    {
        var handler = new StubHandler((_, _) =>
        {
            var content = new ByteArrayContent(ValidGif);
            content.Headers.ContentLength = AnimatedEmoteLoader.MaxAnimationBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var loader = new AnimatedEmoteLoader(new HttpClient(handler));

        Assert.Null(await loader.GetAnimationAsync("too-large"));
        Assert.Equal(1, handler.RequestCount);
    }

    /// <summary>
    /// A decode failure can be reported once per message carrying the emote, and the same handful of
    /// emotes come round all evening. The bookkeeping has to follow how many emotes are cached, not
    /// how many times it was told about them.
    /// </summary>
    [Fact]
    public void MarkingTheSameEmoteUnavailableRepeatedlyDoesNotAccumulate()
    {
        var loader = new AnimatedEmoteLoader(new HttpClient(new StubHandler((_, _) => GifResponse())));

        for (int i = 0; i < 1000; i++) loader.MarkUnavailable("broken-emote");

        Assert.Equal(1, loader.TrackedOrderEntries);
    }

    /// <summary>
    /// The other way an entry leaves without the cache shrinking: a transient failure is evicted so a
    /// later message retries. A bad network stretch used to leave one dead entry behind per attempt.
    /// </summary>
    [Fact]
    public async Task EvictedFailuresDoNotAccumulate()
    {
        var loader = new AnimatedEmoteLoader(new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));

        for (int i = 0; i < 500; i++) Assert.Null(await loader.GetAnimationAsync($"emote-{i}"));

        // At most the one just evicted, which the next insertion drops.
        Assert.True(loader.TrackedOrderEntries <= 1, $"kön höll {loader.TrackedOrderEntries} poster");
    }

    /// <summary>
    /// A download can finish after the entry that started it has left the cache: a decode failure
    /// marks the emote unavailable while the bytes for another copy of it are still on the wire.
    /// Those bytes belong to nothing the cache is holding, and booking them against whoever holds
    /// the id by then would leave the budget counting an animation that is not there – for good,
    /// because the entry it was charged to has a size of its own to give back and not that one.
    /// </summary>
    [Fact]
    public async Task ADownloadFinishingAfterItsEntryWasReplacedIsNotCounted()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var handler = new StubHandler((_, _) =>
        {
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(BigGif(4096))
            };
        });
        var loader = new AnimatedEmoteLoader(new HttpClient(handler));

        // On its own thread, because the stub blocks until it is released. Waiting for the request
        // to actually be in the handler is what makes the overtaking below deterministic: without
        // it the mark can land before the entry exists, and then there is no download to overtake.
        Task<byte[]?> inFlight = Task.Run(() => loader.GetAnimationAsync("overtaken"));
        await started.Task;

        // Overtaken: the decode of an earlier copy of this emote failed while these bytes were still
        // on the wire, and the entry that started them is no longer the cached one.
        loader.MarkUnavailable("overtaken");

        release.SetResult();
        // The caller that asked still gets its animation – only the cache copy is disowned.
        Assert.Equal(4096, (await inFlight)!.Length);

        // The cache holds the "unavailable" entry, which is worth nothing.
        Assert.Equal(0, loader.TrackedBytes);
        // And the emote still reads as unavailable rather than being resurrected by the late bytes.
        Assert.Null(await loader.GetAnimationAsync("overtaken"));
    }

    /// <summary>The size a download books has to be given back when its entry is evicted.</summary>
    [Fact]
    public async Task EvictingACountedEntryGivesItsBytesBack()
    {
        var loader = new AnimatedEmoteLoader(
            new HttpClient(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(BigGif(4096))
            })));

        Assert.Equal(4096, (await loader.GetAnimationAsync("counted"))!.Length);
        Assert.Equal(4096, loader.TrackedBytes);

        loader.MarkUnavailable("counted");
        Assert.Equal(0, loader.TrackedBytes);
    }

    private static byte[] BigGif(int length)
    {
        byte[] bytes = new byte[length];
        Gif89aHeader.CopyTo(bytes.AsSpan());
        return bytes;
    }

    private static readonly byte[] Gif89aHeader = "GIF89a"u8.ToArray();

    /// <summary>The cap still evicts, and the oldest is what goes.</summary>
    [Fact]
    public async Task TheOldestEntryIsEvictedOnceTheCacheIsFull()
    {
        var handler = new StubHandler((_, _) => GifResponse());
        var loader = new AnimatedEmoteLoader(new HttpClient(handler));

        for (int i = 0; i < 300; i++) await loader.GetAnimationAsync($"emote-{i}");
        int afterFilling = handler.RequestCount;

        // The newest is still held; the first one was pushed out and has to be fetched again.
        await loader.GetAnimationAsync("emote-299");
        Assert.Equal(afterFilling, handler.RequestCount);

        await loader.GetAnimationAsync("emote-0");
        Assert.Equal(afterFilling + 1, handler.RequestCount);
    }

    private static HttpResponseMessage GifResponse() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(ValidGif)
    };

    private sealed class StubHandler(
        Func<int, HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int requestNumber = Interlocked.Increment(ref _requestCount);
            return Task.FromResult(responseFactory(requestNumber, request));
        }
    }
}
