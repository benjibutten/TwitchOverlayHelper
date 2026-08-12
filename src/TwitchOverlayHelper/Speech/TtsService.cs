using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using TwitchOverlayHelper.Diagnostics;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Speech;

/// <summary>Which currency bought the reading. It decides what a refusal can actually give back.</summary>
public enum TtsSource
{
    /// <summary>A custom Power-up, paid in bits. Nothing here can hand bits back.</summary>
    PowerUp,

    /// <summary>A channel points reward. Refundable when this app created it.</summary>
    Reward
}

/// <summary>Where one reading has got to. Everything past <see cref="Speaking"/> is an ending.</summary>
public enum TtsState
{
    /// <summary>Waiting for the streamer to say yes or no.</summary>
    Pending,

    /// <summary>Approved and waiting for its turn at the speakers.</summary>
    Queued,

    /// <summary>Being read out loud right now.</summary>
    Speaking,

    /// <summary>Read out in full.</summary>
    Spoken,

    /// <summary>The streamer said no.</summary>
    Rejected,

    /// <summary>Nobody answered it in time.</summary>
    Expired,

    /// <summary>Approved, but the voice never arrived – no key, no credit, no network.</summary>
    Failed
}

/// <summary>
/// One paid reading as it arrives. <paramref name="Refundable"/> is carried rather than worked out
/// later: it depends on how the request came in and on the settings at that moment, and a request
/// that outlives a settings change must still be answered on the terms it was accepted under.
/// </summary>
public sealed record TtsRequest(
    string Id,
    TtsSource Source,
    string RewardId,
    bool Refundable,
    string UserId,
    string DisplayName,
    string Text,
    int Cost,
    DateTimeOffset At);

/// <summary>
/// One row of the dock's approval bar. A view of a request rather than the request itself: the bar
/// sits in a browser source on the streaming machine, and what it needs is a name, a sentence and
/// two buttons.
/// </summary>
public sealed record TtsEntry(
    string Id,
    string Viewer,
    string Text,
    int Cost,
    /// <summary>"powerUp" or "reward" – the bar words its refuse button from this.</summary>
    string Source,
    string State,
    /// <summary>Whether refusing actually gives anything back. False for every bits Power-up.</summary>
    bool Refundable,
    long At,
    /// <summary>When an unanswered request will be let go, so the bar can count down.</summary>
    long? DeadlineAt);

/// <summary>What became of a request that was handed in.</summary>
public sealed record TtsOutcome(bool Accepted, TtsState State, string Reason)
{
    public static readonly TtsOutcome NotForUs = new(false, TtsState.Rejected, string.Empty);
}

/// <summary>
/// Reads what viewers pay to have read: takes a redemption, waits for the streamer's yes when one is
/// wanted, has ElevenLabs say it, and plays it on this machine – one at a time, in the order they
/// were approved.
///
/// <para><b>Why a queue and not a fire-and-forget.</b> Two readings at once is noise, and the second
/// one is money spent on something nobody could make out. So a reading holds the speakers until it
/// has finished, and the rest wait.</para>
///
/// <para><b>Why an answer at the end rather than at the yes.</b> On the channel points route this
/// app owes Twitch a verdict, and the only honest moment to give it is once the words have actually
/// come out of the speakers. Approving something whose synthesis then fails would leave the points
/// spent on silence; the failure is a refund instead. The bits route has no such moment – Twitch
/// offers no endpoint to answer a Power-up redemption at all – so there the verdict is only ever a
/// line in the log and a word in the dock.</para>
/// </summary>
public sealed class TtsService : IDisposable
{
    /// <summary>Roughly a stream's worth of readings kept on disk, so replaying one costs nothing.</summary>
    private const int AudioFileLimit = 60;

    /// <summary>How often unanswered requests are checked against their deadline.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    private readonly AppSettings _settings;
    private readonly SpeechSecretStore _secrets;
    private readonly ElevenLabsClient _elevenLabs;
    private readonly Func<string, double, CancellationToken, Task> _play;
    private readonly string _audioDirectory;
    private readonly Timer _timer;

    private readonly Lock _gate = new();
    /// <summary>Everything not yet finished, oldest first – which is the order the bar shows them in.</summary>
    private readonly List<Entry> _open = [];

    /// <summary>
    /// Cancels whatever is at the speakers right now, for the dock's stop button. Held by the
    /// reading loop for a paid reading and by <see cref="SpeakTestAsync"/> for a test – there is one
    /// audio element at the far end, so there is one of these.
    /// </summary>
    private CancellationTokenSource? _speaking;

    /// <summary>Set while a test holds <see cref="_speaking"/>, so its own finally can let it go.</summary>
    private CancellationTokenSource? _testPlayback;
    private bool _pumping;
    private bool _disposed;

    public TtsService(
        HttpClient httpClient,
        AppSettings settings,
        SpeechSecretStore secrets,
        Func<string, double, CancellationToken, Task> play,
        string? audioDirectory = null)
    {
        _settings = settings;
        _secrets = secrets;
        _play = play;
        _elevenLabs = new ElevenLabsClient(httpClient);
        _audioDirectory = audioDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TwitchOverlayHelper", "ttscache");
        _timer = new Timer(_ => Sweep(), null, SweepInterval, SweepInterval);
    }

    /// <summary>A voice and an ElevenLabs key are in place, so something could be read.</summary>
    public bool CanSpeak => _settings.Tts.VoiceId.Length > 0 && _secrets.Current.ElevenLabsApiKey.Length > 0;

    /// <summary>Whether the feature is both switched on and able to do anything.</summary>
    public bool IsConfigured => _settings.Tts.Enabled && CanSpeak;

    /// <summary>Raised whenever the bar's contents changed, so the dock can be handed the new list.</summary>
    public event Action? Changed;

    /// <summary>Raised when something new is waiting for a yes – what lights the edge glow.</summary>
    public event Action<TtsRequest>? ApprovalNeeded;

    /// <summary>
    /// Raised once a request has reached an ending, with the verdict Twitch should be given. Only
    /// ever fires for requests that carried <see cref="TtsRequest.Refundable"/>; everything else has
    /// nobody to tell.
    /// </summary>
    public event Action<TtsRequest, RedemptionStatus, string>? Answered;

    /// <summary>Raised for the app's own status line: what just happened, worded for a human.</summary>
    public event Action<string>? Noticed;

    /// <summary>
    /// Takes a redemption that matched the configured trigger. Answers rather than returning
    /// quietly, because the caller may owe Twitch a verdict either way.
    /// </summary>
    public TtsOutcome Handle(TtsRequest request)
    {
        TtsSettings tts = _settings.Tts;

        if (!tts.Enabled) return Refuse(request, TtsState.Rejected, "uppläsning är avstängd i appen");
        if (!CanSpeak) return Refuse(request, TtsState.Failed, "ingen röst eller ElevenLabs-nyckel är inställd");

        string text = TtsText.Clean(request.Text, tts.MaxCharacters);
        if (text.Length == 0) return Refuse(request, TtsState.Rejected, "inlösen innehöll ingen text att läsa upp");

        var entry = new Entry(request with { Text = text })
        {
            State = tts.RequireApproval ? TtsState.Pending : TtsState.Queued,
            DeadlineAt = tts.RequireApproval
                ? request.At + TimeSpan.FromSeconds(tts.ApprovalTimeoutSeconds)
                : null
        };

        bool full;
        lock (_gate)
        {
            // The same redemption arriving twice – a reconnect overlap, a retry – must not be read
            // out twice or answered twice.
            if (_open.Any(open => string.Equals(open.Request.Id, request.Id, StringComparison.Ordinal)))
                return TtsOutcome.NotForUs;

            full = _open.Count >= tts.QueueLimit;
            if (!full) _open.Add(entry);
        }

        // A queue nobody will reach the end of is worse than a refusal the viewer hears about now –
        // and on the channel points route the refusal hands the points straight back. Answered
        // outside the lock, because refusing raises the events that redraw the dock and answer
        // Twitch, and nothing that reaches the network belongs inside this lock.
        if (full) return Refuse(request, TtsState.Rejected, "kön för uppläsning är full");

        Changed?.Invoke();
        if (entry.State == TtsState.Pending)
        {
            ApprovalNeeded?.Invoke(entry.Request);
            return new TtsOutcome(true, TtsState.Pending, "väntar på godkännande");
        }

        StartPump();
        return new TtsOutcome(true, TtsState.Queued, "köad för uppläsning");
    }

    /// <summary>The streamer said yes. Nothing is answered to Twitch yet – that waits for the words.</summary>
    public bool Approve(string id)
    {
        lock (_gate)
        {
            if (Find(id) is not { State: TtsState.Pending } entry) return false;
            entry.State = TtsState.Queued;
            entry.DeadlineAt = null;
        }
        Changed?.Invoke();
        StartPump();
        return true;
    }

    /// <summary>
    /// The streamer said no. On the channel points route this is the refund; on the bits route the
    /// reading simply does not happen, which is all a refusal can mean there.
    /// </summary>
    public bool Reject(string id)
    {
        Entry? entry;
        lock (_gate)
        {
            entry = Find(id);
            // A reading already at the speakers is stopped rather than refused: it has been heard,
            // in part, and the stop button is what takes it off.
            if (entry is null || entry.State is not (TtsState.Pending or TtsState.Queued)) return false;
            _open.Remove(entry);
        }
        Finish(entry, TtsState.Rejected, "nekad av streamern");
        return true;
    }

    /// <summary>
    /// Stops what is playing. The reading counts as delivered: the viewers heard it, and the streamer
    /// cutting it short is not the viewer's fault to pay for.
    /// </summary>
    public bool Stop()
    {
        CancellationTokenSource? speaking;
        lock (_gate) speaking = _speaking;
        if (speaking is null) return false;
        try { speaking.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    /// <summary>
    /// Twitch says one of the channel's redemptions changed status without us asking – the streamer
    /// worked the queue in the dashboard while the request sat here. Their answer is final: the
    /// redemption has left the queue, and this app can neither fulfil nor refund it any more.
    ///
    /// <para>A cancellation is the one that changes what happens. The viewer has had their points
    /// back, so the message must not be read out after all – and if it is already being read, it is
    /// taken off the way the stop button takes one off. Anything else is let run; only the answer at
    /// the end is dropped, because sending it would be five failed attempts at a redemption Twitch has
    /// already closed.</para>
    ///
    /// <para>The app's own answers come back through this event as well. They never match anything
    /// here: an entry leaves <see cref="_open"/> before Twitch is ever told about it.</para>
    /// </summary>
    /// <returns>Whether the redemption was one of this queue's, so the caller can stop looking.</returns>
    public bool HandleExternalUpdate(string redemptionId, string status)
    {
        bool canceled = status.Equals("CANCELED", StringComparison.OrdinalIgnoreCase);
        Entry entry;
        CancellationTokenSource? speaking = null;
        lock (_gate)
        {
            if (Find(redemptionId) is not { } found) return false;
            entry = found;
            entry.Released = canceled ? "återbetalad i Twitch" : "avslutad i Twitch";
            if (!canceled) return true;

            // Already at the speakers. The reading loop owns it from here: cancelling stops the sound,
            // and the loop takes it out of the queue and leaves it unanswered because it is released.
            // Taken out here in every other case – including a reading whose loop has just let the
            // speakers go, which would otherwise be left sitting in the bar with nobody to remove it.
            if (entry.State == TtsState.Speaking) speaking = _speaking;
            if (speaking is null) _open.Remove(entry);
        }

        if (speaking is not null)
        {
            try { speaking.Cancel(); } catch (ObjectDisposedException) { }
            return true;
        }

        entry.State = TtsState.Rejected;
        AppLog.Info($"Uppläsning: {Viewer(entry)}s inlösen återbetalades i Twitch – meddelandet läses inte upp.");
        Noticed?.Invoke($"{Viewer(entry)}s uppläsning ströks – inlösen återbetalades i Twitchs egen kö.");
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Whether this redemption is one the reading queue is still holding. Asked by the sweep that
    /// clears out redemptions from before the app was listening: a reconnect gives it a fresh
    /// cutoff, and everything waiting for the streamer's yes is older than that.
    /// </summary>
    public bool Holds(string redemptionId)
    {
        lock (_gate) return Find(redemptionId) is not null;
    }

    /// <summary>What the dock's approval bar should be showing, oldest first.</summary>
    public IReadOnlyList<TtsEntry> Snapshot()
    {
        lock (_gate)
        {
            return _open.Select(entry => new TtsEntry(
                entry.Request.Id,
                entry.Request.DisplayName,
                entry.Request.Text,
                entry.Request.Cost,
                entry.Request.Source == TtsSource.PowerUp ? "powerUp" : "reward",
                Word(entry.State),
                entry.Request.Refundable,
                entry.Request.At.ToUnixTimeMilliseconds(),
                entry.DeadlineAt?.ToUnixTimeMilliseconds())).ToArray();
        }
    }

    /// <summary>
    /// Reads a line straight out, for the test button in the settings. Outside the approval queue:
    /// it is the streamer proving the chain works, not a viewer's purchase.
    ///
    /// <para>It is not, however, outside the speakers. There is one audio element at the far end and
    /// one clip in flight at a time, so a test started over a paid reading would take that reading
    /// off the air and leave the queue behind it waiting on an acknowledgement that is never coming.
    /// A viewer who paid outranks a button, so the test waits its turn rather than barging in.</para>
    /// </summary>
    public async Task SpeakTestAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!CanSpeak) throw new SpeechException("Välj en röst och spara ElevenLabs-nyckeln först.");
        string cleaned = TtsText.Clean(text, _settings.Tts.MaxCharacters);
        if (cleaned.Length == 0) throw new SpeechException("Skriv något att läsa upp.");

        lock (_gate)
        {
            if (_speaking is not null)
                throw new SpeechException("En betald uppläsning pågår just nu – testa igen när den är klar.");
            // Claimed for the whole test, so a redemption approved a moment later queues behind it
            // instead of playing on top of it. The pump only ever starts a reading with this free.
            _speaking = _testPlayback = new CancellationTokenSource();
        }

        try
        {
            TtsSettings tts = _settings.Tts;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _testPlayback!.Token);
            string file = await EnsureAudioAsync(cleaned, tts, linked.Token).ConfigureAwait(false);
            await _play(file, tts.Volume, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The dock's stop button reaches whatever is at the speakers, a test included, and the
            // app closing does the same. Turned into the exception the callers already handle:
            // letting a cancellation out of here would take an async void click handler with it.
            throw new SpeechException("Uppläsningen avbröts.");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_speaking, _testPlayback)) _speaking = null;
                _testPlayback?.Dispose();
                _testPlayback = null;
            }
            // A redemption may have been approved while the test held the speakers; nothing else
            // would start the queue moving again.
            StartPump();
        }
    }

    /// <summary>
    /// Lets everything waiting go without answering Twitch – the app is closing, or has left the
    /// channel. Like the pets' ledger this is deliberately not a round of refunds: a refund fired
    /// off while the window is disappearing is worse than a redemption left unfulfilled, which the
    /// streamer can still work by hand and which the next connection's sweep settles.
    ///
    /// <para>Every entry is marked on the way out, including the one at the speakers. Without that
    /// the reading being cut short would still reach <see cref="Finish"/> and answer Twitch – on a
    /// shutdown, through a ledger that has already been disposed.</para>
    /// </summary>
    public void Reset()
    {
        bool had;
        lock (_gate)
        {
            had = _open.Count > 0;
            foreach (Entry entry in _open) entry.Released = "appen släppte kanalen";
            _open.Clear();
        }
        Stop();
        if (had) Changed?.Invoke();
    }

    /// <summary>Lets go of everything nobody answered in time. Internal so the tests can drive it.</summary>
    internal void Sweep()
    {
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<Entry> expired = [];
            lock (_gate)
            {
                foreach (Entry entry in _open.ToArray())
                {
                    if (entry.State != TtsState.Pending || entry.DeadlineAt is not { } deadline || now < deadline) continue;
                    _open.Remove(entry);
                    expired.Add(entry);
                }
            }

            foreach (Entry entry in expired) Finish(entry, TtsState.Expired, "ingen hann svara");
        }
        catch (Exception ex)
        {
            // A timer callback that throws takes the process with it, and this one runs while the
            // streamer is live.
            AppLog.Error("Uppläsning: fel i tidsgränskontrollen", ex);
        }
    }

    /// <summary>
    /// The reading loop. One at a time, started on demand and ending when the queue runs dry – there
    /// is nothing to keep warm between two redemptions half an hour apart.
    /// </summary>
    private void StartPump()
    {
        lock (_gate)
        {
            if (_pumping || _disposed) return;
            _pumping = true;
        }
        _ = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                Entry entry;
                CancellationTokenSource cancellation;
                lock (_gate)
                {
                    // A test is at the speakers. Stopping here rather than playing over it is what
                    // keeps the two from cutting each other off; the test's finally starts the queue
                    // moving again the moment it is done.
                    if (_speaking is not null)
                    {
                        _pumping = false;
                        return;
                    }
                    if (_open.FirstOrDefault(open => open.State == TtsState.Queued) is not { } next)
                    {
                        _pumping = false;
                        return;
                    }
                    entry = next;
                    entry.State = TtsState.Speaking;
                    cancellation = _speaking = new CancellationTokenSource();
                }

                TtsState state;
                string reason;
                try
                {
                    Changed?.Invoke();
                    (state, reason) = await SpeakAsync(entry, cancellation.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // SpeakAsync answers rather than throws for everything that was foreseen, so this
                    // is a bug or something new. It still has to become an ending here: an entry left
                    // as Speaking would hold the speakers for good – the loop refuses to start a
                    // reading while _speaking is set, so every request after it would queue and none
                    // of them would ever be read.
                    AppLog.Error("Uppläsning: oväntat fel under uppläsningen", ex);
                    state = TtsState.Failed;
                    reason = "ett oväntat fel avbröt uppläsningen";
                }

                string? released;
                lock (_gate)
                {
                    _open.Remove(entry);
                    if (ReferenceEquals(_speaking, cancellation)) _speaking = null;
                    released = entry.Released;
                }
                cancellation.Dispose();

                // No longer ours to answer: the app is closing or has left the channel, or the
                // streamer settled the redemption in Twitch's own queue while it was playing. Either
                // way the verdict is somebody else's, and one sent from here would go to a redemption
                // that is not there any more.
                if (released is not null)
                {
                    AppLog.Info($"Uppläsning: {Viewer(entry)}s inlösen lämnades obesvarad – {released}.");
                    Changed?.Invoke();
                    continue;
                }
                Finish(entry, state, reason);
            }
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the loop must not die holding the flag – nothing would ever be
            // read again until the app restarted.
            AppLog.Error("Uppläsning: kön stannade", ex);
            lock (_gate) _pumping = false;
        }
    }

    private async Task<(TtsState State, string Reason)> SpeakAsync(Entry entry, CancellationToken cancellationToken)
    {
        TtsSettings tts = _settings.Tts;
        // Read once, so a name that arrives as part of the spoken line is the one the bar showed.
        string spoken = tts.AnnounceName && entry.Request.DisplayName.Length > 0
            ? $"{entry.Request.DisplayName} säger: {entry.Request.Text}"
            : entry.Request.Text;

        // Whether the clip ever reached an output. A stop during the ElevenLabs call is a reading
        // nobody heard a syllable of, and calling that "delivered" is the one mistake here that
        // silently keeps a viewer's money for silence – so the two halves are told apart rather than
        // both being caught as "the streamer pressed stop".
        bool reachedSpeakers = false;
        try
        {
            string file;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                // The synthesis alone; playback is not on a clock, because a long message is long on
                // purpose and the stop button is there for the rest.
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                file = await EnsureAudioAsync(spoken, tts, timeout.Token).ConfigureAwait(false);
            }

            reachedSpeakers = true;
            await _play(file, tts.Volume, cancellationToken).ConfigureAwait(false);
            return (TtsState.Spoken, "uppläst");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped by the streamer, or by the app closing. Once it was playing the viewers heard
            // some of it and cutting it short is the streamer's decision, not the viewer's fault to
            // pay for; before that there was nothing to hear, and it is paid back.
            return reachedSpeakers
                ? (TtsState.Spoken, "avbruten av streamern")
                : (TtsState.Failed, "avbruten innan något hann läsas upp");
        }
        catch (Exception ex) when (ex is SpeechException or HttpRequestException or OperationCanceledException)
        {
            AppLog.Warn($"Uppläsning: kunde inte läsa upp {entry.Request.DisplayName}s meddelande: {ex.Message}");
            return (TtsState.Failed, ex.Message);
        }
    }

    /// <summary>Returns the path to the MP3 for this line, synthesising it only if it is new.</summary>
    private async Task<string> EnsureAudioAsync(string spoken, TtsSettings tts, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_audioDirectory, CacheKey(spoken, tts) + ".mp3");
        if (File.Exists(path)) return path;

        byte[] audio = await _elevenLabs
            .SynthesizeAsync(spoken, tts.VoiceId, _secrets.Current.ElevenLabsApiKey, tts.ElevenLabsModel, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(_audioDirectory);
            string tempPath = path + ".tmp";
            await File.WriteAllBytesAsync(tempPath, audio, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, path, true);
            Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SpeechException("Ljudfilen kunde inte sparas: " + ex.Message);
        }
        return path;
    }

    /// <summary>Voice and model are part of the key, so changing either re-reads the same line.</summary>
    private static string CacheKey(string spoken, TtsSettings tts)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tts.VoiceId}|{tts.ElevenLabsModel}|{spoken}"));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(_audioDirectory).GetFiles("*.mp3");
            if (files.Length <= AudioFileLimit) return;
            foreach (FileInfo file in files.OrderBy(file => file.LastWriteTimeUtc).Take(files.Length - AudioFileLimit + 10))
                file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // A cache that cannot be tidied is still a working cache.
        }
    }

    /// <summary>
    /// A request that never made it into the queue at all. Answered on the spot rather than tracked:
    /// there is no reading whose fate could still change the verdict.
    /// </summary>
    private TtsOutcome Refuse(TtsRequest request, TtsState state, string reason)
    {
        Finish(new Entry(request), state, reason);
        return new TtsOutcome(false, state, reason);
    }

    /// <summary>
    /// One ending: Twitch is told when there is something to tell it, the app says what happened,
    /// and the bar is redrawn without the request.
    /// </summary>
    private void Finish(Entry entry, TtsState state, string reason)
    {
        entry.State = state;
        bool spoken = state == TtsState.Spoken;
        string viewer = Viewer(entry);

        // Released means Twitch has no verdict left to take: the redemption was settled in the
        // dashboard, or the app has let the channel go. Every other refundable ending is answered.
        if (entry.Request.Refundable && entry.Released is null)
            Answered?.Invoke(entry.Request, spoken ? RedemptionStatus.Fulfilled : RedemptionStatus.Canceled, reason);

        AppLog.Info(spoken
            ? $"Uppläsning: {viewer}s meddelande lästes upp – {reason}."
            : $"Uppläsning: {viewer}s meddelande lästes inte upp – {reason}.");

        // Three endings, not two. A refusal that cannot pay back has two quite different causes, and
        // saying "bits" about a channel points reward would point the streamer at the wrong
        // limitation – theirs is fixable by creating the reward from the app, the other is not
        // fixable at all. What is said about bits is scoped to what is actually known: the app
        // cannot pay them back because Twitch exposes no endpoint for it. Whether the streamer can
        // do it by hand in the dashboard is Twitch's business, not ours to promise either way.
        Noticed?.Invoke(spoken
            ? $"Läste upp {viewer}s meddelande."
            : entry.Request.Refundable
                ? $"{viewer} fick tillbaka {entry.Request.Cost} poäng – {reason}."
                : entry.Request.Source == TtsSource.PowerUp
                    ? $"{viewer}s uppläsning: {reason}. Appen kan inte betala tillbaka bits – Twitch har inget API för det."
                    : $"{viewer}s uppläsning: {reason}. Belöningen är inte skapad av appen, så poängen måste lämnas tillbaka i Twitchs egen kö.");

        Changed?.Invoke();
    }

    /// <summary>The viewer's name, or their id when Twitch sent no display name to go with it.</summary>
    private static string Viewer(Entry entry) =>
        entry.Request.DisplayName.Length > 0 ? entry.Request.DisplayName : entry.Request.UserId;

    private Entry? Find(string id) =>
        _open.FirstOrDefault(entry => string.Equals(entry.Request.Id, id, StringComparison.Ordinal));

    /// <summary>Kept as camelCase strings rather than numbers so the dock's CSS can key off them.</summary>
    private static string Word(TtsState state) => state switch
    {
        TtsState.Pending => "pending",
        TtsState.Queued => "queued",
        TtsState.Speaking => "speaking",
        TtsState.Spoken => "spoken",
        TtsState.Rejected => "rejected",
        TtsState.Expired => "expired",
        _ => "failed"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        Stop();
    }

    /// <summary>One request and the mutable part of its life. Guarded by <see cref="_gate"/>.</summary>
    private sealed class Entry(TtsRequest request)
    {
        public TtsRequest Request { get; } = request;
        public TtsState State { get; set; } = TtsState.Pending;
        public DateTimeOffset? DeadlineAt { get; set; }

        /// <summary>
        /// Why this redemption stopped being ours to answer, or null while it still is. Set by
        /// <see cref="Reset"/>, where it is meant to stay unfulfilled for the next sweep to find, and
        /// by <see cref="HandleExternalUpdate"/>, where Twitch has already taken it out of the queue.
        /// Every ending checks it before answering Twitch.
        /// </summary>
        public string? Released { get; set; }
    }
}
