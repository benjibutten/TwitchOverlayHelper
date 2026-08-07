using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Pets;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Web;

/// <summary>
/// Fan-out point between the single Twitch connection and every dock that is open. Holds the
/// recent history so a dock that reconnects (OBS restart, page reload) is not staring at an
/// empty column.
/// </summary>
public sealed class ChatHub(AppSettings settings, TwitchBadgeCatalog badges, TwitchSession session, PetRegistry pets, PetCatalog petCatalog)
{
    private const int HistoryLimit = 200;

    private readonly ConcurrentDictionary<Guid, Channel<string>> _clients = new();
    // Messages and events share one queue, so a sub notice keeps its place between the lines it
    // landed between even after the dock reconnects and replays the history.
    private readonly Queue<ChatTimelineItem> _history = new();
    private readonly Lock _historyLock = new();

    private string _statusText = "Inte ansluten";
    private string _statusState = "idle";
    private string _channel = string.Empty;
    private bool _showingSamples;

    public int ClientCount => _clients.Count;

    /// <summary>Twitch room id of the joined channel; needed as broadcaster_id for moderation.</summary>
    public string BroadcasterId { get; set; } = string.Empty;

    /// <summary>
    /// Whether name pronunciation is set up. The dock hides the speaker button when it is not, so
    /// nobody meets a button that only ever answers "fyll i API-nycklar".
    /// </summary>
    public bool SpeechEnabled { get; set; }

    /// <summary>
    /// Points the dock at another channel. The previous channel's lines are dropped so a dock that
    /// reconnects – or one that is open right now – never shows chat from a room we have left.
    /// </summary>
    public void SetChannel(string channel)
    {
        bool changed = !string.Equals(_channel, channel, StringComparison.OrdinalIgnoreCase);
        _channel = channel;
        BroadcasterId = string.Empty;

        // Samples are not anyone's chat, so they stay until the first real message replaces them.
        if (!changed || _showingSamples) return;
        lock (_historyLock) _history.Clear();
        Send(DockJson.Serialize(new DockEnvelope<object?>("clear", null)));
    }

    public void PublishMessage(ChatMessage message)
    {
        Remember(ChatTimelineItem.Of(message));
        Send(DockJson.Serialize(new DockEnvelope<DockMessage>("message", ToDock(message))));
    }

    public void PublishEvent(ChatEvent chatEvent)
    {
        Remember(ChatTimelineItem.Of(chatEvent));
        Send(DockJson.Serialize(new DockEnvelope<DockEvent>("event", DockMapper.ToDock(chatEvent))));
    }

    /// <summary>
    /// Sends a line that is already on screen again, changed. One thing needs this: a Gigantify an
    /// Emote power-up arrives on a different connection than the message it enlarged, and when it
    /// arrives second the only alternative would be to hold every chat line back long enough for a
    /// power-up that almost never comes.
    ///
    /// The history is rewritten too, so a dock that reconnects afterwards replays the marked version
    /// rather than the line as it first went out.
    /// </summary>
    public void PublishMessageUpdate(ChatMessage message)
    {
        lock (_historyLock)
        {
            ChatTimelineItem[] items = _history.ToArray();
            bool found = false;
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].Message is not { } existing || !string.Equals(existing.Id, message.Id, StringComparison.Ordinal)) continue;
                items[i] = ChatTimelineItem.Of(message);
                found = true;
                break;
            }
            // Already scrolled out of the history: the docks that still show it get the update below,
            // and there is nothing left to rewrite for the ones that reconnect.
            if (found)
            {
                _history.Clear();
                foreach (ChatTimelineItem item in items) _history.Enqueue(item);
            }
        }
        Send(DockJson.Serialize(new DockEnvelope<DockMessage>("messageUpdate", ToDock(message))));
    }

    private void Remember(ChatTimelineItem item)
    {
        // The first real line replaces the sample lines that showed what the dock looks like.
        if (_showingSamples)
        {
            _showingSamples = false;
            lock (_historyLock) _history.Clear();
            Send(DockJson.Serialize(new DockEnvelope<object?>("clear", null)));
        }

        lock (_historyLock)
        {
            _history.Enqueue(item);
            while (_history.Count > HistoryLimit) _history.Dequeue();
        }
    }

    public void PublishModeration(ChatModerationEvent moderation)
    {
        lock (_historyLock)
        {
            // Drop the affected messages from history so a reconnecting dock never resurrects them.
            ChatTimelineItem[] kept = _history.Where(item => !IsAffected(item, moderation)).ToArray();
            _history.Clear();
            foreach (ChatTimelineItem item in kept) _history.Enqueue(item);
        }
        Send(DockJson.Serialize(new DockEnvelope<DockModerationPayload>("moderation", DockMapper.ToDock(moderation))));
    }

    /// <summary>
    /// Moderation reaches messages only. A sub or a raid is not something a timeout takes back, and
    /// the views leave event cards standing for the same reason.
    /// </summary>
    private static bool IsAffected(ChatTimelineItem item, ChatModerationEvent moderation) =>
        item.Message is { } message && moderation.Kind switch
        {
            ChatEventKind.ChatCleared => true,
            ChatEventKind.MessageDeleted => string.Equals(message.Id, moderation.TargetMessageId, StringComparison.Ordinal),
            _ => (moderation.TargetUserId is { Length: > 0 } id && string.Equals(message.UserId, id, StringComparison.Ordinal))
                 || (moderation.TargetLogin is { Length: > 0 } login && string.Equals(message.UserLogin, login, StringComparison.OrdinalIgnoreCase))
        };

    public void PublishStatus(string text, string state)
    {
        _statusText = text;
        _statusState = state;
        Send(DockJson.Serialize(new DockEnvelope<DockStatus>("status", new DockStatus(text, state))));
    }

    public void PublishSettings() =>
        Send(DockJson.Serialize(new DockEnvelope<DockSettings>("settings", settings.Dock)));

    public void PublishAuth(bool canSend) =>
        Send(DockJson.Serialize(new DockEnvelope<DockAuth>("auth", BuildAuth(canSend))));

    public void PublishSpeech() =>
        Send(DockJson.Serialize(new DockEnvelope<DockSpeech>("speech", new DockSpeech(SpeechEnabled))));

    /// <summary>Tells every dock that badge images just became available, so it can re-render.</summary>
    public void PublishBadgesLoaded() =>
        Send(DockJson.Serialize(new DockEnvelope<object?>("badgesLoaded", null)));

    public void PublishPetSpawn(PetSpawnResult result) =>
        Send(DockJson.Serialize(new DockEnvelope<DockPetSpawn>("petSpawn",
            new DockPetSpawn(ToDock(result.Pet), result.RemovedId, result.Extended))));

    /// <summary>Pushes size and limits to the pet overlay so slider changes land without a reload.</summary>
    public void PublishPetSettings() =>
        Send(DockJson.Serialize(new DockEnvelope<DockPetSettings>("petSettings", BuildPetSettings())));

    /// <summary>Hands the overlay the species list again, after the user reloads the pets folder.</summary>
    public void PublishPetCatalog() =>
        Send(DockJson.Serialize(new DockEnvelope<IReadOnlyList<DockPetDefinition>>("petCatalog", BuildPetCatalog())));

    private DockPetSettings BuildPetSettings() => new(
        settings.Pets.Enabled, settings.Pets.Scale, settings.Pets.LifetimeMinutes, settings.Pets.MaxPets, settings.Pets.ShowNames);

    private IReadOnlyList<DockPetDefinition> BuildPetCatalog() => petCatalog.Pets
        .Select(pet => pet.SpriteFile is { Length: > 0 }
            ? new DockPetDefinition(pet.Id, pet.Name, pet.Description, "sprite", null, $"/pets/sprite/{pet.Id}", pet.Fps, pet.Emoji, pet.SpriteVersion)
            : new DockPetDefinition(pet.Id, pet.Name, pet.Description, "svg", $"/pets/body/{pet.Id}", null, pet.Fps, pet.Emoji))
        .ToArray();

    private static DockPet ToDock(PetState pet) => new(pet.Id, pet.Name, pet.Color, pet.Species, pet.SpawnedAt, pet.ExpiresAt);

    internal DockAuth BuildAuth(bool canSend)
    {
        SessionState state = session.Snapshot();
        return new DockAuth(
            state.IsLoggedIn,
            state.Login,
            // An IRC socket opened while logged in stays authenticated until it is torn down, so a
            // logout has to take the composer away immediately rather than when the socket closes.
            canSend && state.IsLoggedIn,
            // Twitch only lets you raid out of your own channel, so the button is pointless
            // while watching someone else's chat as a moderator.
            state.IsLoggedIn && BroadcasterId.Length > 0 && BroadcasterId == state.UserId,
            state.Error);
    }

    /// <summary>
    /// Sample lines so an unconnected dock shows what the reading settings actually look like,
    /// rather than an empty column.
    /// </summary>
    public void ShowSamples()
    {
        var now = DateTimeOffset.Now;
        ChatMessage[] samples =
        [
            Sample("sample-1", "Streamern", "Så här ser chatten ut. Ändra utseendet i appen – ändringarna syns direkt här.", "#A970FF", [new ChatBadge("broadcaster", "1")], now),
            Sample("sample-2", "Kajsa_92", "vilket spel är det här? ser jättemysigt ut", "#1F9D55", [new ChatBadge("subscriber", "6")], now),
            Sample("sample-3", "NyTittare", "hej! första gången här", "#C0287F", [], now),
            Sample("sample-4", "Pelle", "TROR DU KLARAR DEN HÄR BOSSEN NU", "#D13C3C", [new ChatBadge("moderator", "1")], now),
            Sample("sample-5", "Botten", "!discord", "#6B7280", [], now),
            Sample("sample-6", "Lisa", "klicka på ett namn för att testa timeout och ban", "#0F7C8A", [new ChatBadge("vip", "1")], now)
        ];

        var sampleEvent = new ChatEvent(ChatEventType.Subscription, "sample-7", "Kajsa_92", now)
        {
            UserLogin = "kajsa_92",
            Months = 8,
            Tier = "1000",
            Message = "tack för alla mysiga kvällar!"
        };

        _showingSamples = true;
        lock (_historyLock)
        {
            _history.Clear();
            foreach (ChatMessage sample in samples) _history.Enqueue(ChatTimelineItem.Of(sample));
            _history.Enqueue(ChatTimelineItem.Of(sampleEvent));
        }
        foreach (ChatMessage sample in samples)
            Send(DockJson.Serialize(new DockEnvelope<DockMessage>("message", ToDock(sample))));
        Send(DockJson.Serialize(new DockEnvelope<DockEvent>("event", DockMapper.ToDock(sampleEvent))));
    }

    private static ChatMessage Sample(string id, string name, string text, string color, ChatBadge[] badges, DateTimeOffset at) =>
        new(id, name, text, color, badges, id == "sample-3", false, at) { UserLogin = name.ToLowerInvariant() };

    private string BuildHello(bool canSend)
    {
        ChatTimelineItem[] history;
        lock (_historyLock) history = _history.ToArray();

        string mentionName = settings.UserName.Length > 0 ? settings.UserName : settings.Channel;
        return DockJson.Serialize(new DockHello(
            "hello",
            settings.Dock,
            new DockStatus(_statusText, _statusState),
            BuildAuth(canSend),
            _channel,
            mentionName,
            SpeechEnabled,
            history.Select(ToDock).ToArray(),
            BuildPetSettings(),
            BuildPetCatalog(),
            pets.Snapshot().Select(ToDock).ToArray()));
    }

    private DockMessage ToDock(ChatMessage message) => DockMapper.ToDock(message, badge =>
        badges.TryGet(badge.SetId, badge.Version, out BadgeInfo? info) ? (info!.ImageUrl, info.Title) : (null, null));

    /// <summary>History travels as tagged items so the dock can replay it through the same code
    /// that handles live frames, and in the order the lines actually arrived.</summary>
    private DockHistoryItem ToDock(ChatTimelineItem item) => item.Event is { } chatEvent
        ? new DockHistoryItem("event", null, DockMapper.ToDock(chatEvent))
        : new DockHistoryItem("message", ToDock(item.Message!), null);

    private void Send(string payload)
    {
        foreach (Channel<string> outbound in _clients.Values)
        {
            // A dock that cannot keep up is dropped rather than allowed to stall the whole fan-out.
            if (!outbound.Writer.TryWrite(payload)) outbound.Writer.TryComplete();
        }
    }

    /// <summary>Runs one dock connection until the socket closes.</summary>
    public async Task RunClientAsync(WebSocket socket, bool canSend, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        // Never DropOldest: the queue carries clear and moderation frames as well as chat lines, and
        // silently dropping one of those leaves a deleted message on screen. When a dock falls this
        // far behind it is dropped instead, and the reconnect hands it a correct history.
        var outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
        _clients[id] = outbound;

        try
        {
            await SendFrameAsync(socket, BuildHello(canSend), cancellationToken).ConfigureAwait(false);
            // Completing the outbound channel when the socket closes is what ends the loop below;
            // without it an idle dock would sit in _clients until the next message happened to arrive.
            Task drain = DrainThenCompleteAsync(socket, outbound, cancellationToken);

            await foreach (string payload in outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (socket.State != WebSocketState.Open) break;
                await SendFrameAsync(socket, payload, cancellationToken).ConfigureAwait(false);
            }
            await drain.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            _clients.TryRemove(id, out _);
            outbound.Writer.TryComplete();
        }
    }

    private static async Task DrainThenCompleteAsync(WebSocket socket, Channel<string> outbound, CancellationToken cancellationToken)
    {
        try { await DrainIncomingAsync(socket, cancellationToken).ConfigureAwait(false); }
        finally { outbound.Writer.TryComplete(); }
    }

    /// <summary>The dock only sends pings; reading them is what detects a closed socket.</summary>
    private static async Task DrainIncomingAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[512];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private static Task SendFrameAsync(WebSocket socket, string payload, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, cancellationToken);
}
