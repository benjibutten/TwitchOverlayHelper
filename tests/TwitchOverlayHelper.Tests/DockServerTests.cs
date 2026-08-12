using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Nicknames;
using TwitchOverlayHelper.Pets;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Speech;
using TwitchOverlayHelper.Twitch;
using TwitchOverlayHelper.Web;

namespace TwitchOverlayHelper.Tests;

public sealed class DockServerTests
{
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<(DockServer Server, AppSettings Settings, HttpClient Client)> StartAsync()
    {
        (DockServer server, AppSettings settings, HttpClient client, _, _) = await StartWithBookAsync(loggedInUserId: null);
        return (server, settings, client);
    }

    private static async Task<(DockServer Server, AppSettings Settings, HttpClient Client, ChatHub Hub)> StartWithHubAsync(string? loggedInUserId)
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub, _) = await StartWithBookAsync(loggedInUserId);
        return (server, settings, client, hub);
    }

    /// <summary>Seeding the token store is what makes the session look logged in without touching Twitch.</summary>
    private static async Task<(DockServer Server, AppSettings Settings, HttpClient Client, ChatHub Hub, NicknameBook Nicknames)> StartWithBookAsync(string? loggedInUserId)
    {
        var settings = new AppSettings { DockServerPort = FreePort() };
        settings.Normalize();

        var store = new TokenStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        if (loggedInUserId is not null)
            store.Save(new StoredCredentials("refresh", "client", "streamern", loggedInUserId, TwitchAuth.RequiredScopes));

        using var sessionHttp = new HttpClient();
        var session = new TwitchSession(sessionHttp, store);
        var chat = new TwitchChatClient();
        // Shared so the pets that ship with the app are written out once, not once per test.
        var petCatalog = new PetCatalog(Path.Combine(Path.GetTempPath(), "toh-tests-pets"));
        var nicknames = new NicknameBook();
        var hub = new ChatHub(settings, new TwitchBadgeCatalog(), session, new PetRegistry(), petCatalog, nicknames);
        // The same wiring the app does: the book is what a nickname change is announced from, so
        // every open dock hears about one made through any of them.
        nicknames.Changed += hub.PublishNickname;
        var api = new TwitchApiClient(new HttpClient(), session);
        var server = new DockServer(new DockServerContext
        {
            Settings = settings,
            Hub = hub,
            Session = session,
            Api = api,
            Chat = chat,
            Speech = SpeechFixture.Service(settings),
            Tts = SpeechFixture.Tts(settings),
            TtsAudio = new TtsAudioStore(),
            Pets = petCatalog,
            Nicknames = nicknames,
            Emotes = new UsableEmoteCatalog(api)
        });

        Assert.True(await server.StartAsync());
        return (server, settings, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{settings.DockServerPort}") }, hub, nicknames);
    }

    private static async Task<string> ErrorOfAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetString() ?? string.Empty;
    }

    [Fact]
    public async Task ServesTheDockPageFromEmbeddedResources()
    {
        (DockServer server, _, HttpClient client) = await StartAsync();
        await using (server)
        {
            HttpResponseMessage response = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("text/html", response.Content.Headers.ContentType!.ToString());

            string html = await response.Content.ReadAsStringAsync();
            Assert.Contains("app.js", html);

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/app.js")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/styles.css")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/pets.html")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/pets.js")).StatusCode);
        }
        client.Dispose();
    }

    [Fact]
    public async Task PetSpriteEndpointOnlyServesIdsTheCatalogKnows()
    {
        (DockServer server, _, HttpClient client) = await StartAsync();
        await using (server)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/pets/sprite/finns-inte")).StatusCode);
            // The pets that ship with the app are drawn from SVG and have no spritesheet to serve.
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/pets/sprite/robo")).StatusCode);
        }
        client.Dispose();
    }

    [Fact]
    public async Task PetBodyEndpointServesTheDrawingFromThePetsFolder()
    {
        (DockServer server, _, HttpClient client) = await StartAsync();
        await using (server)
        {
            HttpResponseMessage response = await client.GetAsync("/pets/body/robo");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("<svg", await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/pets/body/finns-inte")).StatusCode);
        }
        client.Dispose();
    }

    [Fact]
    public async Task RejectsApiCallsWithoutTheAccessKey()
    {
        (DockServer server, AppSettings settings, HttpClient client) = await StartAsync();
        await using (server)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/state")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/state?key=fel")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/state?key={settings.DockAccessKey}")).StatusCode);
        }
        client.Dispose();
    }

    [Fact]
    public async Task RejectsRequestsFromAnotherLocalPage()
    {
        (DockServer server, AppSettings settings, HttpClient client) = await StartAsync();
        await using (server)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/state?key={settings.DockAccessKey}");
            request.Headers.Add("Origin", "http://evil.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
        }
        client.Dispose();
    }

    [Fact]
    public async Task ExposesReadingSettingsButOffersNoWayToChangeThem()
    {
        (DockServer server, AppSettings settings, HttpClient client) = await StartAsync();
        await using (server)
        {
            using JsonDocument state = JsonDocument.Parse(await client.GetStringAsync($"/api/state?key={settings.DockAccessKey}"));
            Assert.Equal(settings.Dock.FontSize, state.RootElement.GetProperty("settings").GetProperty("fontSize").GetDouble());

            // Reading settings belong to the desktop app; the dock must not be able to write them.
            HttpResponseMessage put = await client.PutAsJsonAsync($"/api/settings?key={settings.DockAccessKey}", new DockSettings());
            Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        }
        client.Dispose();
    }

    [Fact]
    public async Task RefusesModerationBeforeTheChannelIsConnected()
    {
        (DockServer server, AppSettings settings, HttpClient client) = await StartAsync();
        await using (server)
        {
            HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/api/mod/ban?key={settings.DockAccessKey}", new { userId = "1" });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        client.Dispose();
    }

    // Nailing a line to the dock's own strip never reaches this server. Putting it in front of the
    // viewers does, and it is a Twitch call like any other moderation button: no channel, no call.
    [Fact]
    public async Task RefusesPinningForViewersBeforeTheChannelIsConnected()
    {
        (DockServer server, AppSettings settings, HttpClient client) = await StartAsync();
        await using (server)
        {
            Assert.Contains("inte ansluten", await ErrorOfAsync(await client.PostAsJsonAsync(
                $"/api/chat/pin?key={settings.DockAccessKey}", new { messageId = "abc" })));
            Assert.Contains("inte ansluten", await ErrorOfAsync(await client.PostAsJsonAsync(
                $"/api/chat/unpin?key={settings.DockAccessKey}", new { messageId = "abc" })));
        }
        client.Dispose();
    }

    // The strip in the dock needs no login, which is the point of building this from the local end.
    // Pinning for everyone watching is the half that does, and hiding the button is not enforcement.
    [Fact]
    public async Task RefusesPinningForViewersWhenNotLoggedIn()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            hub.BroadcasterId = "42";

            Assert.Contains("inte inloggad", await ErrorOfAsync(await client.PostAsJsonAsync(
                $"/api/chat/pin?key={settings.DockAccessKey}", new { messageId = "abc" })));
        }
        client.Dispose();
    }

    // A message with no id cannot be pinned, and finding that out from Twitch's 400 would cost a
    // round trip to say something we already knew.
    [Fact]
    public async Task RefusesPinningAMessageWithoutAnId()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: "42");
        await using (server)
        {
            hub.BroadcasterId = "42";

            Assert.Contains("saknar id", await ErrorOfAsync(await client.PostAsJsonAsync(
                $"/api/chat/pin?key={settings.DockAccessKey}", new { messageId = "" })));
            Assert.Contains("saknar id", await ErrorOfAsync(await client.PostAsJsonAsync(
                $"/api/chat/unpin?key={settings.DockAccessKey}", new { messageId = "" })));
        }
        client.Dispose();
    }

    [Fact]
    public async Task RefusesRaidWhenNotLoggedIn()
    {
        (DockServer server, AppSettings settings, HttpClient client) = await StartAsync();
        await using (server)
        {
            HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/api/raid/start?key={settings.DockAccessKey}", new { userId = "1" });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        client.Dispose();
    }

    // Raiding is only legal out of your own channel, and hiding the button is not enforcement.
    [Fact]
    public async Task RefusesRaidFromAChannelTheUserOnlyModerates()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: "42");
        await using (server)
        {
            hub.BroadcasterId = "999";

            Assert.Contains("egen kanal", await ErrorOfAsync(await client.PostAsJsonAsync(
                $"/api/raid/start?key={settings.DockAccessKey}", new { userId = "1" })));
            Assert.Contains("egen kanal", await ErrorOfAsync(await client.PostAsJsonAsync(
                $"/api/raid/cancel?key={settings.DockAccessKey}", new { })));
        }
        client.Dispose();
    }

    // A dock that reconnects must never be handed lines from a channel the app has left.
    [Fact]
    public async Task ForgetsThePreviousChannelsHistoryWhenSwitchingChannels()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            hub.SetChannel("kanal_a");
            hub.PublishMessage(new ChatMessage("1", "Någon", "hej", "#ffffff", [], false, false, DateTimeOffset.Now));
            Assert.Equal(1, await HelloHistoryCountAsync(settings));

            hub.SetChannel("kanal_b");
            Assert.Equal(0, await HelloHistoryCountAsync(settings));
        }
        client.Dispose();
    }

    // A dock that reconnects mid-stream has to get its lines back in the order they happened, or a
    // resub greeting ends up answering a message that was actually written after it.
    [Fact]
    public async Task ReplaysMessagesAndEventsInTheOrderTheyArrived()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            hub.SetChannel("kanal_a");
            hub.PublishMessage(new ChatMessage("1", "Någon", "hej", null, [], false, false, DateTimeOffset.Now));
            hub.PublishEvent(new ChatEvent(ChatEventType.Raid, "e1", "Streamern", DateTimeOffset.Now) { ViewerCount = 42 });
            hub.PublishMessage(new ChatMessage("2", "Någon", "välkomna", null, [], false, false, DateTimeOffset.Now));

            using JsonDocument hello = JsonDocument.Parse(await HelloAsync(settings));
            JsonElement history = hello.RootElement.GetProperty("history");

            Assert.Equal(["message", "event", "message"],
                history.EnumerateArray().Select(item => item.GetProperty("type").GetString() ?? string.Empty).ToArray());
            Assert.Equal("Streamern raidar med 42 tittare",
                history[1].GetProperty("event").GetProperty("headline").GetString());
        }
        client.Dispose();
    }

    // A timeout takes back what someone said, not the sub they paid for.
    [Fact]
    public async Task LeavesEventsStandingWhenAChatterIsPurged()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            hub.SetChannel("kanal_a");
            hub.PublishMessage(new ChatMessage("1", "Spammer", "spam", null, [], false, false, DateTimeOffset.Now) { UserLogin = "spammer" });
            hub.PublishEvent(new ChatEvent(ChatEventType.Subscription, "e1", "Spammer", DateTimeOffset.Now));
            hub.PublishModeration(new ChatModerationEvent(ChatEventKind.UserPurged, null, null, "spammer", 600, DateTimeOffset.Now));

            using JsonDocument hello = JsonDocument.Parse(await HelloAsync(settings));
            JsonElement history = hello.RootElement.GetProperty("history");

            Assert.Equal(1, history.GetArrayLength());
            Assert.Equal("event", history[0].GetProperty("type").GetString());
        }
        client.Dispose();
    }

    // A Gigantify power-up can land after the line it belongs to. The history has to be rewritten as
    // well as the live views, or a dock that reconnects a minute later replays the unmarked version.
    [Fact]
    public async Task ReplaysTheMarkedVersionOfALineThatWasUpdated()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            hub.SetChannel("kanal_a");
            var message = new ChatMessage("1", "Kajsa", "Kappa", null, [], false, false, DateTimeOffset.Now,
                [new EmoteSpan("25", 0, 5)]);
            hub.PublishMessage(message);
            hub.PublishMessageUpdate(message with { GigantifiedEmoteId = "25" });

            using JsonDocument hello = JsonDocument.Parse(await HelloAsync(settings));
            JsonElement history = hello.RootElement.GetProperty("history");

            // Replaced, not appended: the line happened once and must not read as two.
            Assert.Equal(1, history.GetArrayLength());
            Assert.Equal(0, history[0].GetProperty("message").GetProperty("giantEmote").GetInt32());
        }
        client.Dispose();
    }

    // A dock that opens mid-train – an OBS restart, a page reload – must not stand there empty while
    // the whole channel is watching a train it cannot see.
    [Fact]
    public async Task HandsARunningHypeTrainToADockThatConnectsMidTrain()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            hub.SetChannel("kanal_a");
            hub.PublishHypeTrain(new HypeTrainState("t1", HypeTrainPhase.Progress, 3, 200, 800, 1400, DateTimeOffset.Now)
            {
                ExpiresAt = DateTimeOffset.Now.AddMinutes(4),
                TopContributions = [new HypeTrainContribution("Kajsa", "bits", 1200)]
            });

            using JsonDocument hello = JsonDocument.Parse(await HelloAsync(settings));
            JsonElement train = hello.RootElement.GetProperty("hypeTrain");

            Assert.Equal("running", train.GetProperty("phase").GetString());
            Assert.Equal("Hypetåg – nivå 3", train.GetProperty("headline").GetString());
            Assert.Equal(800, train.GetProperty("goal").GetInt32());
            // Grouped with the non-breaking space Swedish uses, taken from the culture rather than
            // typed: the two kinds of space are indistinguishable in a source file.
            string nbsp = System.Globalization.CultureInfo.GetCultureInfo("sv-SE").NumberFormat.NumberGroupSeparator;
            Assert.Equal($"Kajsa (1{nbsp}200 bits)", train.GetProperty("top")[0].GetString());
        }
        client.Dispose();
    }

    // A train belongs to the channel it ran in; carrying it into the next one would put a strip over
    // someone else's chat for a train they were never part of. It goes even while the sample lines
    // are still up, because a train needs nobody to have said anything to be running.
    [Fact]
    public async Task ForgetsTheHypeTrainWhenSwitchingChannels()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            hub.ShowSamples();
            hub.SetChannel("kanal_a");
            hub.PublishHypeTrain(RunningTrain());

            hub.SetChannel("kanal_b");

            using JsonDocument hello = JsonDocument.Parse(await HelloAsync(settings));
            Assert.False(hello.RootElement.TryGetProperty("hypeTrain", out _));
        }
        client.Dispose();
    }

    /// <summary>
    /// The strip is only ever taken down by a frame that says so. It used to ride along on the clear
    /// frame, which also fires when the first real line replaces the sample lines – so a train
    /// running in a quiet room was wiped off the strip by a stranger's first "hej".
    /// </summary>
    [Fact]
    public async Task LeavesTheHypeTrainAloneWhenTheFirstRealLineReplacesTheSamples()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            hub.ShowSamples();
            hub.PublishHypeTrain(RunningTrain());

            // The clear that drops the samples, and the line that replaced them.
            string[] frames = await FramesAfterHelloAsync(settings, 2, () =>
                hub.PublishMessage(new ChatMessage("1", "Någon", "hej", null, [], false, false, DateTimeOffset.Now)));

            Assert.All(frames, frame => Assert.DoesNotContain("hypeTrain", frame, StringComparison.Ordinal));

            using JsonDocument hello = JsonDocument.Parse(await HelloAsync(settings));
            Assert.True(hello.RootElement.TryGetProperty("hypeTrain", out _));
        }
        client.Dispose();
    }

    // Losing the connection means nothing can tell us the train ended, so the docks that are open
    // right now have to hear that the strip is over – waiting for its deadline would leave it
    // claiming a train is running long after we stopped listening.
    [Fact]
    public async Task TellsOpenDocksWhenThereIsNoTrainAnyMore()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            hub.PublishHypeTrain(RunningTrain());

            string[] frames = await FramesAfterHelloAsync(settings, 1, hub.ClearHypeTrain);

            using JsonDocument frame = JsonDocument.Parse(frames[0]);
            Assert.Equal("hypeTrain", frame.RootElement.GetProperty("type").GetString());
            // No payload at all is how "there is no train" travels.
            Assert.False(frame.RootElement.TryGetProperty("payload", out _));
        }
        client.Dispose();
    }

    private static HypeTrainState RunningTrain() =>
        new("t1", HypeTrainPhase.Progress, 3, 200, 800, 1400, DateTimeOffset.Now)
        {
            ExpiresAt = DateTimeOffset.Now.AddMinutes(4)
        };

    /// <summary>Opens a dock, reads past its hello, then returns the frames <paramref name="act"/> caused.</summary>
    private static async Task<string[]> FramesAfterHelloAsync(AppSettings settings, int count, Action act, string view = "")
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(SocketUrl(settings, view)), timeout.Token);

        byte[] buffer = new byte[64 * 1024];
        await socket.ReceiveAsync(buffer, timeout.Token);

        act();

        var frames = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, timeout.Token);
            frames.Add(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        return frames.ToArray();
    }

    // The strip is a state, not a log: a train that ended before this dock existed is not news.
    [Fact]
    public async Task DoesNotReplayATrainThatIsLongOver()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            hub.SetChannel("kanal_a");
            hub.PublishHypeTrain(new HypeTrainState("t1", HypeTrainPhase.Ended, 4, 0, 0, 4250,
                DateTimeOffset.Now - HypeTrainState.EndedLinger - TimeSpan.FromMinutes(1)));

            using JsonDocument hello = JsonDocument.Parse(await HelloAsync(settings));
            Assert.False(hello.RootElement.TryGetProperty("hypeTrain", out _));
        }
        client.Dispose();
    }

    // ------------------------------------------------------------- the stream overlay

    [Fact]
    public async Task ServesTheStreamOverlayAndTheRendererBothPagesShare()
    {
        (DockServer server, _, HttpClient client) = await StartAsync();
        await using (server)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/stream.html")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/stream.js")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/stream.css")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/chat-render.js")).StatusCode);
        }
        client.Dispose();
    }

    /// <summary>
    /// The whole point of the separate view: this page is on the broadcast, so the nicknames, who is
    /// logged in and the dock's own settings must not merely go undrawn – they must not arrive.
    /// </summary>
    [Fact]
    public async Task StreamHelloCarriesTheAppearanceAndNothingPrivate()
    {
        (DockServer server, AppSettings settings, HttpClient client, _, NicknameBook nicknames) =
            await StartWithBookAsync(loggedInUserId: null);
        await using (server)
        {
            nicknames.Set("42", "kajsa_92", "Kajsa från jobbet");

            using JsonDocument hello = JsonDocument.Parse(await HelloAsync(settings, "stream"));
            Assert.Equal("hello", hello.RootElement.GetProperty("type").GetString());
            Assert.Equal(settings.Stream.FontSize, hello.RootElement.GetProperty("stream").GetProperty("fontSize").GetDouble());
            Assert.True(hello.RootElement.TryGetProperty("history", out _));

            foreach (string secret in new[] { "nicknames", "auth", "settings", "mentionName", "speechEnabled" })
                Assert.False(hello.RootElement.TryGetProperty(secret, out _), $"strömvyn fick '{secret}'");

            // And the dock still gets all of it, so the split has not quietly taken it from both.
            using JsonDocument dockHello = JsonDocument.Parse(await HelloAsync(settings));
            Assert.Equal(1, dockHello.RootElement.GetProperty("nicknames").GetArrayLength());
        }
        client.Dispose();
    }

    /// <summary>
    /// Deeper than the overlay draws, because the page still has to throw away bots, commands and
    /// switched-off cards: a quiet stretch that ended in bot chatter must not open onto an empty
    /// column with good lines sitting just above the cut. Deep is not the whole timeline either.
    /// </summary>
    [Fact]
    public async Task StreamHelloCarriesMoreThanTheOverlayDrawsButNotEverything()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            settings.Stream.MaxMessages = 3;
            hub.SetChannel("kanal_a");
            for (int i = 0; i < 60; i++)
                hub.PublishMessage(new ChatMessage($"m{i}", "Någon", $"rad {i}", null, [], false, false, DateTimeOffset.Now));

            using JsonDocument hello = JsonDocument.Parse(await HelloAsync(settings, "stream"));
            JsonElement history = hello.RootElement.GetProperty("history");
            int count = history.GetArrayLength();
            Assert.InRange(count, settings.Stream.MaxMessages + 1, 59);
            // The tail, not the head: what an overlay opens onto is the newest lines.
            Assert.Equal("rad 59", history[count - 1].GetProperty("message").GetProperty("text").GetString());
        }
        client.Dispose();
    }

    /// <summary>
    /// The preview lines are the only thing the stream overlay has to aim at while nothing is
    /// connected, which is exactly when somebody is dragging the browser source into place in OBS.
    ///
    /// <para>They used to arrive as ordinary chat, every one of them carrying the moment the app
    /// started – and the page drops a replayed line once it is older than the window it would
    /// honestly replay. A few minutes in, every reload of the source therefore met an empty page,
    /// and only restarting the whole app brought the lines back. Saying what they are is what lets
    /// the page leave them alone.</para>
    /// </summary>
    [Fact]
    public async Task TheStreamOverlayIsToldWhenItsLinesAreThePreview()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            hub.ShowSamples();

            using JsonDocument hello = JsonDocument.Parse(await HelloAsync(settings, "stream"));
            Assert.True(hello.RootElement.GetProperty("samples").GetBoolean());
            Assert.True(hello.RootElement.GetProperty("history").GetArrayLength() > 0);
        }
        client.Dispose();
    }

    /// <summary>
    /// Joining the channel is what ends the preview, not the first line somebody happens to write.
    /// A quiet first quarter of an hour is an ordinary way for a stream to start, and until this the
    /// invented lines sat in front of the viewers for the whole of it.
    /// </summary>
    [Fact]
    public async Task ConnectingToTheChannelTakesThePreviewDown()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            hub.ShowSamples();

            string[] frames = await FramesAfterHelloAsync(settings, 1, hub.ClearSamples, "stream");
            using JsonDocument frame = JsonDocument.Parse(frames[0]);
            Assert.Equal("clear", frame.RootElement.GetProperty("type").GetString());

            // And nothing is left for the next page that opens, either.
            using JsonDocument hello = JsonDocument.Parse(await HelloAsync(settings, "stream"));
            Assert.False(hello.RootElement.GetProperty("samples").GetBoolean());
            Assert.Equal(0, hello.RootElement.GetProperty("history").GetArrayLength());
        }
        client.Dispose();
    }

    /// <summary>
    /// Being disconnected, and why, is something the app knows about itself. The dock says it out
    /// loud in its top bar; the page on the broadcast is not told at all.
    /// </summary>
    [Fact]
    public async Task TheConnectionStatusStaysInTheDock()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            hub.SetChannel("kanal_a");

            string[] frames = await FramesAfterHelloAsync(settings, 1, () =>
            {
                hub.PublishStatus("Token har gått ut", "error");
                hub.PublishMessage(new ChatMessage("1", "Någon", "hej", null, [], false, false, DateTimeOffset.Now));
            }, "stream");

            Assert.Contains("\"type\":\"message\"", frames[0]);
            Assert.DoesNotContain("Token har", frames[0]);
        }
        client.Dispose();
    }

    /// <summary>A name given in the dock reaches the dock and stops there.</summary>
    [Fact]
    public async Task ANicknameNeverReachesTheStreamOverlay()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub, NicknameBook nicknames) =
            await StartWithBookAsync(loggedInUserId: null);
        await using (server)
        {
            hub.SetChannel("kanal_a");

            // The nickname is published first, so a stream socket that received it at all would hand
            // it back here instead of the message.
            string[] frames = await FramesAfterHelloAsync(settings, 1, () =>
            {
                nicknames.Set("42", "kajsa_92", "Kajsa från jobbet");
                hub.PublishMessage(new ChatMessage("1", "Kajsa_92", "hej", null, [], false, false, DateTimeOffset.Now));
            }, "stream");

            // Had the nickname reached this socket it would be sitting here instead of the message.
            Assert.Contains("\"type\":\"message\"", frames[0]);
            Assert.DoesNotContain("nickname", frames[0]);
        }
        client.Dispose();
    }

    [Fact]
    public async Task AppearanceFramesGoToThePageTheyBelongTo()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            hub.SetChannel("kanal_a");

            string[] toDock = await FramesAfterHelloAsync(settings, 1, () =>
            {
                hub.PublishStreamSettings();
                hub.PublishSettings();
            });
            Assert.Contains("\"type\":\"settings\"", toDock[0]);

            string[] toStream = await FramesAfterHelloAsync(settings, 1, () =>
            {
                hub.PublishSettings();
                hub.PublishStreamSettings();
            }, "stream");
            Assert.Contains("\"type\":\"streamSettings\"", toStream[0]);
        }
        client.Dispose();
    }

    private static async Task<int> HelloHistoryCountAsync(AppSettings settings)
    {
        using JsonDocument json = JsonDocument.Parse(await HelloAsync(settings));
        return json.RootElement.GetProperty("history").GetArrayLength();
    }

    private static async Task<string> HelloAsync(AppSettings settings, string view = "")
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(SocketUrl(settings, view)), timeout.Token);
        byte[] buffer = new byte[64 * 1024];
        WebSocketReceiveResult hello = await socket.ReceiveAsync(buffer, timeout.Token);
        return Encoding.UTF8.GetString(buffer, 0, hello.Count);
    }

    private static string SocketUrl(AppSettings settings, string view) =>
        $"ws://127.0.0.1:{settings.DockServerPort}/ws?key={settings.DockAccessKey}"
        + (view.Length > 0 ? $"&view={view}" : string.Empty);

    // ------------------------------------------------------------- the pet overlay
    //
    // The pet lawn is the one view that has to answer "is anybody going to see this", because a
    // reward that can pay back refuses rather than spend a viewer's points on an empty screen.

    [Fact]
    public async Task ThePetOverlayGetsItsOwnGreetingWithoutTheStreamersBusinessInIt()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: "42");
        await using (server)
        {
            hub.PublishMessage(new ChatMessage("1", "Någon", "hej", "#ffffff", [], false, false, DateTimeOffset.Now));

            using JsonDocument hello = JsonDocument.Parse(await HelloAsync(settings, "pets"));

            Assert.Equal("hello", hello.RootElement.GetProperty("type").GetString());
            Assert.True(hello.RootElement.TryGetProperty("petSettings", out _));
            Assert.True(hello.RootElement.TryGetProperty("petCatalog", out _));
            Assert.True(hello.RootElement.TryGetProperty("pets", out _));
            // A browser source on the broadcast machine has no business with any of these.
            Assert.False(hello.RootElement.TryGetProperty("history", out _));
            Assert.False(hello.RootElement.TryGetProperty("nicknames", out _));
            Assert.False(hello.RootElement.TryGetProperty("auth", out _));
        }
        client.Dispose();
    }

    [Fact]
    public async Task OnlyThePetViewCountsAsALawnThatCanBeSeen()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            byte[] buffer = new byte[64 * 1024];

            // A dock left open in a browser tab is not an answer to "will anyone see the pet".
            using var dock = new ClientWebSocket();
            await dock.ConnectAsync(new Uri(SocketUrl(settings, "")), timeout.Token);
            await dock.ReceiveAsync(buffer, timeout.Token);
            Assert.Equal(0, hub.PetOverlayCount);

            using var lawn = new ClientWebSocket();
            await lawn.ConnectAsync(new Uri(SocketUrl(settings, "pets")), timeout.Token);
            await lawn.ReceiveAsync(buffer, timeout.Token);
            Assert.Equal(1, hub.PetOverlayCount);
        }
        client.Dispose();
    }

    // The receipt that tells a pet which was drawn from one that was only sent.
    [Fact]
    public async Task ThePetOverlayCanReportThePetsItHasDrawn()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            var shown = new TaskCompletionSource<string>();
            hub.PetShown += id => shown.TrySetResult(id);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var lawn = new ClientWebSocket();
            await lawn.ConnectAsync(new Uri(SocketUrl(settings, "pets")), timeout.Token);
            await lawn.ReceiveAsync(new byte[64 * 1024], timeout.Token);

            await SendAsync(lawn, """{"type":"petShown","id":"viewer-7"}""", timeout.Token);

            Assert.Equal("viewer-7", await shown.Task.WaitAsync(timeout.Token));
        }
        client.Dispose();
    }

    // These sockets sit on the broadcast machine, so anything that is not the one frame we know is
    // dropped rather than guessed at.
    [Fact]
    public async Task ADockCannotClaimAPetWasDrawnAndNeitherCanAnUnknownFrame()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            var heard = new List<string>();
            hub.PetShown += heard.Add;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            byte[] buffer = new byte[64 * 1024];

            using var dock = new ClientWebSocket();
            await dock.ConnectAsync(new Uri(SocketUrl(settings, "")), timeout.Token);
            await dock.ReceiveAsync(buffer, timeout.Token);
            await SendAsync(dock, """{"type":"petShown","id":"viewer-7"}""", timeout.Token);

            using var lawn = new ClientWebSocket();
            await lawn.ConnectAsync(new Uri(SocketUrl(settings, "pets")), timeout.Token);
            await lawn.ReceiveAsync(buffer, timeout.Token);
            await SendAsync(lawn, "inte ens json", timeout.Token);
            await SendAsync(lawn, """{"type":"nagot-annat","id":"viewer-8"}""", timeout.Token);
            // Well-formed JSON of the wrong shape. Reading these without checking the kind first
            // throws InvalidOperationException, which is not a JsonException and would travel
            // straight out of the socket loop – a browser source could end its own connection.
            await SendAsync(lawn, """{"type":1}""", timeout.Token);
            await SendAsync(lawn, """{"type":{"petShown":true},"id":"viewer-8"}""", timeout.Token);
            await SendAsync(lawn, """{"type":"petShown","id":17}""", timeout.Token);
            await SendAsync(lawn, "[1,2,3]", timeout.Token);

            // A frame that does reach through, so the assertion is not just racing the two above.
            var shown = new TaskCompletionSource<string>();
            hub.PetShown += id => shown.TrySetResult(id);
            await SendAsync(lawn, """{"type":"petShown","id":"viewer-9"}""", timeout.Token);
            await shown.Task.WaitAsync(timeout.Token);

            Assert.Equal(["viewer-9"], heard);
        }
        client.Dispose();
    }

    [Fact]
    public async Task ARefundedPetIsSentHomeOnItsOwnFrame()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            string[] frames = await FramesAfterHelloAsync(settings, 1, () => hub.PublishPetRemoved("viewer-7"), "pets");

            using JsonDocument frame = JsonDocument.Parse(frames[0]);
            Assert.Equal("petRemove", frame.RootElement.GetProperty("type").GetString());
            Assert.Equal("viewer-7", frame.RootElement.GetProperty("payload").GetProperty("id").GetString());
        }
        client.Dispose();
    }

    // The lawn reads no chat, and being sent it is not free: its outbound queue is bounded, and a
    // client that fills one is dropped. During a raid that would close the pet socket – which is
    // now the thing that refunds every pet on screen.
    [Fact]
    public async Task ThePetOverlayIsNotSentTheChat()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var lawn = new ClientWebSocket();
            await lawn.ConnectAsync(new Uri(SocketUrl(settings, "pets")), timeout.Token);
            byte[] buffer = new byte[64 * 1024];
            await lawn.ReceiveAsync(buffer, timeout.Token);

            for (int i = 0; i < 20; i++)
                hub.PublishMessage(new ChatMessage($"{i}", "Någon", "hej", "#ffffff", [], false, false, DateTimeOffset.Now));
            // A pet frame behind them: the first thing the lawn hears has to be this one, which only
            // holds if none of the chat above was sent to it.
            hub.PublishPetRemoved("viewer-7");

            WebSocketReceiveResult result = await lawn.ReceiveAsync(buffer, timeout.Token);
            using JsonDocument frame = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
            Assert.Equal("petRemove", frame.RootElement.GetProperty("type").GetString());
        }
        client.Dispose();
    }

    // A lawn added as a browser source before the pet view existed connects as a dock. Narrowing
    // the pet frames to the new view would leave it showing nothing at all.
    [Fact]
    public async Task APetOverlayConnectedAsADockStillGetsItsPets()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            string[] frames = await FramesAfterHelloAsync(settings, 1, () => hub.PublishPetRemoved("viewer-7"));

            using JsonDocument frame = JsonDocument.Parse(frames[0]);
            Assert.Equal("petRemove", frame.RootElement.GetProperty("type").GetString());
        }
        client.Dispose();
    }

    [Fact]
    public async Task LeavingAChannelEmptiesTheLawn()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            string[] frames = await FramesAfterHelloAsync(settings, 1, hub.PublishPetsCleared, "pets");

            using JsonDocument frame = JsonDocument.Parse(frames[0]);
            Assert.Equal("petsClear", frame.RootElement.GetProperty("type").GetString());
        }
        client.Dispose();
    }

    // ------------------------------------------------------------- the reading page
    //
    // A browser source added to the scene so OBS has somewhere to mix the readings. Its answer at the
    // end of a clip is what releases the next one in the queue – and, on the channel points route, the
    // only evidence the reading was delivered at all.

    /// <summary>
    /// A reading page that goes away mid-clip cannot report anything: its report would travel over the
    /// socket that has just closed, and a source taken out of the scene never runs another line of
    /// script. So the app has to notice for itself – otherwise the queue stands still for the whole
    /// five minute timeout, and the viewer waits it out for points they were going to get anyway.
    /// </summary>
    [Fact]
    public async Task AReadingPageThatLeavesMidClipDoesNotHoldTheQueue()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            var output = new BrowserTtsOutput(hub, new TtsAudioStore(), () => settings.DockAccessKey);
            hub.TtsPlaybackFinished += output.OnFinished;
            hub.TtsOverlayCountChanged += output.OnOverlayCountChanged;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var page = new ClientWebSocket();
            await page.ConnectAsync(new Uri(SocketUrl(settings, "tts")), timeout.Token);
            await page.ReceiveAsync(new byte[64 * 1024], timeout.Token);

            Task playing = output.PlayAsync("uppläsning.mp3", 1, timeout.Token);
            // The clip is out and nobody has answered for it: this is the wait the timeout guards.
            Assert.False(playing.IsCompleted);

            // OBS pulling the source, rather than a polite goodbye.
            page.Dispose();

            SpeechException failed = await Assert.ThrowsAsync<SpeechException>(() => playing.WaitAsync(timeout.Token));
            // Which is what pays the viewer back: nobody heard the reading.
            Assert.Contains("OBS", failed.Message);
        }
        client.Dispose();
    }

    private static Task SendAsync(ClientWebSocket socket, string payload, CancellationToken token) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, token);

    // The speaker button is hidden when pronunciation is not set up, but hiding a button is not
    // enforcement – and the endpoint spends money at two APIs.
    [Fact]
    public async Task RefusesToReadANameBeforePronunciationIsSetUp()
    {
        (DockServer server, AppSettings settings, HttpClient client) = await StartAsync();
        await using (server)
        {
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync("/api/speech/name", new { displayName = "Kajsa" })).StatusCode);

            HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/api/speech/name?key={settings.DockAccessKey}", new { displayName = "Kajsa" });
            Assert.Contains("inte påslagen", await ErrorOfAsync(response));
        }
        client.Dispose();
    }

    [Fact]
    public async Task TellsTheDockWhetherNamesCanBeReadAloud()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            using JsonDocument off = JsonDocument.Parse(await client.GetStringAsync($"/api/state?key={settings.DockAccessKey}"));
            Assert.False(off.RootElement.GetProperty("speechEnabled").GetBoolean());

            hub.SpeechEnabled = true;
            using JsonDocument on = JsonDocument.Parse(await client.GetStringAsync($"/api/state?key={settings.DockAccessKey}"));
            Assert.True(on.RootElement.GetProperty("speechEnabled").GetBoolean());
        }
        client.Dispose();
    }

    // Naming a chatter changes nothing on Twitch and nobody else ever sees it, so it works in any
    // channel and without a login – like the local pin, and unlike everything in the moderation row.
    [Fact]
    public async Task NamesAChatterWithoutALogin()
    {
        (DockServer server, AppSettings settings, HttpClient client, _, NicknameBook nicknames) =
            await StartWithBookAsync(loggedInUserId: null);
        await using (server)
        {
            HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/api/nickname?key={settings.DockAccessKey}", new { userId = "7", login = "Kajsa", text = "  Grannen  " });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Grannen", nicknames.For("7", null));
            // The login is stored lowercased, which is the form every chat line carries.
            Assert.Equal("Grannen", nicknames.For(null, "kajsa"));
        }
        client.Dispose();
    }

    // Blank text is the way one is taken back; there is no second endpoint for it.
    [Fact]
    public async Task TakesANicknameBackWhenTheTextIsCleared()
    {
        (DockServer server, AppSettings settings, HttpClient client, _, NicknameBook nicknames) =
            await StartWithBookAsync(loggedInUserId: null);
        await using (server)
        {
            nicknames.Set("7", "kajsa", "Grannen");

            HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/api/nickname?key={settings.DockAccessKey}", new { userId = "7", login = "kajsa", text = "   " });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(nicknames.For("7", "kajsa"));
        }
        client.Dispose();
    }

    /// <summary>
    /// The earlier-sitting button. It goes through the app rather than hiding nodes in the browser
    /// precisely so that this happens: the timeline itself loses the lines, which is what keeps them
    /// off the overlay and out of the file that survives a restart.
    /// </summary>
    [Fact]
    public async Task HidesTheEarlierSittingForEveryDock()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub) = await StartWithHubAsync(loggedInUserId: null);
        await using (server)
        {
            DateTimeOffset evening = DateTimeOffset.Now;
            hub.ReplaceHistory([
                ChatTimelineItem.Of(Line("morgon", evening.AddHours(-9))),
                ChatTimelineItem.Of(Line("kvall", evening)),
            ]);

            string[] frames = await FramesAfterHelloAsync(settings, 1, () =>
            {
                HttpResponseMessage response = client.PostAsJsonAsync(
                    $"/api/chat/trim?key={settings.DockAccessKey}",
                    new { before = evening.ToUnixTimeMilliseconds() }).GetAwaiter().GetResult();
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using JsonDocument body = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Equal(1, body.RootElement.GetProperty("removed").GetInt32());
            });

            // One frame with the whole remaining timeline, not a clear followed by the lines again:
            // replayed as messages they would trickle back in at the reader's chosen pace.
            using JsonDocument frame = JsonDocument.Parse(frames[0]);
            Assert.Equal("history", frame.RootElement.GetProperty("type").GetString());
            JsonElement payload = frame.RootElement.GetProperty("payload");
            Assert.Equal(1, payload.GetArrayLength());
            Assert.Equal("kvall", payload[0].GetProperty("message").GetProperty("id").GetString());
        }
        client.Dispose();
    }

    [Fact]
    public async Task RefusesToHideTheEarlierSittingWithoutAPointToCutAt()
    {
        (DockServer server, AppSettings settings, HttpClient client) = await StartAsync();
        await using (server)
        {
            Assert.Contains("Vet inte var", await ErrorOfAsync(await client.PostAsJsonAsync(
                $"/api/chat/trim?key={settings.DockAccessKey}", new { before = 0 })));
        }
        client.Dispose();
    }

    private static ChatMessage Line(string id, DateTimeOffset at) =>
        new(id, "Kajsa", "hej", "#A970FF", [], false, false, at) { UserId = "7", UserLogin = "kajsa" };

    [Fact]
    public async Task RefusesANicknameWithNobodyToPutItOn()
    {
        (DockServer server, AppSettings settings, HttpClient client) = await StartAsync();
        await using (server)
        {
            Assert.Contains("Vet inte vem", await ErrorOfAsync(await client.PostAsJsonAsync(
                $"/api/nickname?key={settings.DockAccessKey}", new { userId = "", login = "", text = "Grannen" })));
        }
        client.Dispose();
    }

    // A nickname belongs to the chatter, not to one message, so it has to reach the docks that are
    // already open – including the lines they drew before it existed.
    [Fact]
    public async Task TellsEveryDockAboutANewNickname()
    {
        (DockServer server, AppSettings settings, HttpClient client, _, NicknameBook nicknames) =
            await StartWithBookAsync(loggedInUserId: null);
        await using (server)
        {
            string[] frames = await FramesAfterHelloAsync(settings, 1, () => nicknames.Set("7", "kajsa", "Grannen"));

            using JsonDocument frame = JsonDocument.Parse(frames[0]);
            Assert.Equal("nickname", frame.RootElement.GetProperty("type").GetString());
            JsonElement payload = frame.RootElement.GetProperty("payload");
            Assert.Equal("7", payload.GetProperty("userId").GetString());
            Assert.Equal("Grannen", payload.GetProperty("text").GetString());
        }
        client.Dispose();
    }

    // A dock that reloads must not lose the names: they are read from a book it is handed up front,
    // not from the messages, so they land on the replayed history too.
    [Fact]
    public async Task HandsTheWholeNicknameBookToADockThatConnects()
    {
        (DockServer server, AppSettings settings, HttpClient client, ChatHub hub, NicknameBook nicknames) =
            await StartWithBookAsync(loggedInUserId: null);
        await using (server)
        {
            nicknames.Set("7", "kajsa", "Grannen");
            hub.SetChannel("kanal_a");

            using JsonDocument hello = JsonDocument.Parse(await HelloAsync(settings));
            JsonElement book = hello.RootElement.GetProperty("nicknames");

            Assert.Equal(1, book.GetArrayLength());
            Assert.Equal("Grannen", book[0].GetProperty("text").GetString());
            Assert.Equal("kajsa", book[0].GetProperty("login").GetString());
        }
        client.Dispose();
    }

    // The IRC socket outlives a logout, so the endpoint has to be what stops sending.
    [Fact]
    public async Task RefusesSendingWhenNotLoggedIn()
    {
        (DockServer server, AppSettings settings, HttpClient client) = await StartAsync();
        await using (server)
        {
            HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/api/chat/send?key={settings.DockAccessKey}", new { text = "hej" });
            Assert.Contains("inte inloggad", await ErrorOfAsync(response));
        }
        client.Dispose();
    }
}

public sealed class DockSettingsTests
{
    [Fact]
    public void ClampsOutOfRangeValues()
    {
        var settings = new DockSettings { FontSize = 900, LineHeight = 99, MaxMessages = 100000, LetterSpacing = -3 };

        settings.Normalize();

        Assert.Equal(48, settings.FontSize);
        Assert.Equal(2.4, settings.LineHeight);
        Assert.Equal(500, settings.MaxMessages);
        Assert.Equal(0, settings.LetterSpacing);
    }

    [Fact]
    public void FallsBackToAKnownThemeAndFont()
    {
        var settings = new DockSettings { Theme = "regnbåge", FontFamily = "   " };

        settings.Normalize();

        Assert.Equal("cream", settings.Theme);
        Assert.Equal("Verdana", settings.FontFamily);
    }

    [Fact]
    public void TreatsZeroPaceAsNoLimit()
    {
        var settings = new DockSettings { MessagesPerSecond = 0 };

        settings.Normalize();

        Assert.Equal(0, settings.MessagesPerSecond);
    }
}
