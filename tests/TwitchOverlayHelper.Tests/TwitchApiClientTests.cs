using System.Net;
using System.Net.Http;
using System.Text;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Tests;

/// <summary>Answers both id.twitch.tv and api.twitch.tv, and keeps the Helix call for inspection.</summary>
internal sealed class HelixHandler : HttpMessageHandler
{
    public HttpMethod? Method { get; private set; }
    public Uri? Url { get; private set; }
    public string? Body { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host.Contains("id.twitch.tv", StringComparison.Ordinal))
        {
            string body = request.RequestUri.AbsolutePath.EndsWith("/validate", StringComparison.Ordinal)
                ? """{"user_id":"42","login":"streamern","scopes":["moderator:manage:chat_messages"]}"""
                : """{"access_token":"token","refresh_token":"refresh","scope":["moderator:manage:chat_messages"]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        Method = request.Method;
        Url = request.RequestUri;
        Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }
}

public sealed class TwitchApiClientTests
{
    private static (TwitchApiClient Api, HelixHandler Handler) LoggedIn()
    {
        var handler = new HelixHandler();
        var client = new HttpClient(handler);
        var store = new TokenStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        store.Save(new StoredCredentials("refresh", "client", "streamern", "42", TwitchAuth.RequiredScopes));
        return (new TwitchApiClient(client, new TwitchSession(client, store)), handler);
    }

    /// <summary>
    /// The one call in this client that is not shaped like its neighbours. Every other write here
    /// posts a JSON body, and pinning was written that way first – Twitch answers that with a 404
    /// that never mentions the verb, so nothing but a test spells out why this one is different.
    /// </summary>
    [Fact]
    public async Task PinsWithAPutAndPutsEverythingInTheQueryString()
    {
        (TwitchApiClient api, HelixHandler handler) = LoggedIn();

        await api.PinMessageAsync("999", "msg-1");

        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal("/helix/chat/pins", handler.Url!.AbsolutePath);
        Assert.Contains("broadcaster_id=999", handler.Url.Query, StringComparison.Ordinal);
        Assert.Contains("message_id=msg-1", handler.Url.Query, StringComparison.Ordinal);
        // The moderator is whoever is logged in, never the channel being watched.
        Assert.Contains("moderator_id=42", handler.Url.Query, StringComparison.Ordinal);
        // A body is not merely unnecessary here; sending one is how the call was broken.
        Assert.Null(handler.Body);
        // A pin stays until it is taken down, so no duration rides along.
        Assert.DoesNotContain("duration", handler.Url.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnpinsWithADeleteNamingTheMessage()
    {
        (TwitchApiClient api, HelixHandler handler) = LoggedIn();

        await api.UnpinMessageAsync("999", "msg-1");

        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Equal("/helix/chat/pins", handler.Url!.AbsolutePath);
        Assert.Contains("message_id=msg-1", handler.Url.Query, StringComparison.Ordinal);
    }

    // Twitch would answer a missing id with a 400, but the round trip only tells us what we knew.
    [Fact]
    public async Task RefusesToPinAMessageWithoutAnIdWithoutCallingTwitch()
    {
        (TwitchApiClient api, HelixHandler handler) = LoggedIn();

        await Assert.ThrowsAsync<TwitchApiException>(() => api.PinMessageAsync("999", "  "));
        await Assert.ThrowsAsync<TwitchApiException>(() => api.UnpinMessageAsync("999", ""));

        Assert.Null(handler.Method);
    }
}
