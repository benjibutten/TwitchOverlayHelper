using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Twitch;

/// <summary>What the app managed to switch on in the connected channel, and what it could not.</summary>
public sealed record EventSubCoverage(bool Redemptions, bool Shoutouts, bool PowerUps, bool HypeTrain, IReadOnlyList<string> MissingScopes)
{
    public static readonly EventSubCoverage Nothing = new(false, false, false, false, []);

    public bool Any => Redemptions || Shoutouts || PowerUps || HypeTrain;
}

/// <summary>
/// EventSub over WebSocket: the things IRC never sends. Sibling to <see cref="TwitchChatClient"/>
/// and deliberately optional – it needs a login, and most of what it carries only works in your own
/// channel. When it cannot run, nothing else changes: the chat keeps reading over IRC exactly as it
/// does for a logged-out viewer.
/// </summary>
public sealed class TwitchEventSubClient(TwitchSession session, TwitchApiClient api) : IAsyncDisposable
{
    private const string SocketUrl = "wss://eventsub.wss.twitch.tv/ws";

    // Twitch allows 300 enabled subscriptions per WebSocket session and 3 sockets with enabled
    // subscriptions per client id. We open one socket and ask for a handful, so neither is close.
    private const int KeepaliveSeconds = 30;

    private CancellationTokenSource? _lifetime;
    private Task? _runTask;
    private EventSubPlan _plan = EventSubPlan.Nothing;
    private EventSubCoverage _covered = EventSubCoverage.Nothing;

    // During a reconnect Twitch keeps delivering on the old socket while the new one comes up, so
    // for a moment both are live and the same notification arrives twice. Without this guard one
    // redemption would print two cards and spawn two pets.
    private readonly RecentMessageIds _seen = new();

    public event Action<ChatEvent>? EventReceived;

    /// <summary>Raised with the redemption details, for the pet rules that spend channel points.</summary>
    public event Action<RewardRedemption>? RedemptionReceived;

    /// <summary>
    /// Raised when a redemption's status changed – fulfilled or cancelled. Mostly this is the app's
    /// own answer coming back to it, which nothing acts on; what it exists for is the other case,
    /// where the streamer works the queue in Twitch's dashboard and a pet has to come down.
    /// </summary>
    public event Action<RedemptionStatusChange>? RedemptionUpdated;

    /// <summary>
    /// Raised when a Gigantify an Emote power-up was used. It is not an event card of its own: it
    /// belongs to a chat line, and the app's job is to find that line and mark it.
    /// </summary>
    public event Action<GigantifiedEmote>? GigantifyReceived;

    /// <summary>
    /// Raised on every step of a hype train. Not an event card: a train is a state that lives for
    /// minutes, so each notification is the whole current picture and replaces the last one.
    /// </summary>
    public event Action<HypeTrainState>? HypeTrainChanged;

    public event Action<string>? StatusChanged;

    /// <summary>Raised once the subscriptions are settled, so the app can say what is switched on.</summary>
    public event Action<EventSubCoverage>? CoverageChanged;

    public bool IsRunning => _runTask is { IsCompleted: false };

    /// <summary>
    /// Decides what is worth even trying in this channel, before a socket is opened. Being logged
    /// out, missing a scope, or watching someone else's channel are all ordinary answers here – they
    /// mean "fewer events", never "something is broken".
    /// </summary>
    public EventSubPlan Plan(string broadcasterId)
    {
        if (!session.IsLoggedIn || broadcasterId.Length == 0) return EventSubPlan.Nothing;

        // Redemptions are readable in your own channel only, whatever the scope says.
        bool ownChannel = string.Equals(broadcasterId, session.UserId, StringComparison.Ordinal);
        // Either scope opens this topic – Twitch accepts the manage scope in place of the read one,
        // and a login granted only the manage half would otherwise lose its redemptions over a
        // permission it already holds.
        bool canReadRedemptions = session.HasScope(TwitchAuth.RedemptionsScope) || session.HasScope(TwitchAuth.ManageRedemptionsScope);
        bool redemptions = ownChannel && canReadRedemptions;
        // Shoutouts need the mod role in that channel, which Twitch will not tell us up front. We
        // ask, and treat a refusal as "not a moderator here" rather than as an error.
        bool shoutouts = session.HasScope(TwitchAuth.ShoutoutsScope);
        // Power-ups are spent in a channel, and only its broadcaster may read them.
        bool powerUps = ownChannel && session.HasScope(TwitchAuth.BitsScope);
        // A hype train belongs to the channel it runs in, and reads the same way.
        bool hypeTrain = ownChannel && session.HasScope(TwitchAuth.HypeTrainScope);

        var missing = new List<string>();
        // Reported as the one thing that is actually missing rather than as both halves: a login
        // holding the manage scope can already read redemptions, so naming the read scope there
        // would send the user off to fix something that is not broken.
        if (ownChannel && !canReadRedemptions) missing.Add(TwitchAuth.RedemptionsScope);
        if (ownChannel && !session.HasScope(TwitchAuth.ManageRedemptionsScope)) missing.Add(TwitchAuth.ManageRedemptionsScope);
        if (!session.HasScope(TwitchAuth.ShoutoutsScope)) missing.Add(TwitchAuth.ShoutoutsScope);
        if (ownChannel && !session.HasScope(TwitchAuth.BitsScope)) missing.Add(TwitchAuth.BitsScope);
        if (ownChannel && !session.HasScope(TwitchAuth.HypeTrainScope)) missing.Add(TwitchAuth.HypeTrainScope);

        return new EventSubPlan(redemptions, shoutouts, powerUps, hypeTrain, missing);
    }

    public Task StartAsync(string broadcasterId)
    {
        if (IsRunning) throw new InvalidOperationException("EventSub körs redan.");
        EventSubPlan plan = Plan(broadcasterId);
        _plan = plan;
        if (!plan.WorthConnecting)
        {
            CoverageChanged?.Invoke(EventSubCoverage.Nothing with { MissingScopes = plan.MissingScopes });
            return Task.CompletedTask;
        }

        _seen.Clear();
        _lifetime?.Dispose();
        _lifetime = new CancellationTokenSource();
        _runTask = RunWithReconnectAsync(broadcasterId, plan, _lifetime.Token);
        return Task.CompletedTask;
    }


    /// <summary>
    /// Says that nothing is covered any more. Everything that can fall back to another route has to
    /// hear this, or an outage would leave those events handled by nobody at all.
    /// </summary>
    private void ReportNothingCovered()
    {
        if (!_covered.Any) return;
        _covered = EventSubCoverage.Nothing with { MissingScopes = _plan.MissingScopes };
        CoverageChanged?.Invoke(_covered);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? lifetime = _lifetime;
        Task? runTask = _runTask;
        lifetime?.Cancel();
        ReportNothingCovered();
        if (runTask is not null)
        {
            try { await runTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        if (ReferenceEquals(_lifetime, lifetime))
        {
            _lifetime = null;
            _runTask = null;
            lifetime?.Dispose();
        }
    }

    /// <summary>Same shape as the chat client's reconnect loop: back off, and never spin on a refusal.</summary>
    private async Task RunWithReconnectAsync(string broadcasterId, EventSubPlan plan, CancellationToken token)
    {
        int attempt = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(SocketUrl, broadcasterId, plan, token).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (TwitchAuthException ex)
            {
                // The login is gone or rejected; retrying with it would loop forever.
                StatusChanged?.Invoke($"Händelser av: {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                attempt++;
                int delay = Math.Min(30, 3 * attempt);
                StatusChanged?.Invoke($"Händelser tappade kontakten – nytt försök om {delay} s ({ex.Message})");
                try { await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                // However a session ended, its events now reach nobody. Anything with a second route
                // has to be told to use it – redemptions that carry text still arrive over IRC, and
                // the pets pick them up again the moment coverage drops. In a finally rather than in
                // each catch, so no exit path can quietly leave the fallback switched off.
                ReportNothingCovered();
            }
        }
    }

    /// <summary>
    /// One EventSub session, across however many sockets it takes. Returns when the connection ends
    /// for good; the caller backs off and starts a new session.
    /// </summary>
    private async Task RunSessionAsync(string url, string broadcasterId, EventSubPlan plan, CancellationToken token)
    {
        // Only the first URL is ours to shape. Twitch's reconnect URL has to be used exactly as sent.
        EventSubSocket current = await OpenAsync(WithKeepalive(url), token).ConfigureAwait(false);
        bool subscribed = false;
        try
        {
            while (true)
            {
                string sessionId = await current.Welcomed.Task.WaitAsync(token).ConfigureAwait(false);
                // A reconnect session inherits the subscriptions; asking again would double every event.
                if (!subscribed)
                {
                    await SubscribeAsync(broadcasterId, plan, sessionId, token).ConfigureAwait(false);
                    subscribed = true;
                }

                Task ended = await Task.WhenAny(current.Reading, current.Reconnect.Task).ConfigureAwait(false);
                if (ended == current.Reading)
                {
                    // Surfaces whatever ended it, so the caller can back off and try again.
                    await current.Reading.ConfigureAwait(false);
                    return;
                }

                // Twitch keeps sending on the old socket until the replacement is established, so the
                // old one is left reading until the new one has welcomed. That overlap is exactly why
                // notifications are de-duplicated: for a moment both sockets carry the same events.
                EventSubSocket next = await OpenAsync(await current.Reconnect.Task.ConfigureAwait(false), token).ConfigureAwait(false);
                try { await next.Welcomed.Task.WaitAsync(token).ConfigureAwait(false); }
                catch { await next.DisposeAsync().ConfigureAwait(false); throw; }

                await current.DisposeAsync().ConfigureAwait(false);
                current = next;
            }
        }
        finally { await current.DisposeAsync().ConfigureAwait(false); }
    }

    private static string WithKeepalive(string url) =>
        $"{url}{(url.Contains('?') ? '&' : '?')}keepalive_timeout_seconds={KeepaliveSeconds}";

    private async Task<EventSubSocket> OpenAsync(string url, CancellationToken token)
    {
        var client = new ClientWebSocket();
        try { await client.ConnectAsync(new Uri(url), token).ConfigureAwait(false); }
        catch { client.Dispose(); throw; }

        var socket = new EventSubSocket(client);
        socket.Reading = ReadAsync(socket, token);
        return socket;
    }

    /// <summary>Reads one socket to its end, announcing welcome and reconnect through the socket's signals.</summary>
    private async Task ReadAsync(EventSubSocket socket, CancellationToken token)
    {
        try
        {
            await foreach (JsonDocument frame in ReadFramesAsync(socket.Client, token).ConfigureAwait(false))
            {
                using (frame)
                {
                    if (!frame.RootElement.TryGetProperty("metadata", out JsonElement metadata)) continue;
                    string type = ReadString(metadata, "message_type");

                    switch (type)
                    {
                        case "session_welcome":
                            socket.Welcomed.TrySetResult(ReadString(Session(frame), "id"));
                            break;
                        case "session_keepalive":
                            break;
                        case "notification":
                            // Only notifications are worth remembering: welcome and reconnect are
                            // already idempotent, and keepalives would crowd real ids out of the buffer.
                            if (_seen.IsNew(ReadString(metadata, "message_id")))
                                Dispatch(ReadString(metadata, "subscription_type"), Payload(frame));
                            break;
                        case "session_reconnect":
                            socket.Reconnect.TrySetResult(ReadString(Session(frame), "reconnect_url"));
                            break;
                        case "revocation":
                            OnRevoked(Payload(frame));
                            break;
                    }
                }
            }
            throw new WebSocketException("Twitch stängde händelseanslutningen.");
        }
        catch (Exception ex)
        {
            // Anyone waiting on a welcome that will now never come has to be let go, or the session
            // loop would wait on it forever.
            socket.Welcomed.TrySetException(ex);
            throw;
        }
    }

    /// <summary>
    /// Twitch revoked a subscription: the app was removed, a scope was withdrawn, or the version
    /// went away. Retrying does not help, so the feature is reported as off – which is what hands
    /// redemptions back to the IRC route instead of leaving them unhandled by both.
    /// </summary>
    private void OnRevoked(JsonElement payload)
    {
        string type = payload.TryGetProperty("subscription", out JsonElement subscription)
            ? ReadString(subscription, "type")
            : string.Empty;

        if (type == "channel.channel_points_custom_reward_redemption.add") _covered = _covered with { Redemptions = false };
        else if (type.StartsWith("channel.shoutout", StringComparison.Ordinal)) _covered = _covered with { Shoutouts = false };
        else if (type == "channel.bits.use") _covered = _covered with { PowerUps = false };
        else if (type.StartsWith("channel.hype_train", StringComparison.Ordinal)) _covered = _covered with { HypeTrain = false };
        else _covered = _covered with { Redemptions = false, Shoutouts = false, PowerUps = false, HypeTrain = false };

        CoverageChanged?.Invoke(_covered);
        StatusChanged?.Invoke("Twitch drog tillbaka en händelseprenumeration – logga in igen om du vill ha den tillbaka.");
    }

    /// <summary>
    /// Asks for each topic in the plan on its own. One refusal must not take the others with it:
    /// not being a moderator is the normal case in someone else's channel, and the redemptions we
    /// came for should still arrive.
    /// </summary>
    private async Task SubscribeAsync(string broadcasterId, EventSubPlan plan, string sessionId, CancellationToken token)
    {
        bool redemptions = false;
        bool shoutouts = false;
        bool powerUps = false;
        bool hypeTrain = false;

        if (plan.Redemptions)
        {
            var condition = new Dictionary<string, string> { ["broadcaster_user_id"] = broadcasterId };
            redemptions = await TrySubscribeAsync("channel.channel_points_custom_reward_redemption.add", "1",
                condition, sessionId, token).ConfigureAwait(false);

            // Asked for separately and never allowed to speak for the redemptions themselves: this
            // one only carries what the streamer does in Twitch's own queue. Losing it costs a pet
            // left standing after a manual refund – worth having, not worth turning the pets off
            // over. Gated on the same scopes as the topic above, because Twitch takes either one
            // here too; asking only when the manage scope is present would turn a permission
            // Twitch grants into one this app withholds.
            if (redemptions)
                await TrySubscribeAsync("channel.channel_points_custom_reward_redemption.update", "1",
                    condition, sessionId, token).ConfigureAwait(false);
        }

        if (plan.Shoutouts)
        {
            var condition = new Dictionary<string, string>
            {
                ["broadcaster_user_id"] = broadcasterId,
                ["moderator_user_id"] = session.UserId
            };
            // Both halves need the same role and scope, so a refusal on the first answers for both
            // and there is no point asking twice. But the status may only claim shoutouts work when
            // both actually got through – half a subscription would mean half the events.
            shoutouts = await TrySubscribeAsync("channel.shoutout.create", "1", condition, sessionId, token).ConfigureAwait(false)
                && await TrySubscribeAsync("channel.shoutout.receive", "1", condition, sessionId, token).ConfigureAwait(false);
        }

        if (plan.PowerUps)
            powerUps = await TrySubscribeAsync("channel.bits.use", "1",
                new Dictionary<string, string> { ["broadcaster_user_id"] = broadcasterId }, sessionId, token).ConfigureAwait(false);

        if (plan.HypeTrain)
        {
            var condition = new Dictionary<string, string> { ["broadcaster_user_id"] = broadcasterId };
            // Version 2, not 1: v1 knows nothing about shared trains or the golden Kappa train.
            //
            // Asked for one at a time rather than chained with &&, because the end is what takes the
            // strip back down: skipping it when an earlier one happened to fail is exactly how a
            // strip ends up standing over a train that finished half an hour ago. A subscription
            // that did get through while another did not is left where it is – websocket
            // subscriptions belong to the session and go when it does, so a reconnect starts over
            // from nothing either way.
            bool begin = await TrySubscribeAsync("channel.hype_train.begin", "2", condition, sessionId, token).ConfigureAwait(false);
            bool progress = await TrySubscribeAsync("channel.hype_train.progress", "2", condition, sessionId, token).ConfigureAwait(false);
            bool end = await TrySubscribeAsync("channel.hype_train.end", "2", condition, sessionId, token).ConfigureAwait(false);
            // Only all three add up to a strip that appears, moves and leaves again; anything less
            // would have the app claim a feature it can only do half of.
            hypeTrain = begin && progress && end;
        }

        _covered = new EventSubCoverage(redemptions, shoutouts, powerUps, hypeTrain, plan.MissingScopes);
        CoverageChanged?.Invoke(_covered);
        StatusChanged?.Invoke(_covered.Any ? "Händelser på" : "Inga extra händelser i den här kanalen");
    }

    private async Task<bool> TrySubscribeAsync(
        string type, string version, Dictionary<string, string> condition, string sessionId, CancellationToken token)
    {
        try
        {
            await api.CreateEventSubSubscriptionAsync(type, version, condition, sessionId, token).ConfigureAwait(false);
            return true;
        }
        catch (TwitchNotPermittedException)
        {
            // Expected whenever we are not the broadcaster or not a moderator here. Not an error.
            return false;
        }
        catch (TwitchApiException)
        {
            return false;
        }
    }

    private void Dispatch(string subscriptionType, JsonElement payload)
    {
        if (!payload.TryGetProperty("event", out JsonElement data)) return;
        DateTimeOffset at = DateTimeOffset.Now;

        switch (subscriptionType)
        {
            case "channel.channel_points_custom_reward_redemption.add":
            {
                // The reward is a nested object; the redemption's own id is the top-level "id".
                JsonElement reward = data.TryGetProperty("reward", out JsonElement nested) ? nested : default;
                var redemption = new RewardRedemption(
                    ReadString(data, "id"),
                    ReadString(reward, "id"),
                    ReadString(reward, "title"),
                    ReadInt(reward, "cost"),
                    ReadString(data, "user_id"),
                    ReadString(data, "user_login"),
                    ReadString(data, "user_name"),
                    EmptyToNull(ReadString(data, "user_input")),
                    at);
                RedemptionReceived?.Invoke(redemption);

                // A redemption that carries text also arrives as a normal chat line over IRC, where
                // the viewer's words already show. Giving it a card here as well would say the same
                // thing twice, so only the silent rewards – the ones nothing else can show – get one.
                if (redemption.UserInput is null)
                    EventReceived?.Invoke(redemption.ToChatEvent());
                return;
            }
            case "channel.channel_points_custom_reward_redemption.update":
            {
                JsonElement reward = data.TryGetProperty("reward", out JsonElement nested) ? nested : default;
                RedemptionUpdated?.Invoke(new RedemptionStatusChange(
                    ReadString(data, "id"),
                    ReadString(reward, "id"),
                    ReadString(data, "status")));
                return;
            }
            case "channel.bits.use":
                DispatchBitsUse(data, at);
                return;
            case "channel.shoutout.create":
                EventReceived?.Invoke(new ChatEvent(ChatEventType.ShoutoutSent, Guid.NewGuid().ToString("N"),
                    ReadString(data, "moderator_user_name"), at)
                {
                    UserLogin = ReadString(data, "moderator_user_login"),
                    UserId = ReadString(data, "moderator_user_id"),
                    RecipientDisplayName = EmptyToNull(ReadString(data, "to_broadcaster_user_name"))
                });
                return;
            case "channel.shoutout.receive":
                EventReceived?.Invoke(new ChatEvent(ChatEventType.ShoutoutReceived, Guid.NewGuid().ToString("N"),
                    ReadString(data, "from_broadcaster_user_name"), at)
                {
                    UserLogin = ReadString(data, "from_broadcaster_user_login"),
                    UserId = ReadString(data, "from_broadcaster_user_id"),
                    ViewerCount = ReadInt(data, "viewer_count")
                });
                return;
            case "channel.hype_train.begin":
                HypeTrainChanged?.Invoke(ReadHypeTrain(data, HypeTrainPhase.Begin, at));
                return;
            case "channel.hype_train.progress":
                HypeTrainChanged?.Invoke(ReadHypeTrain(data, HypeTrainPhase.Progress, at));
                return;
            case "channel.hype_train.end":
                HypeTrainChanged?.Invoke(ReadHypeTrain(data, HypeTrainPhase.Ended, at));
                return;
        }
    }

    /// <summary>
    /// One hype train notification as the app's whole picture of that train. The end payload carries
    /// neither progress nor goal – there is no next level to reach – so both read as zero, which is
    /// what makes the strip drop its bar rather than freeze it mid-climb.
    /// </summary>
    private static HypeTrainState ReadHypeTrain(JsonElement data, HypeTrainPhase phase, DateTimeOffset at) => new(
        ReadString(data, "id"),
        phase,
        ReadInt(data, "level") ?? 0,
        ReadInt(data, "progress") ?? 0,
        ReadInt(data, "goal") ?? 0,
        ReadInt(data, "total") ?? 0,
        at)
    {
        Kind = EmptyToNull(ReadString(data, "type")),
        IsShared = data.TryGetProperty("is_shared_train", out JsonElement shared) && shared.ValueKind == JsonValueKind.True,
        ExpiresAt = ReadTime(data, "expires_at"),
        TopContributions = ReadContributions(data)
    };

    /// <summary>
    /// Who is carrying the train, kept in the order Twitch ranked them. Sorting them here would mean
    /// comparing a bits count against a subscription tier price, which are the same points only
    /// when a gift batch counts as one subscription – and the payload does not say whether it does.
    /// </summary>
    private static IReadOnlyList<HypeTrainContribution> ReadContributions(JsonElement data)
    {
        if (!data.TryGetProperty("top_contributions", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return [];

        var result = new List<HypeTrainContribution>();
        foreach (JsonElement item in list.EnumerateArray())
        {
            string name = ReadString(item, "user_name");
            if (name.Length == 0) continue;
            result.Add(new HypeTrainContribution(name, ReadString(item, "type"), ReadInt(item, "total") ?? 0));
        }
        return result;
    }

    /// <summary>
    /// Bits were spent. The three built-in power-ups each want something different, and two of the
    /// four ways to spend bits deliberately produce nothing here:
    /// <list type="bullet">
    /// <item>a plain cheer is an ordinary PRIVMSG with a bits tag, already shown by the IRC route –
    /// giving it a card here would say it twice, and would say it only for the broadcaster;</item>
    /// <item>a message effect is the one power-up IRC does carry, in the animation-id tag on the
    /// message it belongs to, so it is read there and needs nothing from us;</item>
    /// <item>a gigantified emote belongs to a chat line and is handed to the tracker that finds it;</item>
    /// <item>a celebration is the only one with no message to attach to, so it gets its own card.</item>
    /// </list>
    /// Custom power-ups are a broadcaster's own bits rewards; they are left alone until there is
    /// somewhere sensible to show them.
    /// </summary>
    private void DispatchBitsUse(JsonElement data, DateTimeOffset at)
    {
        if (ReadString(data, "type") != "power_up") return;
        if (!data.TryGetProperty("power_up", out JsonElement powerUp) || powerUp.ValueKind != JsonValueKind.Object) return;

        switch (ReadString(powerUp, "type"))
        {
            case "gigantify_an_emote":
            {
                JsonElement emote = powerUp.TryGetProperty("emote", out JsonElement nested) ? nested : default;
                string text = data.TryGetProperty("message", out JsonElement message)
                    ? ReadString(message, "text")
                    : string.Empty;
                GigantifyReceived?.Invoke(new GigantifiedEmote(ReadString(data, "user_id"), ReadString(emote, "id"), text, at));
                return;
            }
            case "celebration":
                EventReceived?.Invoke(new ChatEvent(ChatEventType.Celebration, Guid.NewGuid().ToString("N"),
                    ReadString(data, "user_name"), at)
                {
                    UserLogin = ReadString(data, "user_login"),
                    UserId = ReadString(data, "user_id"),
                    Bits = ReadInt(data, "bits")
                });
                return;
        }
    }

    /// <summary>Reads whole text frames off the socket, reassembling the ones Twitch splits.</summary>
    private static async IAsyncEnumerable<JsonDocument> ReadFramesAsync(
        ClientWebSocket socket,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        byte[] buffer = new byte[16 * 1024];
        using var frameBytes = new MemoryStream();
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            frameBytes.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) yield break;
                frameBytes.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;

            JsonDocument? document = null;
            try { document = JsonDocument.Parse(Encoding.UTF8.GetString(frameBytes.GetBuffer(), 0, checked((int)frameBytes.Length))); }
            catch (JsonException) { }
            // A frame we cannot parse is worth skipping, never worth ending the connection over.
            if (document is not null) yield return document;
        }
    }

    private static JsonElement Payload(JsonDocument frame) =>
        frame.RootElement.TryGetProperty("payload", out JsonElement payload) ? payload : default;

    private static JsonElement Session(JsonDocument frame) =>
        Payload(frame).TryGetProperty("session", out JsonElement session) ? session : default;

    /// <summary>
    /// One socket being read, with the two things the session loop waits on. Kept as its own object
    /// so a reconnect can hold two of them open at once – which is what the docs require: the old
    /// connection stays until the new one has welcomed.
    /// </summary>
    private sealed class EventSubSocket(ClientWebSocket client) : IAsyncDisposable
    {
        public ClientWebSocket Client { get; } = client;

        public TaskCompletionSource<string> Welcomed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> Reconnect { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reading { get; set; } = Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            // Aborting rather than closing politely: the reader is blocked on a receive, and a
            // handshake we would have to wait for is not worth it when we are leaving anyway.
            try { Client.Abort(); } catch (ObjectDisposedException) { }
            // The read task always ends in an exception once the socket is gone; observing it here
            // is what keeps it from surfacing as an unobserved task exception later.
            try { await Reading.ConfigureAwait(false); } catch (Exception) { }
            Client.Dispose();
        }
    }

    private static string ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int? ReadInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int number) && number > 0
            ? number
            : null;

    /// <summary>Twitch writes its timestamps as RFC 3339 with more precision than DateTimeOffset keeps.</summary>
    private static DateTimeOffset? ReadTime(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed
            : null;

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

/// <summary>What is worth subscribing to in a channel, decided before any request is sent.</summary>
public sealed record EventSubPlan(bool Redemptions, bool Shoutouts, bool PowerUps, bool HypeTrain, IReadOnlyList<string> MissingScopes)
{
    public static readonly EventSubPlan Nothing = new(false, false, false, false, []);

    public bool WorthConnecting => Redemptions || Shoutouts || PowerUps || HypeTrain;
}

/// <summary>
/// A Gigantify an Emote power-up. The message it enlarged arrives separately over IRC and Twitch
/// sends nothing that ties the two together, so <see cref="PowerUpTracker"/> pairs them up by who
/// wrote them and by the text itself.
/// </summary>
public sealed record GigantifiedEmote(string UserId, string EmoteId, string Text, DateTimeOffset At);

/// <summary>
/// A redemption that changed status. <paramref name="Status"/> is Twitch's own wording –
/// FULFILLED or CANCELED – kept as it arrived rather than parsed into something narrower, so an
/// unexpected value passes through as itself instead of quietly becoming the wrong one.
/// </summary>
public sealed record RedemptionStatusChange(string RedemptionId, string RewardId, string Status);

/// <summary>
/// A channel point redemption, with the name and price IRC never carries. Kept separate from
/// <see cref="ChatEvent"/> because the pet rules act on it whether or not it earns a card.
/// </summary>
public sealed record RewardRedemption(
    string Id,
    string RewardId,
    string RewardTitle,
    int? RewardCost,
    string UserId,
    string UserLogin,
    string DisplayName,
    string? UserInput,
    DateTimeOffset At)
{
    public ChatEvent ToChatEvent() => new(ChatEventType.RewardRedemption, Id, DisplayName, At)
    {
        UserLogin = UserLogin,
        UserId = UserId,
        Message = UserInput,
        RewardId = RewardId,
        RewardTitle = string.IsNullOrWhiteSpace(RewardTitle) ? null : RewardTitle,
        RewardCost = RewardCost
    };
}
