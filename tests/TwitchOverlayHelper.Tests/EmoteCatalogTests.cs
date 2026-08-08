using System.Net;
using System.Net.Http;
using System.Text;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Tests;

/// <summary>
/// Answers the three emote endpoints with canned lists and writes down which ones were asked for.
/// The user list is paged, because that is the only one that can be long enough to page.
/// </summary>
internal sealed class EmoteHandler(params string[] scopes) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    /// <summary>Turned on to make the personal call fail the way a withdrawn scope does.</summary>
    public bool RefuseUserEmotes { get; set; }

    /// <summary>Turned on to make the personal list longer than the client's page cap.</summary>
    public bool EndlessUserEmotes { get; set; }

    private int _page;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri url = request.RequestUri!;
        // A Helix call refreshes the token first, and the validate answer is what the session then
        // believes about its own scopes – so the grant has to be told here, not only in the store.
        if (url.Host.Contains("id.twitch.tv", StringComparison.Ordinal))
        {
            string granted = string.Join(",", scopes.Select(scope => $"\"{scope}\""));
            return Task.FromResult(Json(url.AbsolutePath.EndsWith("/validate", StringComparison.Ordinal)
                ? $$"""{"user_id":"42","login":"streamern","scopes":[{{granted}}]}"""
                : $$"""{"access_token":"token","refresh_token":"refresh","scope":[{{granted}}]}"""));
        }

        Requests.Add(url.PathAndQuery);

        if (url.AbsolutePath == "/helix/chat/emotes/global")
            return Task.FromResult(Json("""{"data":[{"id":"g1","name":"Kappa"},{"id":"g2","name":"PogChamp"}]}"""));

        if (url.AbsolutePath == "/helix/chat/emotes")
            // Shares a name with the global list on purpose: the channel is the one that should keep it.
            return Task.FromResult(Json("""{"data":[{"id":"c1","name":"perraLOL"},{"id":"c2","name":"Kappa"}]}"""));

        if (url.AbsolutePath == "/helix/chat/emotes/user")
        {
            if (RefuseUserEmotes) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"message":"Missing scope"}""", Encoding.UTF8, "application/json")
            });

            if (EndlessUserEmotes)
            {
                int page = ++_page;
                return Task.FromResult(Json($$$"""{"data":[{"id":"p{{{page}}}","name":"sida{{{page}}}"}],"pagination":{"cursor":"nasta"}}"""));
            }

            return Task.FromResult(url.Query.Contains("after=", StringComparison.Ordinal)
                ? Json("""{"data":[{"id":"u2","name":"minaEmotes"}]}""")
                : Json("""{"data":[{"id":"u1","name":"annanKanal"}],"pagination":{"cursor":"nasta"}}"""));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

public sealed class EmoteCatalogTests
{
    private static (TwitchApiClient Api, EmoteHandler Handler) LoggedIn(params string[] scopes)
    {
        var handler = new EmoteHandler(scopes);
        var client = new HttpClient(handler);
        var store = new TokenStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        store.Save(new StoredCredentials("refresh", "client", "streamern", "42", scopes));
        return (new TwitchApiClient(client, new TwitchSession(client, store)), handler);
    }

    /// <summary>
    /// A name in two lists belongs to the nearer one: an emote the channel owns should be found
    /// under the channel. The order the three are claimed in is not the order they are drawn in –
    /// the global set is claimed before the personal one precisely because almost every global emote
    /// is in the personal list too, and letting that claim them would file Kappa under "yours".
    /// </summary>
    [Fact]
    public async Task TheChannelKeepsANameTheGlobalListAlsoUses()
    {
        // Own channel: the broadcaster may use every emote of theirs, so nothing is narrowed away
        // here and the claiming is all that is being read.
        (TwitchApiClient api, _) = LoggedIn(TwitchAuth.EmotesScope);

        EmoteCatalog catalog = await api.GetUsableEmotesAsync("42");

        Assert.False(catalog.MissingScope);
        Assert.Equal(
            ["perraLOL", "Kappa", "PogChamp", "annanKanal", "minaEmotes"],
            catalog.Emotes.Select(emote => emote.Name));
        // The clash resolves to the channel's copy, image and all – not merely to its position.
        UsableEmote kappa = catalog.Emotes.Single(emote => emote.Name == "Kappa");
        Assert.Equal("channel", kappa.Group);
        Assert.Equal("c2", kappa.Id);
        Assert.Equal("global", catalog.Emotes.Single(emote => emote.Name == "PogChamp").Group);
    }

    /// <summary>
    /// chat/emotes answers with everything the channel has, subscriber tiers included, whether or
    /// not this account may send one – and an emote that may not be sent reaches the chat as loose
    /// words rather than as a picture. So where the personal list is known, it decides.
    /// </summary>
    [Fact]
    public async Task DropsChannelEmotesThisAccountCannotSend()
    {
        (TwitchApiClient api, _) = LoggedIn(TwitchAuth.EmotesScope);

        EmoteCatalog catalog = await api.GetUsableEmotesAsync("999");

        // perraLOL is the channel's, and in nobody's personal list: a sub emote of a channel this
        // account only moderates.
        Assert.True(catalog.ChannelChecked);
        Assert.DoesNotContain(catalog.Emotes, emote => emote.Name == "perraLOL");
        // Kappa is in the channel's list too and can be sent, so it stays – as the channel's.
        Assert.Equal("channel", catalog.Emotes.Single(emote => emote.Name == "Kappa").Group);
    }

    /// <summary>
    /// The one channel where the list needs no narrowing: a broadcaster may always use their own
    /// emotes, so the personal list is not the authority there and must not take them away.
    /// </summary>
    [Fact]
    public async Task InYourOwnChannelEveryChannelEmoteIsOffered()
    {
        (TwitchApiClient api, _) = LoggedIn(TwitchAuth.EmotesScope);

        EmoteCatalog catalog = await api.GetUsableEmotesAsync("42");

        Assert.Contains(catalog.Emotes, emote => emote.Name == "perraLOL");
    }

    /// <summary>The personal list is the only paged one, and stopping at page one would lose emotes.</summary>
    [Fact]
    public async Task FollowsTheCursorThroughTheUsersOwnEmotes()
    {
        (TwitchApiClient api, EmoteHandler handler) = LoggedIn(TwitchAuth.EmotesScope);

        EmoteCatalog catalog = await api.GetUsableEmotesAsync("999");

        Assert.Contains(catalog.Emotes, emote => emote.Name == "minaEmotes");
        Assert.Contains(handler.Requests, path => path.Contains("/emotes/user", StringComparison.Ordinal) && path.Contains("after=nasta", StringComparison.Ordinal));
        // Follower emotes for the channel being watched only come back when it is named.
        Assert.Contains(handler.Requests, path => path.Contains("/emotes/user", StringComparison.Ordinal) && path.Contains("broadcaster_id=999", StringComparison.Ordinal));
    }

    /// <summary>
    /// Get User Emotes takes user_id, broadcaster_id and after, and nothing else. An undocumented
    /// "first" is ignored today at best, and the day it stops being ignored it takes the personal
    /// list with it – which is the one list that says what this account may send.
    /// </summary>
    [Fact]
    public async Task AsksTheUserEmoteEndpointForNothingItDoesNotDocument()
    {
        (TwitchApiClient api, EmoteHandler handler) = LoggedIn(TwitchAuth.EmotesScope);

        await api.GetUsableEmotesAsync("999");

        IEnumerable<string> parameters = handler.Requests
            .Where(path => path.Contains("/emotes/user", StringComparison.Ordinal))
            .SelectMany(path => path[(path.IndexOf('?') + 1)..].Split('&'))
            .Select(pair => pair.Split('=')[0])
            .Distinct();
        Assert.Equal(["user_id", "broadcaster_id", "after"], parameters);
    }

    /// <summary>
    /// A login granted before the scope existed gets the one third that needs no permission at all –
    /// the global emotes – and is told why the rest is absent, rather than meeting an error.
    ///
    /// <para>Emphatically not the channel's list: without the personal one there is nothing to hold
    /// it against, and offering a subscriber emote to somebody who is not subscribed puts a picture
    /// in the box that reaches the chat as loose words.</para>
    /// </summary>
    [Fact]
    public async Task WithoutTheScopeOnlyTheGlobalEmotesCanBeVouchedFor()
    {
        (TwitchApiClient api, EmoteHandler handler) = LoggedIn("chat:edit");

        EmoteCatalog catalog = await api.GetUsableEmotesAsync("999");

        Assert.True(catalog.MissingScope);
        Assert.False(catalog.ChannelChecked);
        Assert.DoesNotContain(handler.Requests, path => path.Contains("/emotes/user", StringComparison.Ordinal));
        Assert.Equal(["Kappa", "PogChamp"], catalog.Emotes.Select(emote => emote.Name));
    }

    /// <summary>
    /// A personal list that hit the page cap cannot rule anything out – the emote missing from it
    /// may simply be on a page we never asked for – so the channel's section goes with it rather
    /// than being offered on a guess.
    /// </summary>
    [Fact]
    public async Task APersonalListCutShortByThePageCapLeavesTheChannelOut()
    {
        (TwitchApiClient api, EmoteHandler handler) = LoggedIn(TwitchAuth.EmotesScope);
        handler.EndlessUserEmotes = true;

        EmoteCatalog catalog = await api.GetUsableEmotesAsync("999");

        Assert.False(catalog.ChannelChecked);
        Assert.DoesNotContain(catalog.Emotes, emote => emote.Group == "channel");
    }

    /// <summary>A scope withdrawn on Twitch's side reads as the same "log in again", not as a failure.</summary>
    [Fact]
    public async Task ARefusedPersonalCallLeavesTheRestOfThePickerStanding()
    {
        (TwitchApiClient api, EmoteHandler handler) = LoggedIn(TwitchAuth.EmotesScope);
        handler.RefuseUserEmotes = true;

        EmoteCatalog catalog = await api.GetUsableEmotesAsync("999");

        Assert.True(catalog.MissingScope);
        Assert.Equal(["Kappa", "PogChamp"], catalog.Emotes.Select(emote => emote.Name));
    }

    /// <summary>
    /// The dock may open the picker before the channel has been joined. Global emotes are still an
    /// answer worth having, so the missing room takes its own section away and nothing else.
    /// </summary>
    [Fact]
    public async Task WithoutAChannelItAsksOnlyForWhatDoesNotNeedOne()
    {
        (TwitchApiClient api, EmoteHandler handler) = LoggedIn(TwitchAuth.EmotesScope);

        EmoteCatalog catalog = await api.GetUsableEmotesAsync("");

        Assert.DoesNotContain(handler.Requests, path => path.StartsWith("/helix/chat/emotes?", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, path => path.Contains("broadcaster_id=", StringComparison.Ordinal));
        Assert.Contains(catalog.Emotes, emote => emote.Name == "Kappa" && emote.Group == "global");
    }
}
