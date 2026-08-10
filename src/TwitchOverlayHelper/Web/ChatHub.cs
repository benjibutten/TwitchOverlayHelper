using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Nicknames;
using TwitchOverlayHelper.Pets;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Web;

/// <summary>
/// Fan-out point between the single Twitch connection and every dock that is open. Holds the
/// recent history so a dock that reconnects (OBS restart, page reload) is not staring at an
/// empty column.
/// </summary>
public sealed class ChatHub(
    AppSettings settings,
    TwitchBadgeCatalog badges,
    TwitchSession session,
    PetRegistry pets,
    PetCatalog petCatalog,
    NicknameBook nicknames)
{
    private const int HistoryLimit = 200;

    private readonly ConcurrentDictionary<Guid, Channel<string>> _clients = new();
    // Messages and events share one queue, so a sub notice keeps its place between the lines it
    // landed between even after the dock reconnects and replays the history.
    private readonly Queue<ChatTimelineItem> _history = new();
    private readonly Lock _historyLock = new();
    // Written from the EventSub reader and read by every dock that connects, so it needs a lock of
    // its own rather than riding along on the history's.
    private readonly Lock _hypeTrainLock = new();

    private long _historyVersion;
    private HypeTrainState? _hypeTrain;
    private string _statusText = "Inte ansluten";
    private string _statusState = "idle";
    // Starts on the channel we were in last time rather than on nothing. The saved history is put
    // back before anything connects, and an empty channel here would make the first connect look
    // like a channel switch – which throws that history away the moment the stream starts.
    private string _channel = settings.Channel;
    private bool _showingSamples;

    public int ClientCount => _clients.Count;

    /// <summary>
    /// Lines were dropped from the timeline by a reader rather than by chat moving on – today only
    /// the dock's "hide the earlier sitting" button. The window listens, because the overlay draws
    /// from its own cards and the disk copy from its own file: without this, lines put away in the
    /// dock would still be on the stream, and back in both after the next restart.
    /// </summary>
    public event Action? HistoryTrimmed;

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
        if (!changed) return;

        // A train belongs to the channel it ran in, and it goes even while the sample lines are
        // still up: a train needs nobody to have said anything, so it can be running in a room
        // where the samples have never been replaced.
        ClearHypeTrain();

        lock (_historyLock)
        {
            // Samples are not anyone's chat, so they stay until the first real message replaces them.
            if (_showingSamples) return;
            _history.Clear();
            _historyVersion++;
            Send(DockJson.Serialize(new DockEnvelope<object?>("clear", null)));
        }
    }

    /// <summary>
    /// Says there is no train any more. Its own frame rather than a ride on the clear frame: clear
    /// also fires when the first real line replaces the sample lines, and a train that started
    /// before anyone had said a word would be wiped off the strip by a stranger's first "hej".
    /// </summary>
    public void ClearHypeTrain()
    {
        lock (_hypeTrainLock)
        {
            if (_hypeTrain is null) return;
            _hypeTrain = null;
        }
        Send(DockJson.Serialize(new DockEnvelope<DockHypeTrain?>("hypeTrain", null)));
    }

    public void PublishMessage(ChatMessage message)
    {
        // The queue and the frame under one lock, here and everywhere below: a dock's stream has to
        // arrive in the order the history was built. Publishing outside the lock lets a line land in
        // the middle of another writer's redraw, where it is either drawn in the wrong place or
        // wiped by a clear that was decided before it existed.
        lock (_historyLock)
        {
            Remember(ChatTimelineItem.Of(message));
            Send(DockJson.Serialize(new DockEnvelope<DockMessage>("message", ToDock(message))));
        }
    }

    public void PublishEvent(ChatEvent chatEvent)
    {
        lock (_historyLock)
        {
            Remember(ChatTimelineItem.Of(chatEvent));
            Send(DockJson.Serialize(new DockEnvelope<DockEvent>("event", DockMapper.ToDock(chatEvent))));
        }
    }

    /// <summary>
    /// Where the hype train stands now. Deliberately not part of the history: a train is one thing
    /// that changes for minutes, not a run of lines, and remembering every step would push the chat
    /// out of the column it shares. Each frame is the whole picture and replaces the last one.
    /// </summary>
    public void PublishHypeTrain(HypeTrainState train)
    {
        lock (_hypeTrainLock)
        {
            // Twitch promises nothing about the order of these, so an update that would walk the
            // train backwards is dropped rather than shown.
            if (!train.Supersedes(_hypeTrain)) return;
            _hypeTrain = train;
        }
        Send(DockJson.Serialize(new DockEnvelope<DockHypeTrain>("hypeTrain", DockMapper.ToDock(train))));
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
                // The rewritten line has to survive a restart too. Without this the marker is only
                // ever written to disk if some later message happens to bump the version for us,
                // and in a quiet chat that never comes.
                _historyVersion++;
            }
            Send(DockJson.Serialize(new DockEnvelope<DockMessage>("messageUpdate", ToDock(message))));
        }
    }

    /// <summary>Adds one item to the timeline. Callers hold <see cref="_historyLock"/>.</summary>
    private void Remember(ChatTimelineItem item)
    {
        // The first real line replaces the sample lines that showed what the dock looks like.
        if (_showingSamples)
        {
            _showingSamples = false;
            _history.Clear();
            Send(DockJson.Serialize(new DockEnvelope<object?>("clear", null)));
        }

        _history.Enqueue(item);
        while (_history.Count > HistoryLimit) _history.Dequeue();
        _historyVersion++;
    }

    /// <summary>
    /// Counts how many times the history has changed. Read by the code that writes it to disk, so a
    /// quiet chat costs nothing: no new lines, no new version, no file written.
    /// </summary>
    public long HistoryVersion { get { lock (_historyLock) return _historyVersion; } }

    /// <summary>
    /// The history as it stands, for saving. Sample lines are never handed out – they are a preview
    /// of the settings, and restoring them tomorrow as though they were chat would be a small lie
    /// told every morning.
    /// </summary>
    public IReadOnlyList<ChatTimelineItem> SnapshotHistory()
    {
        lock (_historyLock) return _showingSamples ? [] : _history.ToArray();
    }

    /// <summary>
    /// Replaces the whole timeline – a saved history at startup, or that history rebuilt once the
    /// fetched lines from before we connected have been woven into it.
    ///
    /// <para>Open docks are redrawn, which is what makes the second case work: by then a dock may
    /// already be showing live lines, and the fetched ones belong above them rather than appended
    /// underneath. At startup there is no dock connected yet, so the same call sends nothing.</para>
    /// </summary>
    public void ReplaceHistory(IReadOnlyList<ChatTimelineItem> items)
    {
        if (items.Count == 0) return;
        lock (_historyLock)
        {
            _history.Clear();
            foreach (ChatTimelineItem item in items) _history.Enqueue(item);
            while (_history.Count > HistoryLimit) _history.Dequeue();
            _historyVersion++;
            // Restored lines are real chat, so the samples must not come back over them – and the next
            // real message must not wipe them the way it wipes a preview.
            _showingSamples = false;
            Redraw();
        }
    }

    /// <summary>
    /// Drops every line older than <paramref name="cutoff"/>. What the dock's earlier-sitting button
    /// calls: the chat now survives a restart, so a dock can open onto this morning's lines sitting
    /// above tonight's first "hej", and putting them away has to mean away – not hidden in one
    /// browser until the next reload brings them back.
    /// </summary>
    /// <returns>
    /// How many lines went, so the dock can say it out loud. Usually more than the reader could see:
    /// the dock keeps fewer lines on screen than the timeline holds.
    /// </returns>
    public int TrimHistoryBefore(DateTimeOffset cutoff)
    {
        int removed;
        lock (_historyLock)
        {
            // The sample lines are a preview of the reading settings rather than anyone's chat, and
            // they all carry the moment they were made – there is no earlier sitting among them.
            if (_showingSamples) return 0;

            ChatTimelineItem[] kept = _history.Where(item => item.At >= cutoff).ToArray();
            removed = _history.Count - kept.Length;
            if (removed == 0) return 0;

            _history.Clear();
            foreach (ChatTimelineItem item in kept) _history.Enqueue(item);
            // Put away has to stay put away across a restart, so this counts as a change worth writing.
            _historyVersion++;
            Redraw();
        }
        // Outside the lock: the window answers this by reading the history back for the overlay and
        // for the file, and nothing about that belongs inside a lock the fan-out holds.
        HistoryTrimmed?.Invoke();
        return removed;
    }

    /// <summary>
    /// Hands every open dock the timeline as it now stands. Callers hold <see cref="_historyLock"/>,
    /// so what goes out is exactly what a reconnect would replay, and a live line arriving mid-redraw
    /// waits its turn instead of being wiped by a redraw that was decided before it existed.
    ///
    /// <para>One frame rather than a clear followed by the lines again: the dock paces incoming
    /// messages at the reading speed the user chose, and two hundred lines sent as messages would
    /// trickle back onto the page for a minute. Deliberately not a clear either – the channel has not
    /// changed, so the pinned strip has no reason to lose what it is holding.</para>
    /// </summary>
    private void Redraw() =>
        Send(DockJson.Serialize(new DockEnvelope<IReadOnlyList<DockHistoryItem>>(
            "history", _history.Select(ToDock).ToArray())));

    public void PublishModeration(ChatModerationEvent moderation)
    {
        lock (_historyLock)
        {
            // Drop the affected messages from history so a reconnecting dock never resurrects them.
            ChatTimelineItem[] kept = _history.Where(item => !IsAffected(item, moderation)).ToArray();
            _history.Clear();
            foreach (ChatTimelineItem item in kept) _history.Enqueue(item);
            // A deleted line has to stay deleted across a restart too, so this counts as a change.
            _historyVersion++;
            Send(DockJson.Serialize(new DockEnvelope<DockModerationPayload>("moderation", DockMapper.ToDock(moderation))));
        }
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

    /// <summary>
    /// A nickname was given, changed or taken away. Every dock gets it, including the one that made
    /// the change: the name has to land on the lines already on screen, and rendering it from a
    /// lookup rather than from the message means one frame is enough to update the whole column.
    /// </summary>
    public void PublishNickname(Nickname entry) =>
        Send(DockJson.Serialize(new DockEnvelope<DockNickname>("nickname",
            new DockNickname(entry.UserId, entry.Login, entry.IsRemoval ? null : entry.Text))));

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
            BroadcasterId,
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

        lock (_historyLock)
        {
            _showingSamples = true;
            _history.Clear();
            foreach (ChatMessage sample in samples) _history.Enqueue(ChatTimelineItem.Of(sample));
            _history.Enqueue(ChatTimelineItem.Of(sampleEvent));

            foreach (ChatMessage sample in samples)
                Send(DockJson.Serialize(new DockEnvelope<DockMessage>("message", ToDock(sample))));
            Send(DockJson.Serialize(new DockEnvelope<DockEvent>("event", DockMapper.ToDock(sampleEvent))));
        }
    }

    private static ChatMessage Sample(string id, string name, string text, string color, ChatBadge[] badges, DateTimeOffset at) =>
        new(id, name, text, color, badges, id == "sample-3", false, at) { UserLogin = name.ToLowerInvariant() };

    private string BuildHello(bool canSend)
    {
        ChatTimelineItem[] history;
        lock (_historyLock) history = _history.ToArray();
        HypeTrainState? hypeTrain;
        lock (_hypeTrainLock) hypeTrain = _hypeTrain;

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
            pets.Snapshot().Select(ToDock).ToArray(),
            // A train that has already run out is not news to a dock opening now, and replaying it
            // would put a strip on screen for something that finished before the page existed.
            hypeTrain is { } train && train.IsWorthShowing(DateTimeOffset.Now) ? DockMapper.ToDock(train) : null,
            // The whole book, not the names in the history: a dock that scrolls back to a line from
            // an hour ago should still see the nickname on it.
            nicknames.Snapshot().Select(entry => new DockNickname(entry.UserId, entry.Login, entry.Text)).ToArray()));
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
