using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TwitchOverlayHelper.Models;
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
        (DockServer server, AppSettings settings, HttpClient client, _) = await StartWithHubAsync(loggedInUserId: null);
        return (server, settings, client);
    }

    /// <summary>Seeding the token store is what makes the session look logged in without touching Twitch.</summary>
    private static async Task<(DockServer Server, AppSettings Settings, HttpClient Client, ChatHub Hub)> StartWithHubAsync(string? loggedInUserId)
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
        var hub = new ChatHub(settings, new TwitchBadgeCatalog(), session, new PetRegistry(), petCatalog);
        var server = new DockServer(new DockServerContext
        {
            Settings = settings,
            Hub = hub,
            Session = session,
            Api = new TwitchApiClient(new HttpClient(), session),
            Chat = chat,
            Speech = SpeechFixture.Service(settings),
            Pets = petCatalog
        });

        Assert.True(await server.StartAsync());
        return (server, settings, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{settings.DockServerPort}") }, hub);
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
    private static async Task<string[]> FramesAfterHelloAsync(AppSettings settings, int count, Action act)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{settings.DockServerPort}/ws?key={settings.DockAccessKey}"), timeout.Token);

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

    private static async Task<int> HelloHistoryCountAsync(AppSettings settings)
    {
        using JsonDocument json = JsonDocument.Parse(await HelloAsync(settings));
        return json.RootElement.GetProperty("history").GetArrayLength();
    }

    private static async Task<string> HelloAsync(AppSettings settings)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{settings.DockServerPort}/ws?key={settings.DockAccessKey}"), timeout.Token);
        byte[] buffer = new byte[64 * 1024];
        WebSocketReceiveResult hello = await socket.ReceiveAsync(buffer, timeout.Token);
        return Encoding.UTF8.GetString(buffer, 0, hello.Count);
    }

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
