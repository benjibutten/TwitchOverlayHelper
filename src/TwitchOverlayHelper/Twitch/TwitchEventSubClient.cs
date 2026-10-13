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

    /// <summary>
    /// Whether the channel's own bits Power-ups are being delivered. Needs the same scope as
    /// <see cref="PowerUps"/> but is a subscription of its own, so the two can differ.
    /// </summary>
    public bool CustomPowerUps { get; init; }

    public bool Any => Redemptions || Shoutouts || PowerUps || CustomPowerUps || HypeTrain;
}

/// <summary>
/// EventSub over WebSocket: the things IRC never sends. Sibling to <see cref="TwitchChatClient"/>
/// and optional – it needs a login, and most of what it carries only works in your own channel.
/// When it cannot run, the chat keeps reading over IRC.
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

    // During a reconnect Twitch delivers on both the old and the new socket for a moment, so the
    // same notification arrives twice.
    private readonly RecentMessageIds _seen = new();

    public event Action<ChatEvent>? EventReceived;

    /// <summary>Raised with the redemption details, for the pet rules that spend channel points.</summary>
    public event Action<RewardRedemption>? RedemptionReceived;

    /// <summary>
    /// Raised when a redemption's status changed – fulfilled or cancelled. This includes the app's
    /// own answers as well as changes the streamer makes in Twitch's dashboard queue.
    /// </summary>
    public event Action<RedemptionStatusChange>? RedemptionUpdated;

    /// <summary>
    /// Raised when a Gigantify an Emote power-up was used. It belongs to a chat line, which
    /// arrives separately over IRC.
    /// </summary>
    public event Action<GigantifiedEmote>? GigantifyReceived;

    /// <summary>
    /// Raised when a viewer redeemed one of the channel's own custom Power-ups, with the text the
    /// viewer typed.
    /// </summary>
    public event Action<CustomPowerUpRedemption>? CustomPowerUpReceived;

    /// <summary>
    /// Raised on every step of a hype train. Each notification is the train's whole current state
    /// and replaces the last one.
    /// </summary>
    public event Action<HypeTrainState>? HypeTrainChanged;

    public event Action<string>? StatusChanged;

    /// <summary>Raised once the subscriptions are settled, so the app can say what is switched on.</summary>
    public event Action<EventSubCoverage>? CoverageChanged;

    public bool IsRunning => _runTask is { IsCompleted: false };

    /// <summary>
    /// Decides what is worth subscribing to in this channel, before a socket is opened. Being
    /// logged out, missing a scope, or watching someone else's channel yields a smaller plan, not
    /// an error.
    /// </summary>
    public EventSubPlan Plan(string broadcasterId)
    {
        if (!session.IsLoggedIn || broadcasterId.Length == 0) return EventSubPlan.Nothing;

        // Redemptions are readable in your own channel only, whatever the scope says.
        bool ownChannel = string.Equals(broadcasterId, session.UserId, StringComparison.Ordinal);
        // Twitch accepts the manage scope in place of the read one for this topic.
        bool canReadRedemptions = session.HasScope(TwitchAuth.RedemptionsScope) || session.HasScope(TwitchAuth.ManageRedemptionsScope);
        bool redemptions = ownChannel && canReadRedemptions;
        // Shoutouts need the mod role in that channel, which Twitch will not tell us up front, so a
        // refused subscription means "not a moderator here".
        bool shoutouts = session.HasScope(TwitchAuth.ShoutoutsScope);
        // Power-ups are spent in a channel, and only its broadcaster may read them.
        bool powerUps = ownChannel && session.HasScope(TwitchAuth.BitsScope);
        // A hype train belongs to the channel it runs in, and reads the same way.
        bool hypeTrain = ownChannel && session.HasScope(TwitchAuth.HypeTrainScope);

        var missing = new List<string>();
        // The read scope is only missing when the manage scope cannot stand in for it.
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
    /// Raises <see cref="CoverageChanged"/> with nothing covered, so that listeners with another
    /// route, such as IRC for redemptions, fall back to it.
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
                // In a finally so every exit path tells listeners to fall back: redemptions that
                // carry text still arrive over IRC, and the pets use that route once coverage drops.
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
                // old one is left reading until the new one has welcomed.
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
                            // Only notifications are de-duplicated: welcome and reconnect are
                            // idempotent, and keepalives would crowd real ids out of the buffer.
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
            // Releases the session loop if it is waiting on a welcome that will never come.
            socket.Welcomed.TrySetException(ex);
            throw;
        }
    }

    /// <summary>
    /// Twitch revoked a subscription: the app was removed, a scope was withdrawn, or the version
    /// went away. Retrying does not help, so the feature is reported as off, which hands
    /// redemptions back to the IRC route.
    /// </summary>
    private void OnRevoked(JsonElement payload)
    {
        string type = payload.TryGetProperty("subscription", out JsonElement subscription)
            ? ReadString(subscription, "type")
            : string.Empty;

        if (type == "channel.channel_points_custom_reward_redemption.add") _covered = _covered with { Redemptions = false };
        else if (type.StartsWith("channel.shoutout", StringComparison.Ordinal)) _covered = _covered with { Shoutouts = false };
        else if (type == "channel.bits.use") _covered = _covered with { PowerUps = false };
        else if (type == "channel.custom_power_up_redemption.add") _covered = _covered with { CustomPowerUps = false };
        else if (type.StartsWith("channel.hype_train", StringComparison.Ordinal)) _covered = _covered with { HypeTrain = false };
        else _covered = _covered with { Redemptions = false, Shoutouts = false, PowerUps = false, CustomPowerUps = false, HypeTrain = false };

        CoverageChanged?.Invoke(_covered);
        StatusChanged?.Invoke("Twitch drog tillbaka en händelseprenumeration – logga in igen om du vill ha den tillbaka.");
    }

    /// <summary>
    /// Asks for each topic in the plan on its own, so one refusal does not take the others with
    /// it, and reports the result through <see cref="CoverageChanged"/>.
    /// </summary>
    private async Task SubscribeAsync(string broadcasterId, EventSubPlan plan, string sessionId, CancellationToken token)
    {
        bool redemptions = false;
        bool shoutouts = false;
        bool powerUps = false;
        bool customPowerUps = false;
        bool hypeTrain = false;

        if (plan.Redemptions)
        {
            var condition = new Dictionary<string, string> { ["broadcaster_user_id"] = broadcasterId };
            redemptions = await TrySubscribeAsync("channel.channel_points_custom_reward_redemption.add", "1",
                condition, sessionId, token).ConfigureAwait(false);

            // Its result does not affect coverage: losing it only costs a pet left standing after a
            // refund made in Twitch's own queue. Twitch takes either redemption scope here too, so
            // it is not gated on the manage scope.
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
            // Both halves need the same role and scope, so a refusal on the first answers for both.
            // Shoutouts only count as covered when both got through.
            shoutouts = await TrySubscribeAsync("channel.shoutout.create", "1", condition, sessionId, token).ConfigureAwait(false)
                && await TrySubscribeAsync("channel.shoutout.receive", "1", condition, sessionId, token).ConfigureAwait(false);
        }

        if (plan.PowerUps)
        {
            var condition = new Dictionary<string, string> { ["broadcaster_user_id"] = broadcasterId };
            powerUps = await TrySubscribeAsync("channel.bits.use", "1", condition, sessionId, token).ConfigureAwait(false);

            // Tracked separately from the power-ups above: this one carries the paid readings, the
            // other the enlarged emotes.
            customPowerUps = await TrySubscribeAsync("channel.custom_power_up_redemption.add", "1",
                condition, sessionId, token).ConfigureAwait(false);
        }

        if (plan.HypeTrain)
        {
            var condition = new Dictionary<string, string> { ["broadcaster_user_id"] = broadcasterId };
            // Version 2, not 1: v1 knows nothing about shared trains or the golden Kappa train.
            //
            // Not chained with &&: the end topic is what takes the strip down, so it must be asked
            // for even when an earlier one failed. A partial set is not cleaned up, because
            // websocket subscriptions go when the session does.
            bool begin = await TrySubscribeAsync("channel.hype_train.begin", "2", condition, sessionId, token).ConfigureAwait(false);
            bool progress = await TrySubscribeAsync("channel.hype_train.progress", "2", condition, sessionId, token).ConfigureAwait(false);
            bool end = await TrySubscribeAsync("channel.hype_train.end", "2", condition, sessionId, token).ConfigureAwait(false);
            // Only all three add up to a strip that appears, moves and leaves again.
            hypeTrain = begin && progress && end;
        }

        _covered = new EventSubCoverage(redemptions, shoutouts, powerUps, hypeTrain, plan.MissingScopes)
        {
            CustomPowerUps = customPowerUps
        };
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

                // A redemption that carries text also arrives as a normal chat line over IRC, so
                // only the rewards without text get a card.
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
            case "channel.custom_power_up_redemption.add":
            {
                JsonElement powerUp = data.TryGetProperty("custom_power_up", out JsonElement nested) ? nested : default;
                CustomPowerUpReceived?.Invoke(new CustomPowerUpRedemption(
                    ReadString(data, "id"),
                    ReadString(powerUp, "id"),
                    ReadString(powerUp, "title"),
                    ReadInt(powerUp, "bits") ?? 0,
                    ReadString(data, "user_id"),
                    ReadString(data, "user_login"),
                    ReadString(data, "user_name"),
                    EmptyToNull(ReadString(data, "user_input")),
                    at));
                return;
            }
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
    /// One hype train notification as the app's whole picture of that train. The end payload
    /// carries neither progress nor goal, so both read as zero.
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
    /// Who is carrying the train, in the order Twitch ranked them. Not re-sorted: bits and
    /// subscription totals are not comparable from the payload alone.
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
    /// Handles channel.bits.use. Only two of the ways to spend bits produce anything here:
    /// <list type="bullet">
    /// <item>a plain cheer is an ordinary PRIVMSG with a bits tag, already shown by the IRC route;</item>
    /// <item>a message effect arrives over IRC in the animation-id tag of its message;</item>
    /// <item>a gigantified emote belongs to a chat line and is raised through <see cref="GigantifyReceived"/>;</item>
    /// <item>a celebration has no message to attach to, so it gets its own card.</item>
    /// </list>
    /// A channel's own custom Power-up also arrives here, as type <c>custom_power_up</c>, and is
    /// ignored: its own topic carries the redemption's id and what the viewer typed, and acting on
    /// both would answer one redemption twice.
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
    /// One socket being read, with the two signals the session loop waits on. A reconnect holds
    /// two of them open at once: Twitch requires the old connection to stay until the new one has
    /// welcomed.
    /// </summary>
    private sealed class EventSubSocket(ClientWebSocket client) : IAsyncDisposable
    {
        public ClientWebSocket Client { get; } = client;

        public TaskCompletionSource<string> Welcomed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> Reconnect { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reading { get; set; } = Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            // Aborted rather than closed: the reader is blocked on a receive, and a close handshake
            // would have to be waited for.
            try { Client.Abort(); } catch (ObjectDisposedException) { }
            // Observed here so the read task's exception does not surface as unobserved later.
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
/// A Gigantify an Emote power-up. The message it enlarged arrives separately over IRC;
/// <see cref="PowerUpTracker"/> pairs the two up.
/// </summary>
public sealed record GigantifiedEmote(string UserId, string EmoteId, string Text, DateTimeOffset At);

/// <summary>
/// One redemption of a channel's own bits Power-up.
///
/// <para>Shaped like <see cref="RewardRedemption"/>, but Twitch offers no endpoint to fulfil,
/// cancel or refund one of these: the bits are spent when the notification is sent.
/// <paramref name="Id"/> is only an identity for this app's own bookkeeping.</para>
/// </summary>
public sealed record CustomPowerUpRedemption(
    string Id,
    string PowerUpId,
    string Title,
    int Bits,
    string UserId,
    string UserLogin,
    string DisplayName,
    string? UserInput,
    DateTimeOffset At);

/// <summary>
/// A redemption that changed status. <paramref name="Status"/> is Twitch's own wording –
/// FULFILLED or CANCELED – passed through unparsed, so an unexpected value stays visible.
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
