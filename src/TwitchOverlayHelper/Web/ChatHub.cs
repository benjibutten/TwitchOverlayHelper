using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
/// rather than broadcast. A socket that says nothing is a dock, which is what every browser source
/// added before this existed does.
/// </summary>
public enum DockView
{
    Dock,
    /// <summary>The transparent chat laid over the stream, seen by the viewers.</summary>
    Stream,
    /// <summary>
    /// The pet lawn. Told apart from a dock because it is the one view that answers the question
    /// "is anybody actually going to see this pet" – and a dock left open in a browser tab is not
    /// an answer to that. A pet overlay from before this view existed still connects as a dock and
    /// still works; it simply cannot vouch for itself, and the rewards that pay back notice.
    /// </summary>
    Pets,

    /// <summary>
    /// The paid readings, as sound and nothing else. A page of its own rather than a job for the pet
    /// lawn: OBS mixes each browser source's audio separately, so this is what gives the readings
    /// their own volume, their own track and their own monitoring – and a streamer who wants no pets
    /// should not have to add a lawn in order to be heard.
    /// </summary>
    Tts
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
    /// How many pet overlays are connected right now. Zero means a pet spawned this second would be
    /// drawn for nobody – the browser source is missing from the scene, or OBS has shut it down –
    /// which is the difference between a redemption that was delivered and one that has to be paid
    /// back.
    /// </summary>
    public int PetOverlayCount => CountOf(DockView.Pets);

    /// <summary>
    /// Lines were dropped from the timeline by a reader rather than by chat moving on – today only
    /// the dock's "hide the earlier sitting" button. The window listens, because the overlay draws
    /// from its own cards and the disk copy from its own file: without this, lines put away in the
    /// dock would still be on the stream, and back in both after the next restart.
    /// </summary>
    public event Action? HistoryTrimmed;

    /// <summary>
    /// A pet overlay reporting that it has put a pet on screen, by pet id. The only signal in the
    /// app that a redemption was actually delivered rather than merely accepted: everything before
    /// this proves the server thought so.
    /// </summary>
    public event Action<string>? PetShown;

    /// <summary>Raised with the new count whenever a pet overlay connects or goes away.</summary>
    public event Action<int>? PetOverlayCountChanged;

    /// <summary>
    /// The reading page reporting that it has finished – or failed to start – one clip, by the id it
    /// was sent. The only signal that a reading actually reached the mix: everything before it
    /// proves the server sent a frame.
    /// </summary>
    public event Action<string, bool>? TtsPlaybackFinished;

    /// <summary>
    /// Raised with the new count whenever a reading page connects or goes away. What it exists for is
    /// the going away: a clip in flight when the last page left will never be reported on, because the
    /// report would go over the socket that has just closed.
    /// </summary>
    public event Action<int>? TtsOverlayCountChanged;

    /// <summary>
    /// How many reading pages are connected. Zero means a reading would be synthesised, paid for and
    /// heard by nobody – which is the difference between a redemption delivered and one to refund.
    /// </summary>
    public int TtsOverlayCount => CountOf(DockView.Tts);

    /// <summary>Twitch room id of the joined channel; needed as broadcaster_id for moderation.</summary>
    public string BroadcasterId { get; set; } = string.Empty;

    /// <summary>
    /// Whether name pronunciation is set up. The dock hides the speaker button when it is not, so
    /// nobody meets a button that only ever answers "fyll i API-nycklar".
    /// </summary>
    public bool SpeechEnabled { get; set; }

    /// <summary>
    /// What the dock's approval bar should be showing. A callback rather than a list held here: the
    /// readings live in the speech service, and two copies of that queue would be two answers to
    /// "what is waiting" – the wrong one being the one on screen while the streamer decides.
    /// </summary>
    public Func<IReadOnlyList<TtsEntry>>? TtsPending { get; set; }

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

    /// <summary>
    /// Takes the preview down, because the app has joined a channel and everything from here is real
    /// chat. Called on connect rather than left to the first line that arrives: a room that stays
    /// quiet for the first ten minutes of a stream would otherwise have six invented lines and a made
    /// up subscription sitting on the broadcast the whole time, and the viewers cannot tell them from
    /// the real thing.
    /// </summary>
    public void ClearSamples()
    {
        lock (_historyLock)
        {
            if (_showingSamples) DropSamples();
        }
    }

    /// <summary>
    /// The samples go, and every page hears that the column is empty. Callers hold
    /// <see cref="_historyLock"/>. Deliberately no version bump: the samples were never part of what
    /// gets saved, so nothing about the file on disk has changed.
    /// </summary>
    private void DropSamples()
    {
        _showingSamples = false;
        _history.Clear();
        Send(DockJson.Serialize(new DockEnvelope<object?>("clear", null)));
    }

    /// <summary>Adds one item to the timeline. Callers hold <see cref="_historyLock"/>.</summary>
    private void Remember(ChatTimelineItem item)
    {
        // A line that arrived before the connect could take the samples down – our own echo, a test
        // pet, the very first "hej" – replaces them the same way it always did.
        if (_showingSamples) DropSamples();

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
    /// Moderation reaches messages only, which is <see cref="ChatModerationEvent.Affects"/>'s rule –
    /// shared with the overlay, so the same ban cannot take a line off one surface and leave it on
    /// another.
    /// </summary>
    private static bool IsAffected(ChatTimelineItem item, ChatModerationEvent moderation) =>
        item.Message is { } message && moderation.Affects(message);

    /// <summary>
    /// Whether we are connected, and what went wrong if not. The dock's alone: it is information
    /// about the app rather than about the conversation, and the page that carries it onto the
    /// broadcast should not be told which of our tokens expired.
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
    /// The paid readings waiting on the streamer, to the dock alone. Sent as the whole list rather
    /// than as one request at a time, the way the hype train is: a bar showing the wrong queue is
    /// worse than one that redraws a little more often, and a dock that reconnects mid-decision gets
    /// the same shape in its hello.
    ///
    /// <para>Never anywhere but the dock. The text is a stranger's words waiting to be judged, and
    /// the pages that can be added to a scene are the ones the viewers are looking at.</para>
    /// </summary>
    public void PublishTts() =>
        SendTo(DockView.Dock, DockJson.Serialize(new DockEnvelope<IReadOnlyList<TtsEntry>>("tts", TtsSnapshot())));

    private IReadOnlyList<TtsEntry> TtsSnapshot() => TtsPending?.Invoke() ?? [];

    /// <summary>
    /// Hands one clip to the reading pages. Addressed to them alone: it carries a playable address,
    /// and the pages on the broadcast that draw chat have no business fetching it.
    /// </summary>
    /// <returns>
    /// How many pages were told. Zero means the browser source is missing from the scene, and the
    /// caller has to treat the reading as undelivered rather than wait for an acknowledgement that
    /// nobody is going to send.
    /// </returns>
    public int PublishTtsPlay(string playbackId, string url, TtsClip clip)
    {
        // Whose words these are travels with the clip only when there is a card to draw them on.
        // Deciding it here rather than in the page is the whole point: a frame that was never sent
        // cannot be read by a browser source sitting on the broadcast, and this is the one place in
        // the app that knows both what the settings say and who is listening.
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
    /// Pushes the card's appearance, so a slider in the app lands without anyone reloading OBS. To
    /// the reading pages alone: nothing else draws it.
    /// </summary>
    public void PublishTtsWidget() =>
        SendTo(DockView.Tts, DockJson.Serialize(new DockEnvelope<DockTtsWidget>("ttsWidget", DockTtsWidget.From(settings.Tts))));

    /// <summary>
    /// Draws the card for a few seconds with nothing playing, for the preview button in the settings.
    /// The only way to see where it has landed in the scene without spending a viewer's money on
    /// finding out – and unlike the test reading it costs no ElevenLabs characters either.
    /// </summary>
    /// <returns>How many reading pages drew it, so the app can say when there is nothing to preview on.</returns>
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
    /// A nickname was given, changed or taken away. Every dock gets it, including the one that made
    /// the change: the name has to land on the lines already on screen, and rendering it from a
    /// lookup rather than from the message means one frame is enough to update the whole column.
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

    /// <summary>
    /// Sends one pet home ahead of time, because the redemption behind it was paid back. Its own
    /// frame rather than a spawn carrying <c>removedId</c>: nothing is arriving to make room here,
    /// and a spawn frame with no pet in it would be a lie the overlay has to unpick.
    /// </summary>
    public void PublishPetRemoved(string petId) =>
        SendEverywhere(DockJson.Serialize(new DockEnvelope<DockPetRemoved>("petRemove", new DockPetRemoved(petId))));

    /// <summary>
    /// Empties the lawn – the app has left the channel these pets were bought in. Sent as its own
    /// frame because a reconnecting overlay would otherwise be handed the pets back from a registry
    /// that has already let them go, and a viewer from the previous channel would be walking about
    /// in front of the new one.
    /// </summary>
    public void PublishPetsCleared() =>
        SendEverywhere(DockJson.Serialize(new DockEnvelope<object?>("petsClear", null)));

    /// <summary>Pushes size and limits to the pet overlay so slider changes land without a reload.</summary>
    public void PublishPetSettings() =>
        SendEverywhere(DockJson.Serialize(new DockEnvelope<DockPetSettings>("petSettings", BuildPetSettings())));

    /// <summary>Hands the overlay the species list again, after the user reloads the pets folder.</summary>
    public void PublishPetCatalog() =>
        SendEverywhere(DockJson.Serialize(new DockEnvelope<IReadOnlyList<DockPetDefinition>>("petCatalog", BuildPetCatalog())));

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
    ///
    /// <para>Sent as one frame that says what they are, rather than as the messages they pretend to
    /// be. The pages need to be able to tell: on the stream overlay a replayed line is dropped once
    /// it is older than the fade time, and these all carry the moment the app started – as ordinary
    /// chat they would be gone from every reload a few minutes into the run, which is exactly when
    /// somebody is placing the browser source in OBS and has nothing else to aim at.</para>
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
    /// What the stream overlay opens onto: its own appearance and the tail of the timeline. There is
    /// no reason to hand it the whole two hundred lines, but the tail has to be deeper than the dozen
    /// it draws – the page throws away bots, commands, switched-off events and anything too old to be
    /// worth replaying, and a quiet stretch that ended in bot chatter would otherwise open onto an
    /// empty column with perfectly good lines sitting just above the cut.
    /// </summary>
    private string BuildStreamHello()
    {
        int depth = Math.Clamp(settings.Stream.MaxMessages * 4, 40, HistoryLimit);
        ChatTimelineItem[] history;
        bool samples;
        lock (_historyLock)
        {
            history = _history.TakeLast(depth).ToArray();
            samples = _showingSamples;
        }
        // Said out loud, because the page treats the two completely differently: replayed chat is
        // dropped once it is too old to be worth putting back in front of the viewers, and a preview
        // has no age – it is what the overlay is aimed at while nothing is connected.
        return DockJson.Serialize(new DockStreamHello("hello", settings.Stream, history.Select(ToDock).ToArray(), samples));
    }

    /// <summary>
    /// The pet overlay's own greeting: its settings, the drawings and the pets already alive. It
    /// used to get the dock's, which carries the nicknames, who is logged in and the whole chat
    /// history – none of which a lawn full of creatures has any use for, and all of which sits on
    /// the broadcast machine as a browser source.
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
            // A train that has already run out is not news to a dock opening now, and replaying it
            // would put a strip on screen for something that finished before the page existed.
            hypeTrain is { } train && train.IsWorthShowing(DateTimeOffset.Now) ? DockMapper.ToDock(train) : null,
            // The whole book, not the names in the history: a dock that scrolls back to a line from
            // an hour ago should still see the nickname on it.
            nicknames.Snapshot().Select(entry => new DockNickname(entry.UserId, entry.Login, entry.Text)).ToArray(),
            // A reading waiting for an answer outlives an OBS restart, so the bar has to come back
            // with it rather than leaving the viewer's purchase to time out unseen.
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
    /// The chat and everything that happens in it, to the pages that read chat. The pet lawn is
    /// deliberately not one of them.
    ///
    /// <para><b>Why the lawn is left out.</b> Its socket carries a bounded queue like every other,
    /// and a client that fills it is dropped rather than allowed to stall the fan-out. The lawn
    /// reads none of these frames – but during a raid it would be handed every one of them, and a
    /// queue filled by chat it was never going to look at would close the socket. That now costs
    /// real money: no lawn connected is what refunds the pets currently on it, so a busy minute of
    /// chat could hand back the points for every pet on screen.</para>
    /// </summary>
    private void Send(string payload) => Fan(payload, client => client.View != DockView.Pets);

    /// <summary>
    /// Sends to one kind of page only. What the dock alone gets is everything a viewer has no
    /// business seeing – the nicknames, who is logged in, the reading settings – and what the stream
    /// overlay alone gets is its own appearance. Addressed rather than filtered in the browser: a
    /// frame that is never sent cannot be read by a page sitting on the broadcast.
    /// </summary>
    private void SendTo(DockView view, string payload) => Fan(payload, client => client.View == view);

    /// <summary>
    /// The pet frames, to every page. Not narrowed to <see cref="DockView.Pets"/>: a lawn added as a
    /// browser source before that view existed connects as a dock, and narrowing would leave it
    /// showing nothing at all. They are few and far between, so a dock that ignores them pays
    /// nothing for receiving them.
    /// </summary>
    private void SendEverywhere(string payload) => Fan(payload, _ => true);

    /// <summary>
    /// Enumerated over the dictionary rather than over its <c>Values</c>: that property builds a
    /// fresh list of every client each time it is read, and this runs on every chat line.
    /// </summary>
    private void Fan(string payload, Func<Client, bool> wants)
    {
        foreach (KeyValuePair<Guid, Client> entry in _clients)
        {
            Client client = entry.Value;
            if (!wants(client)) continue;
            // A dock that cannot keep up is dropped rather than allowed to stall the whole fan-out.
            if (!client.Outbound.Writer.TryWrite(payload)) client.Outbound.Writer.TryComplete();
        }
    }

    /// <summary>How many connected pages are of one kind. Same reason as <see cref="Fan"/>.</summary>
    private int CountOf(DockView view)
    {
        int count = 0;
        foreach (KeyValuePair<Guid, Client> entry in _clients)
            if (entry.Value.View == view) count++;
        return count;
    }

    /// <summary>Runs one dock connection until the socket closes.</summary>
    public async Task RunClientAsync(WebSocket socket, bool canSend, CancellationToken cancellationToken, DockView view = DockView.Dock)
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
        _clients[id] = new Client(outbound, view);
        if (view == DockView.Pets) PetOverlayCountChanged?.Invoke(PetOverlayCount);
        else if (view == DockView.Tts) TtsOverlayCountChanged?.Invoke(TtsOverlayCount);

        try
        {
            string hello = view switch
            {
                DockView.Stream => BuildStreamHello(),
                DockView.Pets => BuildPetsHello(),
                // The reading page is handed its own appearance and nothing else. It has no state to
                // restore – a clip that was playing when OBS restarted is over – and it is a browser
                // source on the broadcast machine, so the less it is ever sent the better.
                DockView.Tts => DockJson.Serialize(new DockTtsHello("hello", DockTtsWidget.From(settings.Tts))),
                _ => BuildHello(canSend)
            };
            await SendFrameAsync(socket, hello, cancellationToken).ConfigureAwait(false);
            // Completing the outbound channel when the socket closes is what ends the loop below;
            // without it an idle dock would sit in _clients until the next message happened to arrive.
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
    /// Reads what a page sends back. For a dock that is pings only, and reading them is what
    /// detects a closed socket. Two pages say more: the pet overlay reports each pet it has drawn,
    /// and the reading page reports each clip it has played – both being the only thing that can
    /// tell a delivered redemption from one the browser source never got round to.
    ///
    /// <para>Nothing here may act on a page's say-so beyond that: these sockets sit on the
    /// broadcast machine, so an unknown frame is dropped rather than guessed at.</para>
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
                if (view is not (DockView.Pets or DockView.Tts) || result.MessageType != WebSocketMessageType.Text) continue;

                // A frame longer than an id is not one of ours; the rest of it is dropped so a page
                // cannot grow this buffer without limit.
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
    /// One frame from a page that is allowed to report something. Each view is only ever listened to
    /// for its own kind of report: a pet lawn cannot answer for a reading, and the reverse.
    ///
    /// <para>Every value is checked for its kind before it is read. <c>GetString</c> on a number or
    /// an object throws <see cref="InvalidOperationException"/>, which is not a
    /// <see cref="JsonException"/> and would travel straight out of the socket loop – so a page
    /// sending <c>{"type":1}</c> could take the reader down. Both are caught anyway: this parses
    /// input from a browser, and nothing it sends may end a connection.</para>
    /// </summary>
    private void ReadReport(string payload, DockView view)
    {
        try
        {
            // Disposed rather than left to the collector: JsonDocument rents its backing buffer
            // from the shared ArrayPool, and one that is never disposed never gives it back.
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement frame = document.RootElement;
            if (frame.ValueKind != JsonValueKind.Object) return;
            if (!frame.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String) return;
            if (Text(frame, "id") is not { Length: > 0 } id) return;

            switch (type.GetString())
            {
                case "petShown" when view == DockView.Pets:
                    PetShown?.Invoke(id);
                    return;
                case "ttsPlayed" when view == DockView.Tts:
                    TtsPlaybackFinished?.Invoke(id, true);
                    return;
                case "ttsFailed" when view == DockView.Tts:
                    TtsPlaybackFinished?.Invoke(id, false);
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
