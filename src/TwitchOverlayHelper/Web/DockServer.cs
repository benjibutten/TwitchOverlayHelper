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
using TwitchOverlayHelper.Settings;
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
            channel = context.Settings.Channel
        }, DockJson.Options));

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

    private static IResult Problem(string message) =>
        Results.Json(new { error = message }, DockJson.Options, statusCode: StatusCodes.Status400BadRequest);
}
