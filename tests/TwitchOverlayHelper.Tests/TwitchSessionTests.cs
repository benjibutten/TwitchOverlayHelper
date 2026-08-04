using System.Net;
using System.Net.Http;
using System.Text;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Tests;

/// <summary>Answers id.twitch.tv, with a gate so a test can hold a refresh open.</summary>
internal sealed class TwitchAuthHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _tokenResponses = new();

    public TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool GateRefresh { get; init; }
    public int TokenCalls { get; private set; }

    public void QueueTokenResponse(HttpStatusCode status, string body) => _tokenResponses.Enqueue((status, body));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath;

        if (path.EndsWith("/validate", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, """{"user_id":"42","login":"streamern","scopes":["chat:read"]}""");

        if (path.EndsWith("/revoke", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.OK);

        TokenCalls++;
        if (GateRefresh)
        {
            RefreshStarted.TrySetResult();
            await ReleaseRefresh.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        (HttpStatusCode status, string body) = _tokenResponses.Count > 0
            ? _tokenResponses.Dequeue()
            : (HttpStatusCode.OK, """{"access_token":"ny-token","refresh_token":"ny-refresh","scope":["chat:read"]}""");
        return Json(status, body);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

public sealed class TwitchSessionTests
{
    private static (TwitchSession Session, TokenStore Store) LoggedIn(HttpClient client)
    {
        var store = new TokenStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        store.Save(new StoredCredentials("gammal-refresh", "client", "streamern", "42", TwitchAuth.RequiredScopes));
        return (new TwitchSession(client, store), store);
    }

    // Logging out has to stick. A refresh that was already talking to Twitch when the user signed
    // out would otherwise write the credentials straight back into the store.
    [Fact]
    public async Task ARefreshThatLandsAfterLogoutDoesNotSignTheUserBackIn()
    {
        var handler = new TwitchAuthHandler { GateRefresh = true };
        using var client = new HttpClient(handler);
        (TwitchSession session, TokenStore store) = LoggedIn(client);

        Task<string> refresh = session.GetAccessTokenAsync();
        await handler.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await session.LogoutAsync();
        handler.ReleaseRefresh.TrySetResult();

        await Assert.ThrowsAsync<TwitchAuthException>(() => refresh);
        Assert.False(session.IsLoggedIn);
        Assert.Null(store.Load());
        session.Dispose();
    }

    [Fact]
    public async Task RefreshesTheTokenAndKeepsTheNewRefreshToken()
    {
        var handler = new TwitchAuthHandler();
        using var client = new HttpClient(handler);
        (TwitchSession session, TokenStore store) = LoggedIn(client);

        Assert.Equal("ny-token", await session.GetAccessTokenAsync());
        Assert.Equal("ny-refresh", store.Load()!.RefreshToken);

        // The second call is inside the validation window and must not spend another round trip.
        Assert.Equal("ny-token", await session.GetAccessTokenAsync());
        Assert.Equal(1, handler.TokenCalls);
        session.Dispose();
    }
}

public sealed class TwitchAuthTests
{
    // Twitch reports rate limiting the OAuth way, as "slow_down". Treating it as a hard error
    // would abandon a login that only needed to be polled a little slower.
    [Fact]
    public async Task BacksOffOnSlowDownInsteadOfAbandoningTheLogin()
    {
        var handler = new TwitchAuthHandler();
        handler.QueueTokenResponse(HttpStatusCode.BadRequest, """{"status":400,"message":"slow_down"}""");
        handler.QueueTokenResponse(HttpStatusCode.OK, """{"access_token":"token","refresh_token":"refresh","scope":["chat:read"]}""");
        using var client = new HttpClient(handler);
        var auth = new TwitchAuth(client);

        // Interval 0 keeps the test quick; the back-off after slow_down is what adds the only wait.
        var prompt = new DeviceCodePrompt("device", "ABCD-1234", "https://twitch.tv/activate", 60, 0);
        TwitchTokens tokens = await auth.AwaitApprovalAsync("client", prompt);

        Assert.Equal("token", tokens.AccessToken);
        Assert.Equal(2, handler.TokenCalls);
    }

    [Fact]
    public async Task KeepsWaitingWhileTheUserHasNotApprovedYet()
    {
        var handler = new TwitchAuthHandler();
        handler.QueueTokenResponse(HttpStatusCode.BadRequest, """{"status":400,"message":"authorization_pending"}""");
        handler.QueueTokenResponse(HttpStatusCode.OK, """{"access_token":"token","refresh_token":"refresh","scope":[]}""");
        using var client = new HttpClient(handler);
        var auth = new TwitchAuth(client);

        TwitchTokens tokens = await auth.AwaitApprovalAsync("client", new DeviceCodePrompt("device", "ABCD", "uri", 60, 0));

        Assert.Equal("token", tokens.AccessToken);
    }
}
