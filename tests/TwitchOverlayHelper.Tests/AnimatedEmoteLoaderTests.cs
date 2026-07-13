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
