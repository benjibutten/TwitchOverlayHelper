using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using TwitchOverlayHelper.Diagnostics;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Storage;
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
/// One paid reading as it arrives. <paramref name="Refundable"/> is fixed when the request is
/// accepted, so a request that outlives a settings change is answered on the terms it came in under.
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

/// <summary>One row of the dock's approval bar: a request as the dock is sent it.</summary>
/// <param name="Source">"powerUp" or "reward" – the bar words its refuse button from this.</param>
/// <param name="Refundable">Whether refusing gives anything back. False for every bits Power-up.</param>
/// <param name="DeadlineAt">When an unanswered request will be let go, in unix milliseconds.</param>
public sealed record TtsEntry(
    string Id,
    string Viewer,
    string Text,
    int Cost,
    string Source,
    string State,
    bool Refundable,
    long At,
    long? DeadlineAt);

/// <summary>
/// One synthesised clip on its way to an output, with the viewer and text the overlay's card shows
/// while it plays.
///
/// <para><paramref name="Cost"/> of zero means nothing was paid for it: the test button, and
/// anything else the streamer starts themselves.</para>
/// </summary>
public sealed record TtsClip(
    string FilePath,
    double Volume,
    string Viewer,
    string Text,
    int Cost,
    TtsSource Source);

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
/// <para>On the channel points route Twitch gets its verdict when the reading ends, not at the
/// streamer's yes: a synthesis that fails after approval becomes a refund. Twitch offers no endpoint
/// to answer a Power-up redemption, so on the bits route the verdict is only a line in the log and
/// a word in the dock.</para>
/// </summary>
public sealed class TtsService : IDisposable
{
    /// <summary>How many synthesised readings are kept on disk.</summary>
    private const int AudioFileLimit = 60;

    // The three refusal reasons the chat bot matches on to word its own answer.
    internal const string QueueFullReason = "kön för uppläsning är full";

    internal const string DisabledReason = "uppläsning är avstängd i appen";

    internal const string NotConfiguredReason = "ingen röst eller ElevenLabs-nyckel är inställd";

    /// <summary>How often unanswered requests are checked against their deadline.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    private readonly AppSettings _settings;
    private readonly SpeechSecretStore _secrets;
    private readonly ElevenLabsClient _elevenLabs;
    private readonly Func<TtsClip, CancellationToken, Task> _play;
    private readonly string _audioDirectory;
    private readonly Timer _timer;

    private readonly Lock _gate = new();
    // Everything not yet finished, oldest first, which is the order the bar shows them in.
    private readonly List<Entry> _open = [];

    // Cancels whatever is at the speakers. Held by the reading loop for a paid reading and by
    // SpeakTestAsync for a test; there is one output, so non-null also means the speakers are taken.
    private CancellationTokenSource? _speaking;

    // Set while a test holds _speaking, so its finally can tell whether to release it.
    private CancellationTokenSource? _testPlayback;
    private bool _pumping;
    private bool _disposed;

    public TtsService(
        HttpClient httpClient,
        AppSettings settings,
        SpeechSecretStore secrets,
        Func<TtsClip, CancellationToken, Task> play,
        string? audioDirectory = null)
    {
        _settings = settings;
        _secrets = secrets;
        _play = play;
        _elevenLabs = new ElevenLabsClient(httpClient);
        _audioDirectory = audioDirectory ?? ProfilePaths.Folder("ttscache");
        _timer = new Timer(_ => Sweep(), null, SweepInterval, SweepInterval);
    }

    /// <summary>A voice and an ElevenLabs key are in place, so something could be read.</summary>
    public bool CanSpeak => _settings.Tts.VoiceId.Length > 0 && _secrets.Current.ElevenLabsApiKey.Length > 0;

    /// <summary>Whether the feature is both switched on and able to do anything.</summary>
    public bool IsConfigured => _settings.Tts.Enabled && CanSpeak;

    /// <summary>Raised whenever the bar's contents changed.</summary>
    public event Action? Changed;

    /// <summary>Raised when something new is waiting for a yes.</summary>
    public event Action<TtsRequest>? ApprovalNeeded;

    /// <summary>
    /// Raised once a request has reached an ending, with the verdict Twitch should be given. Only
    /// fires for requests that carried <see cref="TtsRequest.Refundable"/>.
    /// </summary>
    public event Action<TtsRequest, RedemptionStatus, string>? Answered;

    /// <summary>Raised for the app's own status line: what just happened, worded for a human.</summary>
    public event Action<string>? Noticed;

    /// <summary>
    /// Raised for every ending, refundable or not, with the request as it was accepted and the
    /// state it reached – including a request refused before it reached the queue. It says what
    /// happened to the reading, not whether Twitch has taken the verdict.
    /// </summary>
    public event Action<TtsRequest, TtsState, string>? Finished;

    /// <summary>
    /// Takes a redemption that matched the configured trigger and returns what became of it. A
    /// refusal is also raised through <see cref="Answered"/> and <see cref="Finished"/>.
    /// </summary>
    public TtsOutcome Handle(TtsRequest request)
    {
        TtsSettings tts = _settings.Tts;

        if (!tts.Enabled) return Refuse(request, TtsState.Rejected, DisabledReason);
        if (!CanSpeak) return Refuse(request, TtsState.Failed, NotConfiguredReason);

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

        // Refused outside the lock: refusing raises the events that redraw the dock and answer
        // Twitch, and nothing that reaches the network belongs inside it.
        if (full) return Refuse(request, TtsState.Rejected, QueueFullReason);

        Changed?.Invoke();
        if (entry.State == TtsState.Pending)
        {
            ApprovalNeeded?.Invoke(entry.Request);
            return new TtsOutcome(true, TtsState.Pending, "väntar på godkännande");
        }

        StartPump();
        return new TtsOutcome(true, TtsState.Queued, "köad för uppläsning");
    }

    /// <summary>The streamer said yes. Twitch is not answered until the reading has ended.</summary>
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
    /// reading simply does not happen. Returns false for a reading already at the speakers, which
    /// <see cref="Stop"/> takes off instead.
    /// </summary>
    public bool Reject(string id)
    {
        Entry? entry;
        lock (_gate)
        {
            entry = Find(id);
            if (entry is null || entry.State is not (TtsState.Pending or TtsState.Queued)) return false;
            _open.Remove(entry);
        }
        Finish(entry, TtsState.Rejected, "nekad av streamern");
        return true;
    }

    /// <summary>
    /// Stops what is at the speakers. A reading that had started playing counts as delivered; one
    /// stopped during synthesis is paid back.
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
    /// worked the queue in the dashboard. The redemption is closed, so the app no longer answers it.
    ///
    /// <para>On a cancellation the message is not read out, and one already being read is stopped.
    /// On any other status the reading runs on and only the answer at the end is dropped.</para>
    /// </summary>
    /// <returns>Whether the redemption was one of this queue's.</returns>
    public bool HandleExternalUpdate(string redemptionId, string status)
    {
        bool canceled = status.Equals("CANCELED", StringComparison.OrdinalIgnoreCase);
        Entry entry;
        CancellationTokenSource? speaking = null;
        lock (_gate)
        {
            // The app's own answers come back through this event too, but never match: an entry
            // leaves _open before Twitch is told about it.
            if (Find(redemptionId) is not { } found) return false;
            entry = found;
            entry.Released = canceled ? "återbetalad i Twitch" : "avslutad i Twitch";
            if (!canceled) return true;

            // A reading at the speakers is cancelled and left to the reading loop, which removes it
            // and leaves it unanswered because it is released. In every other case it is removed
            // here – including when the loop has just let the speakers go.
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
    /// Whether this redemption is one the reading queue is still holding, so the ledger's startup
    /// sweep leaves it alone.
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
    /// Reads a line straight out, for the test button in the settings, bypassing the approval queue.
    /// Throws <see cref="SpeechException"/> when nothing can be spoken, when a reading already holds
    /// the speakers, or when the test is stopped.
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
            // Claimed for the whole test, so a redemption approved meanwhile queues behind it: the
            // pump only starts a reading while _speaking is null.
            _speaking = _testPlayback = new CancellationTokenSource();
        }

        try
        {
            TtsSettings tts = _settings.Tts;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _testPlayback!.Token);
            string file = await EnsureAudioAsync(cleaned, tts, linked.Token).ConfigureAwait(false);
            // The overlay shows its card for a test too; an empty viewer name would leave a hole in it.
            var clip = new TtsClip(
                file,
                tts.Volume,
                "Test",
                cleaned,
                0,
                tts.Trigger == TtsTrigger.PowerUp ? TtsSource.PowerUp : TtsSource.Reward);
            await _play(clip, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Stopped by the dock's stop button or by the app closing. Rethrown as SpeechException:
            // a cancellation escaping from here would crash an async void click handler.
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
            // A redemption approved while the test held the speakers has nothing else to start it.
            StartPump();
        }
    }

    /// <summary>
    /// Lets everything waiting go without answering Twitch, and stops what is playing – the app is
    /// closing, or has left the channel. The redemptions stay unfulfilled, for the streamer to work
    /// by hand or the next connection's sweep to refund.
    /// </summary>
    public void Reset()
    {
        bool had;
        lock (_gate)
        {
            had = _open.Count > 0;
            // Marked released, the one at the speakers included: cut short, it would otherwise
            // reach Finish and answer Twitch through a ledger that may already be disposed.
            foreach (Entry entry in _open) entry.Released = "appen släppte kanalen";
            _open.Clear();
        }
        Stop();
        if (had) Changed?.Invoke();
    }

    /// <summary>Expires every pending request whose approval deadline has passed.</summary>
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
            // A timer callback that throws takes the process with it.
            AppLog.Error("Uppläsning: fel i tidsgränskontrollen", ex);
        }
    }

    /// <summary>Starts the reading loop unless it is already running. The loop ends when the queue runs dry.</summary>
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
                    // A test is at the speakers; its finally restarts the pump when it is done.
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
                    // SpeakAsync returns a state for every expected failure, so this is a bug. It must
                    // still become an ending: an entry left as Speaking would keep _speaking set, and
                    // nothing after it would ever be read.
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

                // Released while it was playing: the app is leaving the channel, or the streamer
                // settled the redemption in Twitch's own queue. No verdict is sent.
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
            // The loop must not die with _pumping set: nothing would start it again.
            AppLog.Error("Uppläsning: kön stannade", ex);
            lock (_gate) _pumping = false;
        }
    }

    private async Task<(TtsState State, string Reason)> SpeakAsync(Entry entry, CancellationToken cancellationToken)
    {
        TtsSettings tts = _settings.Tts;
        string spoken = tts.AnnounceName && entry.Request.DisplayName.Length > 0
            ? $"{entry.Request.DisplayName} säger: {entry.Request.Text}"
            : entry.Request.Text;

        // Whether the clip ever reached an output: a stop during synthesis is a reading nobody
        // heard, and must not count as delivered.
        bool reachedSpeakers = false;
        try
        {
            string file;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                // The timeout covers the synthesis only; playback has the stop button instead.
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                file = await EnsureAudioAsync(spoken, tts, timeout.Token).ConfigureAwait(false);
            }

            reachedSpeakers = true;
            // The card shows the request's own text: the synthesised line may carry "Kajsa säger:"
            // in front, which is not part of the viewer's message.
            var clip = new TtsClip(
                file,
                tts.Volume,
                entry.Request.DisplayName,
                entry.Request.Text,
                entry.Request.Cost,
                entry.Request.Source);
            await _play(clip, cancellationToken).ConfigureAwait(false);
            return (TtsState.Spoken, "uppläst");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped by the streamer, or by the app closing. Once it was playing it counts as
            // delivered; before that there was nothing to hear, and it is paid back.
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

    /// <summary>Ends a request that never made it into the queue.</summary>
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

        // The verdict has been handed to the ledger, which may still be retrying it: listeners
        // learn that the points are back from the ledger, not from this event.
        Finished?.Invoke(entry.Request, state, reason);

        // A refusal that cannot pay back is worded by its cause: a reward not created by the app
        // is something the streamer can fix, bits are not – Twitch has no endpoint to refund them.
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

    /// <summary>The state as the string the dock is sent; its CSS keys off these.</summary>
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
        /// <see cref="Reset"/> and <see cref="HandleExternalUpdate"/>; every ending checks it before
        /// answering Twitch.
        /// </summary>
        public string? Released { get; set; }
    }
}
