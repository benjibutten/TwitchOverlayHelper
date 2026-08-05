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

    private static async Task<int> HelloHistoryCountAsync(AppSettings settings)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{settings.DockServerPort}/ws?key={settings.DockAccessKey}"), timeout.Token);
        byte[] buffer = new byte[64 * 1024];
        WebSocketReceiveResult hello = await socket.ReceiveAsync(buffer, timeout.Token);
        using JsonDocument json = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, hello.Count));
        return json.RootElement.GetProperty("history").GetArrayLength();
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
