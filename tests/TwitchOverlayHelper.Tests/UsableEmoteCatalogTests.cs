using System.Net.Http;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Tests;

/// <summary>
/// Twitch decides which words in a message were emotes on its way to the viewers and tells everyone
/// except the sender, so a line we wrote arrives with none. These are the sums that put them back –
/// and they run in the app rather than in one of the views, because the overlay over the game and
/// the dock have to draw the same message.
/// </summary>
public sealed class UsableEmoteCatalogTests
{
    private static async Task<UsableEmoteCatalog> Loaded(params string[] scopes)
    {
        var handler = new EmoteHandler(scopes);
        var client = new HttpClient(handler);
        var store = new TokenStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        store.Save(new StoredCredentials("refresh", "client", "streamern", "42", scopes));
        var catalog = new UsableEmoteCatalog(new TwitchApiClient(client, new TwitchSession(client, store)));
        // Own channel, so the channel's emotes are offered whole and there is one of each kind here.
        await catalog.GetAsync("42", "42");
        return catalog;
    }

    [Fact]
    public async Task FindsAnEmoteStandingOnItsOwn()
    {
        UsableEmoteCatalog catalog = await Loaded(TwitchAuth.EmotesScope);

        EmoteSpan span = Assert.Single(catalog.SpansIn("Kappa"));

        Assert.Equal("c2", span.EmoteId);
        Assert.Equal(0, span.Start);
        Assert.Equal(5, span.Length);
    }

    /// <summary>
    /// Twitch matches emotes on word boundaries, so a name glued to other letters is not one. Getting
    /// this wrong would draw a picture over half a word the viewers saw whole.
    /// </summary>
    [Fact]
    public async Task IgnoresANameThatIsOnlyPartOfAWord()
    {
        UsableEmoteCatalog catalog = await Loaded(TwitchAuth.EmotesScope);

        Assert.Empty(catalog.SpansIn("Kappagrejen"));
        Assert.Empty(catalog.SpansIn("suKappa"));
        Assert.Empty(catalog.SpansIn("hej-Kappa-då"));
    }

    /// <summary>The spans have to line up with the text exactly; the views index straight into it.</summary>
    [Fact]
    public async Task PointsAtEveryEmoteInALineOfMixedWords()
    {
        UsableEmoteCatalog catalog = await Loaded(TwitchAuth.EmotesScope);
        const string line = "hej Kappa på  dig perraLOL";

        IReadOnlyList<EmoteSpan> spans = catalog.SpansIn(line);

        Assert.Equal(2, spans.Count);
        Assert.All(spans, span => Assert.True(line.Substring(span.Start, span.Length) is "Kappa" or "perraLOL"));
        Assert.Equal("Kappa", line.Substring(spans[0].Start, spans[0].Length));
        // Runs of whitespace must not shift the second one: the offsets are counted, not guessed.
        Assert.Equal("perraLOL", line.Substring(spans[1].Start, spans[1].Length));
    }

    /// <summary>
    /// Nothing fetched yet is not the same as "no emotes here": the honest answer is to leave the
    /// line as it was typed, which is how it read before any of this existed.
    /// </summary>
    [Fact]
    public void SaysNothingBeforeAnythingHasBeenFetched()
    {
        var catalog = new UsableEmoteCatalog(new TwitchApiClient(new HttpClient(new EmoteHandler()), null!));

        Assert.Empty(catalog.SpansIn("Kappa"));
    }

    /// <summary>
    /// A channel change makes the held answer wrong, and a wrong answer here would draw the previous
    /// room's emotes onto our own lines.
    /// </summary>
    [Fact]
    public async Task ForgetsEverythingWhenTheRoomChanges()
    {
        UsableEmoteCatalog catalog = await Loaded(TwitchAuth.EmotesScope);
        Assert.NotEmpty(catalog.SpansIn("Kappa"));

        catalog.Forget();

        Assert.Empty(catalog.SpansIn("Kappa"));
    }

    /// <summary>
    /// Forgetting is what a channel switch and a logout both do, and both happen on the UI thread –
    /// while a fetch started by the dock's picker may still be waiting on Twitch. Waiting for that
    /// fetch here would hold the window still for as long as Twitch takes to answer, which on a
    /// connection that has gone quiet is the whole HTTP timeout.
    /// </summary>
    [Fact]
    public async Task ForgettingDoesNotWaitForAFetchThatIsStillRunning()
    {
        var handler = new HeldFetchHandler();
        var client = new HttpClient(handler);
        var store = new TokenStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        store.Save(new StoredCredentials("refresh", "client", "streamern", "42", [TwitchAuth.EmotesScope]));
        var catalog = new UsableEmoteCatalog(new TwitchApiClient(client, new TwitchSession(client, store)));

        Task<EmoteCatalog> fetch = catalog.GetAsync("42", "42");
        await handler.Started.Task;

        Task forgetting = Task.Run(catalog.Forget);
        Assert.Same(forgetting, await Task.WhenAny(forgetting, Task.Delay(TimeSpan.FromSeconds(5))));
        await forgetting;

        handler.Release();
        await fetch;
        // The answer that was on its way is about the room we just left, and drawing another room's
        // emotes onto our own lines is exactly what forgetting was for.
        Assert.Empty(catalog.SpansIn("Kappa"));
    }

    /// <summary>
    /// Answers like <see cref="EmoteHandler"/> does, but holds the first Helix call open until it is
    /// let go – which is the window where a fetch is in flight and no answer has been kept yet.
    /// </summary>
    private sealed class HeldFetchHandler() : DelegatingHandler(new EmoteHandler(TwitchAuth.EmotesScope))
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Not the token calls: those run before the fetch proper and holding them would only
            // stop it from starting.
            if (!request.RequestUri!.Host.Contains("id.twitch.tv", StringComparison.Ordinal))
            {
                Started.TrySetResult();
                await _release.Task.ConfigureAwait(false);
            }
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Only what this account may send is ever in here, so a subscriber emote of a channel we merely
    /// watch cannot be drawn as a picture – the viewers saw loose words, and so should the streamer.
    /// </summary>
    [Fact]
    public async Task NeverDrawsAnEmoteThisAccountCannotSend()
    {
        var handler = new EmoteHandler(TwitchAuth.EmotesScope);
        var client = new HttpClient(handler);
        var store = new TokenStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        store.Save(new StoredCredentials("refresh", "client", "streamern", "42", [TwitchAuth.EmotesScope]));
        var catalog = new UsableEmoteCatalog(new TwitchApiClient(client, new TwitchSession(client, store)));

        // Somebody else's channel, where perraLOL is a sub emote we are not entitled to.
        await catalog.GetAsync("999", "42");

        Assert.Empty(catalog.SpansIn("perraLOL"));
        Assert.NotEmpty(catalog.SpansIn("Kappa"));
    }
}
