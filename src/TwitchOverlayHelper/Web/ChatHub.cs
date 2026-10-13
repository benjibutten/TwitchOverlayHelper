using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TwitchOverlayHelper.Fight;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Nicknames;
using TwitchOverlayHelper.Pets;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Speech;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Web;

/// <summary>
/// Which page a socket belongs to. The chat frames are the same for all of them, but a few carry
/// something only the streamer should have – the nicknames above all – and those are addressed
/// rather than broadcast. A socket that does not name its view is a dock.
/// </summary>
public enum DockView
{
    Dock,
    /// <summary>The transparent chat laid over the stream, seen by the viewers.</summary>
    Stream,
    /// <summary>
    /// The pet lawn. A view of its own so the app can tell whether a pet will be seen at all,
    /// which a dock left open in a browser tab cannot answer. A pet overlay that connects as a
    /// dock works but is not counted.
    /// </summary>
    Pets,

    /// <summary>
    /// The paid readings, as sound and nothing else. A page of its own because OBS mixes each
    /// browser source's audio separately, which gives the readings their own volume and track.
    /// </summary>
    Tts,

    /// <summary>
    /// The wait screen with the fight. Gets its own setup and the viewers' cheers, never the chat.
    /// </summary>
    Fight
}

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

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    // Messages and events share one queue, so a sub notice keeps its place between the lines it
    // landed between when a reconnecting dock replays the history.
    private readonly Queue<ChatTimelineItem> _history = new();
    private readonly Lock _historyLock = new();
    // Written from the EventSub reader and read by every dock that connects.
    private readonly Lock _hypeTrainLock = new();

    private long _historyVersion;
    private HypeTrainState? _hypeTrain;
    private string _statusText = "Inte ansluten";
    private string _statusState = "idle";
    // Starts on the saved channel: the saved history is restored before anything connects, and an
    // empty channel here would make the first connect look like a channel switch and discard it.
    private string _channel = settings.Channel;
    private bool _showingSamples;

    public int ClientCount => _clients.Count;

    /// <summary>
    /// How many pet overlays are connected right now. Zero means a pet spawned now would be drawn
    /// for nobody, so its redemption has to be paid back.
    /// </summary>
    public int PetOverlayCount => CountOf(DockView.Pets);

    /// <summary>
    /// Raised when a reader, rather than chat moving on, dropped lines from the timeline. The
    /// overlay and the saved history keep their own copies and have to drop the lines too.
    /// </summary>
    public event Action? HistoryTrimmed;

    /// <summary>
    /// A pet overlay reporting that it has put a pet on screen, by pet id. The only signal that a
    /// redemption was delivered rather than merely accepted.
    /// </summary>
    public event Action<string>? PetShown;

    /// <summary>Raised with the new count whenever a pet overlay connects or goes away.</summary>
    public event Action<int>? PetOverlayCountChanged;

    /// <summary>
    /// A pet overlay reporting that a lucky spin has played out, by spin id. The win is booked
    /// before the frame goes out, so this only frees the stage for the next spin.
    /// </summary>
    public event Action<string>? SpinShown;

    /// <summary>
    /// The reading page reporting that it has finished – or failed to start – one clip, by the id
    /// it was sent. The only signal that a reading reached the mix.
    /// </summary>
    public event Action<string, bool>? TtsPlaybackFinished;

    /// <summary>
    /// Raised with the new count whenever a reading page connects or goes away. A clip in flight
    /// when the last page leaves is never reported through <see cref="TtsPlaybackFinished"/>.
    /// </summary>
    public event Action<int>? TtsOverlayCountChanged;

    /// <summary>
    /// The wait screen saying who is in the ring – player 1's fighter id, then player 2's. Used to
    /// find the corner a cheer by name is meant for.
    /// </summary>
    public event Action<string, string>? FightLineupReported;

    /// <summary>
    /// How many reading pages are connected. Zero means a reading would be heard by nobody, so its
    /// redemption has to be refunded.
    /// </summary>
    public int TtsOverlayCount => CountOf(DockView.Tts);

    /// <summary>Twitch room id of the joined channel; needed as broadcaster_id for moderation.</summary>
    public string BroadcasterId { get; set; } = string.Empty;

    /// <summary>Whether name pronunciation is set up. The dock hides the speaker button when it is not.</summary>
    public bool SpeechEnabled { get; set; }

    /// <summary>
    /// What the dock's approval bar should be showing. A callback because the speech service owns
    /// the queue of readings.
    /// </summary>
    public Func<IReadOnlyList<TtsEntry>>? TtsPending { get; set; }

    /// <summary>
    /// Who is in the ring and where, for the wait screen. A callback because the fighters live in
    /// their own catalog, read off disk.
    /// </summary>
    internal Func<DockFightSetup>? FightSetup { get; set; }

    /// <summary>How many wait screens are open. Zero means a viewer's cheer would land nowhere.</summary>
    public int FightOverlayCount => CountOf(DockView.Fight);

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

        // Before the samples check below: a train can be running in a room where the samples have
        // never been replaced.
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

    /// <summary>Tells every page that no hype train is running.</summary>
    public void ClearHypeTrain()
    {
        lock (_hypeTrainLock)
        {
            if (_hypeTrain is null) return;
            _hypeTrain = null;
        }
        // Its own frame rather than part of "clear": clear also fires when the first real line
        // replaces the samples, which must not wipe a train that started before it.
        Send(DockJson.Serialize(new DockEnvelope<DockHypeTrain?>("hypeTrain", null)));
    }

    public void PublishMessage(ChatMessage message)
    {
        // The queue and the frame under one lock, here and everywhere below, so a dock receives
        // frames in the order the history was built and no line lands mid-redraw.
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
    /// Publishes where the hype train stands now. Not part of the history: each frame is the
    /// whole picture and replaces the last one. An update older than the current state is dropped.
    /// </summary>
    public void PublishHypeTrain(HypeTrainState train)
    {
        lock (_hypeTrainLock)
        {
            // Twitch promises nothing about the order of these.
            if (!train.Supersedes(_hypeTrain)) return;
            _hypeTrain = train;
        }
        Send(DockJson.Serialize(new DockEnvelope<DockHypeTrain>("hypeTrain", DockMapper.ToDock(train))));
    }

    /// <summary>
    /// Sends a line that is already on screen again, changed, and rewrites it in the history. Used
    /// when a Gigantify an Emote power-up arrives after the message it enlarged.
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
            // When the line has scrolled out of the history, docks that still show it get the
            // update below.
            if (found)
            {
                _history.Clear();
                foreach (ChatTimelineItem item in items) _history.Enqueue(item);
                // Bumped so the rewritten line is saved to disk even if no later message arrives.
                _historyVersion++;
            }
            Send(DockJson.Serialize(new DockEnvelope<DockMessage>("messageUpdate", ToDock(message))));
        }
    }

    /// <summary>
    /// Takes the sample lines down. Call on connect rather than waiting for the first line: in a
    /// quiet room the invented lines would otherwise stay on the broadcast, where viewers cannot
    /// tell them from real chat.
    /// </summary>
    public void ClearSamples()
    {
        lock (_historyLock)
        {
            if (_showingSamples) DropSamples();
        }
    }

    /// <summary>
    /// Removes the samples and tells every page the column is empty. Callers hold
    /// <see cref="_historyLock"/>.
    /// </summary>
    private void DropSamples()
    {
        // No version bump: the samples are never saved.
        _showingSamples = false;
        _history.Clear();
        Send(DockJson.Serialize(new DockEnvelope<object?>("clear", null)));
    }

    /// <summary>Adds one item to the timeline. Callers hold <see cref="_historyLock"/>.</summary>
    private void Remember(ChatTimelineItem item)
    {
        // A real line replaces the samples.
        if (_showingSamples) DropSamples();

        _history.Enqueue(item);
        while (_history.Count > HistoryLimit) _history.Dequeue();
        _historyVersion++;
    }

    /// <summary>
    /// Counts how many times the history has changed, so the code that saves it can skip writing
    /// when nothing has.
    /// </summary>
    public long HistoryVersion { get { lock (_historyLock) return _historyVersion; } }

    /// <summary>
    /// The history as it stands, for saving. Empty while the sample lines are showing, since they
    /// are a preview and not chat.
    /// </summary>
    public IReadOnlyList<ChatTimelineItem> SnapshotHistory()
    {
        lock (_historyLock) return _showingSamples ? [] : _history.ToArray();
    }

    /// <summary>
    /// Replaces the whole timeline – a saved history at startup, or that history merged with the
    /// lines fetched from before we connected – and redraws every open dock. Does nothing for an
    /// empty list.
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
            // Restored lines are real chat, so the next message must not wipe them like samples.
            _showingSamples = false;
            Redraw();
        }
    }

    /// <summary>
    /// Drops every line older than <paramref name="cutoff"/> from the timeline, redraws the open
    /// docks and raises <see cref="HistoryTrimmed"/>. Used by the dock's earlier-sitting button.
    /// </summary>
    /// <returns>
    /// How many lines went. Can be more than the reader could see: the dock keeps fewer lines on
    /// screen than the timeline holds.
    /// </returns>
    public int TrimHistoryBefore(DateTimeOffset cutoff)
    {
        int removed;
        lock (_historyLock)
        {
            // The sample lines all carry the moment they were made.
            if (_showingSamples) return 0;

            ChatTimelineItem[] kept = _history.Where(item => item.At >= cutoff).ToArray();
            removed = _history.Count - kept.Length;
            if (removed == 0) return 0;

            _history.Clear();
            foreach (ChatTimelineItem item in kept) _history.Enqueue(item);
            // Bumped so the trimmed lines stay gone across a restart.
            _historyVersion++;
            Redraw();
        }
        // Outside the lock: the handler reads the history back.
        HistoryTrimmed?.Invoke();
        return removed;
    }

    // One "history" frame rather than a clear followed by the lines: the dock paces incoming
    // messages at reading speed, so two hundred of them would trickle in for a minute, and a
    // clear would also empty the pinned strip although the channel has not changed.
    /// <summary>
    /// Hands every open dock the timeline as it now stands. Callers hold <see cref="_historyLock"/>.
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
            // Bumped so a deleted line stays deleted across a restart.
            _historyVersion++;
            Send(DockJson.Serialize(new DockEnvelope<DockModerationPayload>("moderation", DockMapper.ToDock(moderation))));
        }
    }

    /// <summary>
    /// Moderation reaches messages only. Uses <see cref="ChatModerationEvent.Affects"/>, the rule
    /// the overlay uses, so a ban removes the same lines on both surfaces.
    /// </summary>
    private static bool IsAffected(ChatTimelineItem item, ChatModerationEvent moderation) =>
        item.Message is { } message && moderation.Affects(message);

    /// <summary>
    /// Publishes whether we are connected, and what went wrong if not. Sent to docks only: the
    /// pages on the broadcast must not show which of our tokens expired.
    /// </summary>
    public void PublishStatus(string text, string state)
    {
        _statusText = text;
        _statusState = state;
        SendTo(DockView.Dock, DockJson.Serialize(new DockEnvelope<DockStatus>("status", new DockStatus(text, state))));
    }

    public void PublishSettings() =>
        SendTo(DockView.Dock, DockJson.Serialize(new DockEnvelope<DockSettings>("settings", settings.Dock)));

    /// <summary>Pushes the stream overlay's appearance, so a slider in the app lands without a reload.</summary>
    public void PublishStreamSettings() =>
        SendTo(DockView.Stream, DockJson.Serialize(new DockEnvelope<StreamSettings>("streamSettings", settings.Stream)));

    public void PublishAuth(bool canSend) =>
        SendTo(DockView.Dock, DockJson.Serialize(new DockEnvelope<DockAuth>("auth", BuildAuth(canSend))));

    public void PublishSpeech() =>
        SendTo(DockView.Dock, DockJson.Serialize(new DockEnvelope<DockSpeech>("speech", new DockSpeech(SpeechEnabled))));

    /// <summary>
    /// Publishes the paid readings waiting on the streamer, as the whole list each time. Sent to
    /// docks only: the text is unapproved, and the other pages are the ones the viewers see.
    /// </summary>
    public void PublishTts() =>
        SendTo(DockView.Dock, DockJson.Serialize(new DockEnvelope<IReadOnlyList<TtsEntry>>("tts", TtsSnapshot())));

    private IReadOnlyList<TtsEntry> TtsSnapshot() => TtsPending?.Invoke() ?? [];

    /// <summary>
    /// Hands one clip to the reading pages, and to them alone.
    /// </summary>
    /// <returns>
    /// How many pages were told. Zero means the browser source is missing from the scene, and the
    /// caller has to treat the reading as undelivered: no acknowledgement will come.
    /// </returns>
    public int PublishTtsPlay(string playbackId, string url, TtsClip clip)
    {
        // The viewer's name and text are only sent when the settings ask for a card. Decided here
        // rather than in the page, so a browser source on the broadcast never receives them.
        bool card = settings.Tts.ShowsWidget;
        var play = card
            ? new DockTtsPlay(
                playbackId,
                url,
                clip.Volume,
                clip.Viewer,
                clip.Text,
                clip.Cost > 0 ? clip.Cost : null,
                clip.Source == TtsSource.PowerUp ? "powerUp" : "reward")
            : new DockTtsPlay(playbackId, url, clip.Volume);

        Fan(DockJson.Serialize(new DockEnvelope<DockTtsPlay>("ttsPlay", play)), client => client.View == DockView.Tts);
        return TtsOverlayCount;
    }

    /// <summary>
    /// Pushes the card's appearance to the reading pages, so a slider in the app lands without
    /// anyone reloading OBS.
    /// </summary>
    public void PublishTtsWidget() =>
        SendTo(DockView.Tts, DockJson.Serialize(new DockEnvelope<DockTtsWidget>("ttsWidget", DockTtsWidget.From(settings.Tts))));

    /// <summary>
    /// Draws the card for <paramref name="milliseconds"/> with nothing playing, for the preview
    /// button in the settings. Costs no ElevenLabs characters.
    /// </summary>
    /// <returns>How many reading pages were told to draw it.</returns>
    public int PublishTtsPreview(string viewer, string text, int cost, TtsSource source, int milliseconds)
    {
        var preview = new DockTtsPreview(
            viewer, text, cost, source == TtsSource.PowerUp ? "powerUp" : "reward", milliseconds);
        SendTo(DockView.Tts, DockJson.Serialize(new DockEnvelope<DockTtsPreview>("ttsPreview", preview)));
        return TtsOverlayCount;
    }

    /// <summary>Stops whatever the reading pages are playing – the dock's stop button, or a shutdown.</summary>
    public void PublishTtsStop() =>
        SendTo(DockView.Tts, DockJson.Serialize(new DockEnvelope<object?>("ttsStop", null)));

    /// <summary>
    /// A nickname was given, changed or taken away. Every dock gets it, including the one that
    /// made the change, and applies it to the lines already on screen.
    /// </summary>
    public void PublishNickname(Nickname entry) =>
        SendTo(DockView.Dock, DockJson.Serialize(new DockEnvelope<DockNickname>("nickname",
            new DockNickname(entry.UserId, entry.Login, entry.IsRemoval ? null : entry.Text))));

    /// <summary>Tells every dock that badge images just became available, so it can re-render.</summary>
    public void PublishBadgesLoaded() =>
        Send(DockJson.Serialize(new DockEnvelope<object?>("badgesLoaded", null)));

    public void PublishPetSpawn(PetSpawnResult result) =>
        SendEverywhere(DockJson.Serialize(new DockEnvelope<DockPetSpawn>("petSpawn",
            new DockPetSpawn(ToDock(result.Pet), result.RemovedId, result.Extended))));

    /// <summary>Acts out one lucky spin on the pet overlay. The winner is already decided and booked.</summary>
    internal void PublishSpin(DockSpin spin) =>
        SendEverywhere(DockJson.Serialize(new DockEnvelope<DockSpin>("spin", spin)));

    /// <summary>
    /// Sends one pet home ahead of time, because the redemption behind it was paid back.
    /// </summary>
    public void PublishPetRemoved(string petId) =>
        SendEverywhere(DockJson.Serialize(new DockEnvelope<DockPetRemoved>("petRemove", new DockPetRemoved(petId))));

    /// <summary>
    /// Empties the lawn on every open overlay, because the app has left the channel these pets
    /// were bought in.
    /// </summary>
    public void PublishPetsCleared() =>
        SendEverywhere(DockJson.Serialize(new DockEnvelope<object?>("petsClear", null)));

    /// <summary>Pushes size and limits to the pet overlay so slider changes land without a reload.</summary>
    public void PublishPetSettings() =>
        SendEverywhere(DockJson.Serialize(new DockEnvelope<DockPetSettings>("petSettings", BuildPetSettings())));

    /// <summary>Hands the overlay the species list again, after the user reloads the pets folder.</summary>
    public void PublishPetCatalog() =>
        SendEverywhere(DockJson.Serialize(new DockEnvelope<IReadOnlyList<DockPetDefinition>>("petCatalog", BuildPetCatalog())));

    /// <summary>A new fighter, arena or command word, so an open wait screen changes without a reload in OBS.</summary>
    public void PublishFightSetup()
    {
        if (FightSetup is null) return;
        SendTo(DockView.Fight, DockJson.Serialize(new DockEnvelope<DockFightSetup>("fightSetup", FightSetup())));
    }

    /// <summary>
    /// A vote in the character select. The voter travels as a hashed key the page can count by:
    /// the wait screen is on the broadcast and must not hold a Twitch id.
    /// </summary>
    public void PublishFightPick(FightPick pick)
    {
        string voter = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(pick.UserKey ?? pick.Viewer)))[..16];
        SendTo(DockView.Fight, DockJson.Serialize(new DockEnvelope<DockFightPick>("fightPick",
            new DockFightPick(pick.Player, pick.Fighter, pick.Outfit, pick.Viewer, voter, pick.Color))));
    }

    /// <summary>A viewer took a side. Only the wait screen is told – it is the only page that fights.</summary>
    public void PublishFightAssist(FightAssist assist) =>
        SendTo(DockView.Fight, DockJson.Serialize(new DockEnvelope<DockFightAssist>("fightAssist", new DockFightAssist(
            assist.Player, assist.Kind == FightAssistKind.Heal ? "heal" : "cheer", assist.Viewer, assist.Color))));

    /// <summary>
    /// The pet settings as the overlay gets them. Also what the pet inspector reads over HTTP, so
    /// it plays at the lawn's animation speed.
    /// </summary>
    internal DockPetSettings BuildPetSettings() => new(
        settings.Pets.Enabled, settings.Pets.Scale, settings.Pets.LifetimeMinutes, settings.Pets.MaxPets,
        settings.Pets.ShowNames, settings.Pets.RarityEffects, settings.Pets.RarityFadeSeconds,
        settings.Pets.AnimationSpeed);

    /// <summary>The species list as the overlay gets it. Also what the pet inspector reads over HTTP.</summary>
    internal IReadOnlyList<DockPetDefinition> BuildPetCatalog() => petCatalog.Pets
        .Select(pet => pet.SpriteFile is { Length: > 0 }
            ? new DockPetDefinition(pet.Id, pet.Name, pet.Description, "sprite", null, $"/pets/sprite/{pet.Id}", pet.Fps, pet.Emoji, pet.Rarity, pet.SpriteVersion)
            : new DockPetDefinition(pet.Id, pet.Name, pet.Description, "svg", $"/pets/body/{pet.Id}", null, pet.Fps, pet.Emoji, pet.Rarity))
        .ToArray();

    private static DockPet ToDock(PetState pet) => new(pet.Id, pet.Name, pet.Color, pet.Species, pet.SpawnedAt, pet.ExpiresAt);

    internal DockAuth BuildAuth(bool canSend)
    {
        SessionState state = session.Snapshot();
        return new DockAuth(
            state.IsLoggedIn,
            state.Login,
            // An IRC socket opened while logged in stays authenticated until it is torn down, so
            // the login is checked too and a logout takes the composer away at once.
            canSend && state.IsLoggedIn,
            // Twitch only lets you raid out of your own channel.
            state.IsLoggedIn && BroadcasterId.Length > 0 && BroadcasterId == state.UserId,
            BroadcasterId,
            state.Error);
    }

    /// <summary>
    /// Shows sample lines, so an unconnected dock shows what the reading settings look like rather
    /// than an empty column.
    ///
    /// <para>Sent as one "samples" frame rather than as messages: the stream overlay drops
    /// replayed lines older than the fade time, and samples must stay up while the browser source
    /// is being placed in OBS.</para>
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

            Send(DockJson.Serialize(new DockSamples("samples", _history.Select(ToDock).ToArray())));
        }
    }

    private static ChatMessage Sample(string id, string name, string text, string color, ChatBadge[] badges, DateTimeOffset at) =>
        new(id, name, text, color, badges, id == "sample-3", false, at) { UserLogin = name.ToLowerInvariant() };

    /// <summary>
    /// What the stream overlay opens onto: its own appearance and the tail of the timeline.
    /// </summary>
    private string BuildStreamHello()
    {
        // Deeper than the lines the page draws, because it filters out bots, commands,
        // switched-off events and lines too old to replay.
        int depth = Math.Clamp(settings.Stream.MaxMessages * 4, 40, HistoryLimit);
        ChatTimelineItem[] history;
        bool samples;
        lock (_historyLock)
        {
            history = _history.TakeLast(depth).ToArray();
            samples = _showingSamples;
        }
        // The samples flag matters to the page: replayed chat is dropped once it is too old, while
        // samples have no age.
        return DockJson.Serialize(new DockStreamHello("hello", settings.Stream, history.Select(ToDock).ToArray(), samples));
    }

    /// <summary>
    /// The pet overlay's own greeting: its settings, the drawings and the pets already alive.
    /// Unlike the dock's it carries no nicknames, login or chat history, which a browser source
    /// on the broadcast machine should not hold.
    /// </summary>
    private string BuildPetsHello() =>
        DockJson.Serialize(new DockPetsHello("hello", BuildPetSettings(), BuildPetCatalog(), pets.Snapshot().Select(ToDock).ToArray()));

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
            // A train that has already run out is not replayed to a dock opening now.
            hypeTrain is { } train && train.IsWorthShowing(DateTimeOffset.Now) ? DockMapper.ToDock(train) : null,
            // The whole book, not only the names in the history: the dock may still show older lines.
            nicknames.Snapshot().Select(entry => new DockNickname(entry.UserId, entry.Login, entry.Text)).ToArray(),
            // A reading waiting for an answer outlives an OBS restart, so the approval bar comes
            // back with the hello.
            TtsSnapshot()));
    }

    private DockMessage ToDock(ChatMessage message) => DockMapper.ToDock(message, badge =>
        badges.TryGet(badge.SetId, badge.Version, out BadgeInfo? info) ? (info!.ImageUrl, info.Title) : (null, null));

    /// <summary>History travels as tagged items so the dock can replay it through the same code
    /// that handles live frames, and in the order the lines actually arrived.</summary>
    private DockHistoryItem ToDock(ChatTimelineItem item) => item.Event is { } chatEvent
        ? new DockHistoryItem("event", null, DockMapper.ToDock(chatEvent))
        : new DockHistoryItem("message", ToDock(item.Message!), null);

    /// <summary>One open socket and which page it belongs to.</summary>
    private sealed record Client(Channel<string> Outbound, DockView View);

    /// <summary>
    /// Sends the chat and everything that happens in it to the pages that read chat, which
    /// excludes the pet lawn and the wait screen.
    ///
    /// <para>The lawn must be left out: a client that fills its bounded queue is dropped, and a
    /// raid's worth of chat frames the lawn never reads could close its socket. No lawn connected
    /// refunds the pets on it.</para>
    /// </summary>
    private void Send(string payload) => Fan(payload, client => client.View is not (DockView.Pets or DockView.Fight));

    /// <summary>
    /// Sends to one kind of page only. Private data – the nicknames, who is logged in, the
    /// reading settings – is addressed to the dock rather than filtered in the browser, so it
    /// never reaches a page sitting on the broadcast.
    /// </summary>
    private void SendTo(DockView view, string payload) => Fan(payload, client => client.View == view);

    /// <summary>
    /// Sends the pet frames to every page. Not narrowed to <see cref="DockView.Pets"/>, because a
    /// pet overlay whose URL does not name that view connects as a dock.
    /// </summary>
    private void SendEverywhere(string payload) => Fan(payload, _ => true);

    private void Fan(string payload, Func<Client, bool> wants)
    {
        // Enumerates the dictionary rather than its Values, which builds a fresh list on every
        // read; this runs on every chat line.
        foreach (KeyValuePair<Guid, Client> entry in _clients)
        {
            Client client = entry.Value;
            if (!wants(client)) continue;
            // A dock that cannot keep up is dropped rather than allowed to stall the whole fan-out.
            if (!client.Outbound.Writer.TryWrite(payload)) client.Outbound.Writer.TryComplete();
        }
    }

    /// <summary>How many connected pages are of one kind.</summary>
    private int CountOf(DockView view)
    {
        int count = 0;
        // Enumerates the dictionary rather than its Values, like Fan.
        foreach (KeyValuePair<Guid, Client> entry in _clients)
            if (entry.Value.View == view) count++;
        return count;
    }

    /// <summary>Runs one dock connection until the socket closes.</summary>
    public async Task RunClientAsync(WebSocket socket, bool canSend, CancellationToken cancellationToken, DockView view = DockView.Dock)
    {
        var id = Guid.NewGuid();
        // Never DropOldest: the queue also carries clear and moderation frames, and dropping one
        // leaves a deleted message on screen. A dock that falls this far behind is disconnected
        // instead, and its reconnect gets a correct history.
        var outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
        _clients[id] = new Client(outbound, view);
        if (view == DockView.Pets) PetOverlayCountChanged?.Invoke(PetOverlayCount);
        else if (view == DockView.Tts) TtsOverlayCountChanged?.Invoke(TtsOverlayCount);

        try
        {
            string hello = view switch
            {
                DockView.Stream => BuildStreamHello(),
                DockView.Pets => BuildPetsHello(),
                // The reading page gets its own appearance and nothing else: it has no state to
                // restore, and it is a browser source on the broadcast machine.
                DockView.Tts => DockJson.Serialize(new DockTtsHello("hello", DockTtsWidget.From(settings.Tts))),
                // The wait screen is told who fights where, never the chat. It ignores the pet
                // frames every page gets.
                DockView.Fight => FightSetup is null
                    ? DockJson.Serialize(new DockEnvelope<object?>("hello", null))
                    : DockJson.Serialize(new DockEnvelope<DockFightSetup>("hello", FightSetup())),
                _ => BuildHello(canSend)
            };
            await SendFrameAsync(socket, hello, cancellationToken).ConfigureAwait(false);
            // Completes the outbound channel when the socket closes, which ends the loop below even
            // for an idle dock.
            Task drain = DrainThenCompleteAsync(socket, outbound, view, cancellationToken);

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
            if (view == DockView.Pets) PetOverlayCountChanged?.Invoke(PetOverlayCount);
            else if (view == DockView.Tts) TtsOverlayCountChanged?.Invoke(TtsOverlayCount);
        }
    }

    private async Task DrainThenCompleteAsync(WebSocket socket, Channel<string> outbound, DockView view, CancellationToken cancellationToken)
    {
        try { await DrainIncomingAsync(socket, view, cancellationToken).ConfigureAwait(false); }
        finally { outbound.Writer.TryComplete(); }
    }

    /// <summary>
    /// Reads what a page sends back until the socket closes. For a dock that is pings only, and
    /// reading them is what detects a closed socket. The pet overlay, the reading page and the
    /// wait screen also send reports, which go to <see cref="ReadReport"/>.
    /// </summary>
    private async Task DrainIncomingAsync(WebSocket socket, DockView view, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[512];
        var message = new StringBuilder();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (view is not (DockView.Pets or DockView.Tts or DockView.Fight) || result.MessageType != WebSocketMessageType.Text) continue;

                // Capped so a page cannot grow this buffer without limit; no valid report is this long.
                if (message.Length < 2048) message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                string payload = message.ToString();
                message.Clear();
                ReadReport(payload, view);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    /// <summary>
    /// Handles one report frame from a page. Each view is only listened to for its own kind of
    /// report, and an unknown or malformed frame is ignored: these sockets sit on the broadcast
    /// machine, and nothing a browser sends may end a connection.
    /// </summary>
    private void ReadReport(string payload, DockView view)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement frame = document.RootElement;
            // Every value's kind is checked before it is read: GetString on a number throws
            // InvalidOperationException, not JsonException.
            if (frame.ValueKind != JsonValueKind.Object) return;
            if (!frame.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String) return;
            if (Text(frame, "id") is not { Length: > 0 } id) return;

            switch (type.GetString())
            {
                case "petShown" when view == DockView.Pets:
                    PetShown?.Invoke(id);
                    return;
                case "spinDone" when view == DockView.Pets:
                    SpinShown?.Invoke(id);
                    return;
                case "ttsPlayed" when view == DockView.Tts:
                    TtsPlaybackFinished?.Invoke(id, true);
                    return;
                case "ttsFailed" when view == DockView.Tts:
                    TtsPlaybackFinished?.Invoke(id, false);
                    return;
                case "fightLineup" when view == DockView.Fight:
                    if (Text(frame, "p2") is { Length: > 0 and <= 64 } p2 && id.Length <= 64) FightLineupReported?.Invoke(id, p2);
                    return;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
        }
    }

    private static string? Text(JsonElement frame, string property) =>
        frame.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Task SendFrameAsync(WebSocket socket, string payload, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, cancellationToken);
}
