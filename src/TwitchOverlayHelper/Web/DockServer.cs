using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TwitchOverlayHelper.Nicknames;
using TwitchOverlayHelper.Pets;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Speech;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Web;

/// <summary>Everything the dock endpoints need from the running app.</summary>
public sealed class DockServerContext
{
    public required AppSettings Settings { get; init; }
    public required ChatHub Hub { get; init; }
    public required TwitchSession Session { get; init; }
    public required TwitchApiClient Api { get; init; }
    public required TwitchChatClient Chat { get; init; }
    public required NameSpeechService Speech { get; init; }
    public required PetCatalog Pets { get; init; }
    public required NicknameBook Nicknames { get; init; }
    public required UsableEmoteCatalog Emotes { get; init; }
}

/// <summary>
/// The local web server behind the OBS browser dock. Bound to loopback so it is never reachable
/// from the network, and gated on a per-install key so other pages on this machine cannot use it.
/// </summary>
public sealed class DockServer(DockServerContext context) : IAsyncDisposable
{
    private WebApplication? _app;

    public bool IsRunning => _app is not null;
    public string? LastError { get; private set; }
    public int Port { get; private set; }

    public string DockUrl => $"http://127.0.0.1:{Port}/?key={context.Settings.DockAccessKey}";

    /// <summary>The transparent pet overlay, meant for an OBS browser source over the game.</summary>
    public string PetsUrl => $"http://127.0.0.1:{Port}/pets.html?key={context.Settings.DockAccessKey}";

    public async Task<bool> StartAsync()
    {
        if (_app is not null) return true;

        int port = context.Settings.DockServerPort;
        try
        {
            WebApplication app = Build(port);
            await app.StartAsync().ConfigureAwait(false);
            _app = app;
            Port = port;
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException)
        {
            // The dock URL is pasted into OBS once and must stay stable, so we never silently
            // fall back to another port – the user gets told to free this one instead.
            LastError = $"Port {port} är upptagen av ett annat program. Välj en annan port i inställningarna.";
            return false;
        }
    }

    public async Task StopAsync()
    {
        WebApplication? app = _app;
        _app = null;
        if (app is null) return;
        try { await app.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
        await app.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private WebApplication Build(int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.AddServerHeader = false;
        });
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });

        WebApplication app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.Use(GuardAsync);
        MapEndpoints(app);
        return app;
    }

    private async Task GuardAsync(HttpContext http, RequestDelegate next)
    {
        // OBS caches aggressively; a stale dock after an app update would be very confusing.
        http.Response.Headers.CacheControl = "no-store";

        bool needsKey = http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/ws");
        if (needsKey)
        {
            if (!IsAuthorized(http))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsync("Fel eller saknad nyckel.").ConfigureAwait(false);
                return;
            }
            if (!IsSameOrigin(http))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsync("Otillåten origin.").ConfigureAwait(false);
                return;
            }
        }

        await next(http).ConfigureAwait(false);
    }

    private bool IsAuthorized(HttpContext http)
    {
        string expected = context.Settings.DockAccessKey;
        string? provided = http.Request.Query["key"].FirstOrDefault() ?? http.Request.Headers["X-Dock-Key"].FirstOrDefault();
        return expected.Length > 0
            && provided is not null
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(provided));
    }

    /// <summary>Blocks another local page from driving the API through the browser, key or not.</summary>
    private bool IsSameOrigin(HttpContext http)
    {
        string? origin = http.Request.Headers.Origin.FirstOrDefault();
        if (string.IsNullOrEmpty(origin)) return true;
        return origin.Equals($"http://127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase)
            || origin.Equals($"http://localhost:{Port}", StringComparison.OrdinalIgnoreCase);
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/ws", async (HttpContext http) =>
        {
            if (!http.WebSockets.IsWebSocketRequest) return Results.BadRequest();
            using WebSocket socket = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await context.Hub.RunClientAsync(socket, context.Chat.CanSend, http.RequestAborted).ConfigureAwait(false);
            return Results.Empty;
        });

        app.MapGet("/api/state", () => Results.Json(new
        {
            settings = context.Settings.Dock,
            auth = context.Hub.BuildAuth(context.Chat.CanSend),
            channel = context.Settings.Channel,
            speechEnabled = context.Hub.SpeechEnabled
        }, DockJson.Options));

        // Reads a chatter's name out loud on the machine running the app. Deliberately not gated on
        // being logged in: hearing a name is a reading aid, not a moderation action.
        app.MapPost("/api/speech/name", async (SpeakNameRequest request) =>
        {
            if (!context.Speech.IsConfigured)
                return Problem("Uppläsning av namn är inte påslagen i appen.");

            string name = Pick(request.DisplayName, request.Login);
            try
            {
                NameSpeechResult result = await context.Speech.SpeakAsync(name).ConfigureAwait(false);
                return Results.Json(new { spoken = result.Spoken, warning = result.Warning }, DockJson.Options);
            }
            catch (Exception ex) when (ex is SpeechException or HttpRequestException)
            {
                return Problem(ex.Message);
            }
        });

        // Naming a chatter is a reading aid on this machine: it changes nothing on Twitch, nobody
        // but this reader ever sees it, and it works in any channel. So it sits outside everything a
        // logout takes away, next to the local pin rather than next to the moderation buttons.
        // The app saves and fans the change out from the book's own change event, which is what
        // keeps the overlay and every open dock saying the same name.
        app.MapPost("/api/nickname", (SetNicknameRequest request) =>
        {
            string userId = request.UserId?.Trim() ?? string.Empty;
            string login = request.Login?.Trim().ToLowerInvariant() ?? string.Empty;
            if (userId.Length == 0 && login.Length == 0)
                return Problem("Vet inte vem smeknamnet gäller.");

            // Blank text is the way a nickname is taken back, so it is an answer rather than an error.
            if (NicknameBook.Clean(request.Text).Length == 0)
            {
                context.Nicknames.Remove(userId, login);
                return Results.Json(new { userId, login, text = (string?)null }, DockJson.Options);
            }

            Nickname? saved = context.Nicknames.Set(userId, login, request.Text);
            return saved is null
                ? Problem("Smeknamnet kunde inte sparas.")
                : Results.Json(saved, DockJson.Options);
        });

        // Putting the earlier sitting away is a reading decision on this machine – nothing on Twitch,
        // nothing anyone else sees – so it sits outside the login, next to the local pin and the
        // nicknames. It goes through the app rather than staying in the browser because the same
        // lines are on the overlay and in the file that survives a restart: hiding them in one of the
        // three would only mean meeting them again tomorrow.
        app.MapPost("/api/chat/trim", (TrimHistoryRequest request) =>
        {
            if (request.Before <= 0) return Problem("Vet inte var det tidigare passet slutade.");
            int removed = context.Hub.TrimHistoryBefore(DateTimeOffset.FromUnixTimeMilliseconds(request.Before));
            return Results.Json(new { removed }, DockJson.Options);
        });

        // The IRC socket stays authenticated until it is torn down, so a logout has to be enforced
        // here too – hiding the composer in the dock is not what makes sending stop.
        app.MapPost("/api/chat/send", async (SendMessageRequest request) =>
            await RunAsync(() => context.Session.IsLoggedIn
                ? context.Chat.SendMessageAsync(request.Text)
                : throw new TwitchApiException("Du är inte inloggad på Twitch.")).ConfigureAwait(false));

        app.MapPost("/api/mod/timeout", async (TimeoutRequest request) =>
            await RunAsync(() => context.Api.TimeoutAsync(RequireBroadcaster(), request.UserId, request.Seconds, request.Reason)).ConfigureAwait(false));

        app.MapPost("/api/mod/ban", async (BanRequest request) =>
            await RunAsync(() => context.Api.BanAsync(RequireBroadcaster(), request.UserId, request.Reason)).ConfigureAwait(false));

        app.MapPost("/api/mod/unban", async (UnbanRequest request) =>
            await RunAsync(() => context.Api.UnbanAsync(RequireBroadcaster(), request.UserId)).ConfigureAwait(false));

        app.MapPost("/api/mod/delete", async (DeleteMessageRequest request) =>
            await RunAsync(() => context.Api.DeleteMessageAsync(RequireBroadcaster(), request.MessageId)).ConfigureAwait(false));

        // Only half of pinning is a Twitch call. Nailing a line to the dock's own strip never leaves
        // the browser and needs neither a login nor a mod role, so it has no endpoint at all; putting
        // the same line in front of the viewers is a moderator action and goes through Helix.
        // Twitch pushes nothing back when a pin changes, so nothing here can be polled into a state –
        // these two are the whole feature on this side.
        app.MapPost("/api/chat/pin", async (PinMessageRequest request) =>
            await RunAsync(() => context.Api.PinMessageAsync(RequireBroadcaster(), request.MessageId)).ConfigureAwait(false));

        app.MapPost("/api/chat/unpin", async (PinMessageRequest request) =>
            await RunAsync(() => context.Api.UnpinMessageAsync(RequireBroadcaster(), request.MessageId)).ConfigureAwait(false));

        // What the emote picker may offer. Behind a login for the same reason the composer is: the
        // point of the list is to type something into the chat, and Twitch decides what this account
        // may send. Answered from the app's own copy, which is the same one our echoed lines are
        // drawn from – so the picker and the column can never disagree about what an emote is.
        app.MapGet("/api/emotes", async () =>
        {
            if (!context.Session.IsLoggedIn) return Problem("Du är inte inloggad på Twitch.");
            try
            {
                EmoteCatalog catalog = await context.Emotes
                    .GetAsync(context.Hub.BroadcasterId, context.Session.UserId).ConfigureAwait(false);
                return Results.Json(catalog, DockJson.Options);
            }
            catch (Exception ex) when (ex is TwitchApiException or TwitchAuthException or HttpRequestException)
            { return Problem(ex.Message); }
        });

        app.MapGet("/api/raid/candidates", async () =>
        {
            try { return Results.Json(await context.Api.GetFollowedLiveChannelsAsync().ConfigureAwait(false), DockJson.Options); }
            catch (Exception ex) when (ex is TwitchApiException or TwitchAuthException or HttpRequestException)
            { return Problem(ex.Message); }
        });

        // Twitch only allows raiding out of your own channel, never a channel you merely moderate.
        app.MapPost("/api/raid/start", async (StartRaidRequest request) =>
            await RunAsync(() => context.Api.StartRaidAsync(RequireOwnChannel(), request.UserId)).ConfigureAwait(false));

        app.MapPost("/api/raid/cancel", async () =>
            await RunAsync(() => context.Api.CancelRaidAsync(RequireOwnChannel())).ConfigureAwait(false));

        // Pet drawings live in the user's pets folder, so an edited pet reaches the overlay after a
        // reload without the app shipping a new build.
        app.MapGet("/pets/body/{id}", (string id) =>
            context.Pets.TryGetBody(id, out string svg) ? Results.Text(svg, "image/svg+xml") : Results.NotFound());

        // Pet spritesheets live on disk too. Only ids the catalog itself resolved are served, so
        // the URL can never name an arbitrary file.
        app.MapGet("/pets/sprite/{id}", (string id) =>
        {
            if (!context.Pets.TryGetSpriteFile(id, out string path)) return Results.NotFound();
            string type = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".webp" => "image/webp",
                ".png" => "image/png",
                _ => "application/octet-stream"
            };
            return Results.File(path, type);
        });

        // An explicit catch-all: the default fallback pattern skips paths that look like files,
        // which would leave app.js and styles.css unreachable.
        app.MapFallback("/{**path}", async (HttpContext http) =>
        {
            if (!StaticAssets.TryRead(http.Request.Path.Value ?? "/", out byte[] content, out string contentType))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            http.Response.ContentType = contentType;
            await http.Response.Body.WriteAsync(content).ConfigureAwait(false);
        });
    }

    /// <summary>The channel being moderated. Works in any channel where you hold the mod role.</summary>
    private string RequireBroadcaster() => context.Hub.BroadcasterId.Length > 0
        ? context.Hub.BroadcasterId
        : throw new TwitchApiException("Kanalen är inte ansluten än.");

    /// <summary>
    /// Raiding is only legal out of your own channel. The dock hides the button elsewhere, but the
    /// endpoint has to check it as well so a stray request cannot raid away your own viewers while
    /// you are sitting in someone else's chat as a moderator.
    /// </summary>
    private string RequireOwnChannel()
    {
        string userId = context.Session.UserId;
        if (userId.Length == 0) throw new TwitchApiException("Du är inte inloggad på Twitch.");
        if (context.Hub.BroadcasterId != userId)
            throw new TwitchApiException("Raid går bara att starta från din egen kanal.");
        return userId;
    }

    private static async Task<IResult> RunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return Results.Ok();
        }
        catch (Exception ex) when (ex is TwitchApiException or TwitchAuthException or InvalidOperationException or HttpRequestException)
        {
            return Problem(ex.Message);
        }
    }

    /// <summary>The display name is what the reader sees, so it is what should be read back.</summary>
    private static string Pick(string? displayName, string? login) =>
        !string.IsNullOrWhiteSpace(displayName) ? displayName : login ?? string.Empty;

    private static IResult Problem(string message) =>
        Results.Json(new { error = message }, DockJson.Options, statusCode: StatusCodes.Status400BadRequest);
}
