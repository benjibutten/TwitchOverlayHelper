using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TwitchOverlayHelper.Bot;
using TwitchOverlayHelper.Diagnostics;
using TwitchOverlayHelper.Fight;
using TwitchOverlayHelper.History;
using TwitchOverlayHelper.Interop;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Nicknames;
using TwitchOverlayHelper.Overlay;
using TwitchOverlayHelper.Pets;
using TwitchOverlayHelper.Services;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Speech;
using TwitchOverlayHelper.Spins;
using TwitchOverlayHelper.Storage;
using TwitchOverlayHelper.Twitch;
using TwitchOverlayHelper.Updates;
using TwitchOverlayHelper.Web;

namespace TwitchOverlayHelper;

public partial class MainWindow : Window
{
    private const int ToggleHotkeyId = 9001;
    private const int EditHotkeyId = 9002;

    private readonly SettingsStore _settingsStore = new();
    /// <summary>What loading the profile ran into, held until the window is up to show it.</summary>
    private string? _profileNotice;
    /// <summary>True when the notice is serious enough to stop the user rather than pass them by.</summary>
    private bool _profileNoticeIsLoss;
    /// <summary>
    /// Last time's chat, so restarting the app mid-stream does not wipe the column. Only ever holds
    /// what this app saw – Twitch offers no way to ask for chat that happened while we were away.
    /// </summary>
    private readonly ChatHistoryStore _historyStore = new();
    private long _lastSavedHistoryVersion;
    /// <summary>Whether the last history write failed, so the retries are logged once, not per tick.</summary>
    private bool _historySaveFailing;
    /// <summary>Which channel the one-shot fetch of older lines has already been done for.</summary>
    private string? _backfilledChannel;
    /// <summary>
    /// Nicknames are typed by hand and cannot be recovered from Twitch, so they have a file of
    /// their own, written the moment they change with a dated copy beside every save.
    /// </summary>
    private readonly NicknameStore _nicknameStore = new();
    private readonly NicknameBook _nicknames;
    private readonly TwitchBadgeCatalog _badgeCatalog = new();
    private readonly TwitchChatClient _chatClient = new();
    private readonly StartupRegistrySyncService _startupRegistrySyncService = new();
    private readonly System.Net.Http.HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    // Its own client: generating a voice clip is slower than any Twitch call, and a name that
    // takes a few seconds to come back must not push the Twitch timeout up for everything else.
    private readonly System.Net.Http.HttpClient _speechHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly TwitchSession _session;
    private readonly TwitchApiClient _apiClient;
    /// <summary>
    /// The lines said before we connected, from the one service the app asks that is not Twitch –
    /// see <see cref="RecentMessagesClient"/>.
    /// </summary>
    private readonly RecentMessagesClient _recentMessages;
    /// <summary>
    /// What we may send in this channel. Loaded once when the room becomes known, since it is
    /// needed the moment a line is written.
    /// </summary>
    private readonly UsableEmoteCatalog _emotes;
    private readonly TwitchEventSubClient _eventSubClient;
    private readonly RewardCatalog _rewards = new();
    private readonly PowerUpTracker _powerUps = new();
    /// <summary>
    /// Which hype train moments have already been given a card. Kept apart from the strip's
    /// ordering: the strip can refuse a late update and still owe the overlay its card.
    /// </summary>
    private readonly RecentMessageIds _hypeCards = new();
    /// <summary>
    /// Keeps a chat line and the power-up marker for it in that order. The two are raised on
    /// different threads – IRC and EventSub – and a marker that overtook its line would ask the
    /// views to update a message they had never been given.
    /// </summary>
    private readonly System.Threading.Lock _publishGate = new();
    private readonly SpeechSecretStore _speechSecrets = new();
    private readonly SpeechAudioPlayer _audioPlayer;
    private readonly NameSpeechService _nameSpeech;
    private readonly TtsService _tts;
    private readonly TtsAudioStore _ttsAudio = new();
    private readonly BrowserTtsOutput _browserTts;
    private readonly ChatHub _hub;
    private readonly PetRegistry _petRegistry = new();
    /// <summary>
    /// Built in the constructor on purpose: reading the catalogue writes the shipped pets into
    /// the profile, and a field initialiser would run before the profile snapshot is taken.
    /// </summary>
    private readonly PetCatalog _petCatalog;
    private readonly FightCatalog _fightCatalog;
    private readonly FightService _fight;
    private readonly PetService _petService;

    /// <summary>
    /// Every lucky spin win on disk. Built in the constructor like the catalog: its first read can
    /// quarantine or restore files, which must not happen before the profile snapshot.
    /// </summary>
    private readonly SpinWinStore _spinWins;

    /// <summary>Lyckosnurren: draws, books and stages the wins, and runs the gift conversation.</summary>
    private readonly SpinService _spins;

    /// <summary>
    /// Holds every redemption of an app-made pet reward open until the pet has been delivered, and
    /// pays it back when it has not.
    /// </summary>
    private readonly RedemptionLedger _ledger;

    /// <summary>Which channel the ledger's pending redemptions belong to, so a reconnect is told from a switch.</summary>
    private string _ledgerChannel = string.Empty;

    /// <summary>
    /// Whether the reward queue has been read through since the last gap in coverage. Set only by a
    /// sweep that finished, and cleared by every stretch where redemptions were not being
    /// delivered – a reconnect and a channel switch both leave a queue nobody was listening to.
    /// </summary>
    private bool _swept;

    /// <summary>A sweep is out. Coverage is reported more than once, and two sweeps would read the same queue twice.</summary>
    private bool _sweeping;
    /// <summary>
    /// The chat bot: a second Twitch account, the queue that spaces its lines out, and the layer
    /// that decides what is worth saying at all.
    /// </summary>
    private readonly BotAccount _botAccount;

    private readonly BotSender _botSender;
    private readonly BotService _bot;
    private readonly DockServer _dockServer;
    private readonly DockServerContext _dockContext;
    private readonly AppSettings _settings;
    private readonly OverlayWindow _overlay;
    private readonly EdgeAlertWindow _edgeAlerts;
    private readonly EdgeAlertScheduler _edgeScheduler = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<PetRewardRule> _petRewards = [];
    private readonly ConcurrentQueue<ChatTimelineItem> _pendingMessages = new();
    private readonly DispatcherTimer _chatFlushTimer;
    private readonly DispatcherTimer _settingsApplyTimer;
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly DispatcherTimer _historySaveTimer;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private HwndSource? _hwndSource;
    private Button? _recordingButton;
    private bool _loading = true;
    private bool _editing;
    private bool _closing;
    private bool _exitRequested;
    private bool _updatingStartWithWindows;
    private bool _reconnecting;
    private bool _refreshingPetCatalog;
    private DockSettingsWindow? _dockSettingsWindow;
    private StreamSettingsWindow? _streamSettingsWindow;
    private SpeechSettingsWindow? _speechSettingsWindow;
    private TtsWidgetWindow? _ttsWidgetWindow;
    private BotSettingsWindow? _botSettingsWindow;
    private SpinSettingsWindow? _spinSettingsWindow;
    private PetsWindow? _petsWindow;
    private string? _lastBadgeRoom;
    private string? _lastSeenRewardId;
    private string? _lastSeenRewardName;
    private CancellationTokenSource? _badgeLoadCancellation;
    private int _pendingMessageCount;
    private int _chatTimerRequested;
    private int _eventSubGeneration;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Enable(this);
        VersionText.Text = AppVersion.DisplayText;
        _settings = _settingsStore.Load();
        // Before anything is written back: a build that reads an older file, understands part of
        // it and saves would lose settings the per-file copies cannot restore.
        GuardProfile();
        // After the guard: reading the catalogue seeds this version's pets into the profile
        // folder, and the snapshot has to be of what was there before.
        _petCatalog = new PetCatalog();
        _fightCatalog = new FightCatalog();
        _nicknames = _nicknameStore.Load();
        _nicknames.Changed += OnNicknameChanged;
        SyncStartWithWindows();
        _session = new TwitchSession(_httpClient);
        _apiClient = new TwitchApiClient(_httpClient, _session);
        _recentMessages = new RecentMessagesClient(_httpClient);
        _emotes = new UsableEmoteCatalog(_apiClient);
        // Our own line arrives with no emote spans; this is what puts them back, before either view
        // has seen the message.
        _chatClient.ResolveEmotes = _emotes.SpansIn;
        _eventSubClient = new TwitchEventSubClient(_session, _apiClient);
        _audioPlayer = new SpeechAudioPlayer(Dispatcher);
        _nameSpeech = new NameSpeechService(_speechHttpClient, _settings, _speechSecrets, _audioPlayer.PlayAsync);
        _hub = new ChatHub(_settings, _badgeCatalog, _session, _petRegistry, _petCatalog, _nicknames) { SpeechEnabled = _nameSpeech.IsConfigured };
        _browserTts = new BrowserTtsOutput(_hub, _ttsAudio, () => _settings.DockAccessKey);
        _tts = new TtsService(_speechHttpClient, _settings, _speechSecrets, PlayReadingAsync);
        _petService = new PetService(_settings, _petCatalog, _petRegistry, _hub);
        _hub.FightSetup = BuildFightSetup;
        // A cheer with no wait screen open would only start the viewer's cooldown for nothing.
        _fight = new FightService(_settings, _fightCatalog, _hub.PublishFightAssist, _hub.PublishFightPick) { IsShowing = () => _hub.FightOverlayCount > 0 };
        _hub.FightLineupReported += _fight.SetLineup;
        _spinWins = new SpinWinStore();
        // What makes a win-only pet usable by exactly its winner, wherever pets are asked for.
        _petService.OwnsWonPet = _spinWins.Owns;
        // The broadcaster is read at call time rather than captured: the app can be pointed at
        // another channel while it runs, and a refund aimed at the channel we have left is refused.
        _ledger = new RedemptionLedger(
            new TwitchRedemptionGateway(_apiClient, () => _hub.BroadcasterId),
            _petRegistry,
            _hub.PublishPetRemoved);
        _botAccount = new BotAccount(_httpClient);
        // After the bot account, because the spin has to be able to ask whether the bot can write.
        _spins = new SpinService(_settings, _petCatalog, _spinWins, _petService, _hub,
            login => _apiClient.GetUserByLoginAsync(login),
            overlayCount: null,
            // A duplicate can only become a gift if its winner will be told to claim it: the
            // message has to be switched on and the sending connection up. Otherwise the draw
            // dodges what the viewer already owns.
            canGift: () => _settings.Bot.Speaks(BotFlow.SpinDuplicate)
                && (_settings.Bot.Mode == BotMode.Bot ? _botAccount.CanSend : _chatClient.IsRunning));
        _spins.Start();
        // Which account carries the line is decided at send time rather than captured: the mode can
        // be changed while the app runs, and a queued line should go out as whoever the bot is now.
        _botSender = new BotSender(SendBotLineAsync, () => _settings.Bot.MessagesPer30Seconds);
        _bot = new BotService(_settings, _botSender, new BotContext(
            _tts.Snapshot,
            () => _settings.Bot.Mode == BotMode.Bot ? _botAccount.Login : _session.Login,
            // The spin's own commands – "!mina" and "!ge" – answer from the win store, which no
            // static command row could.
            _spins.HandleChatMessage));
        _dockContext = new DockServerContext
        {
            Settings = _settings,
            Hub = _hub,
            Session = _session,
            Api = _apiClient,
            Chat = _chatClient,
            Speech = _nameSpeech,
            Tts = _tts,
            TtsAudio = _ttsAudio,
            Pets = _petCatalog,
            Nicknames = _nicknames,
            Emotes = _emotes,
            Fight = _fightCatalog
        };
        _dockServer = new DockServer(_dockContext);
        _overlay = new OverlayWindow(_settings, _badgeCatalog, _nicknames);
        _edgeAlerts = new EdgeAlertWindow();
        _chatFlushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(75) };
        _chatFlushTimer.Tick += FlushPendingMessages;
        _settingsApplyTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _settingsApplyTimer.Tick += (_, _) => { _settingsApplyTimer.Stop(); _overlay.ApplySettings(); };
        _settingsSaveTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(450) };
        _settingsSaveTimer.Tick += (_, _) => { _settingsSaveTimer.Stop(); SaveSettingsNow(); };
        _trayIcon = CreateTrayIcon();
        _overlay.PlacementChanged += SaveSettings;
        // Written on a timer, not per message: chat arrives in bursts, and a file write per line
        // during a raid is disk work for no gain.
        _historySaveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
        _historySaveTimer.Tick += (_, _) => SaveChatHistory();
        _historySaveTimer.Start();
        PopulateControls();
        _loading = false;
        if (_settings.OverlayVisible) _overlay.Show();

        // The dock can put an earlier sitting's lines away. The overlay is showing the same ones and
        // the file on disk would hand them back at the next start, so both have to follow.
        _hub.HistoryTrimmed += () => RunOnUi(OnHistoryTrimmed);

        // One entry point rather than four handlers: the reward name has to be filled in before the
        // message reaches the views, and that only holds if enrichment happens ahead of the fan-out.
        _chatClient.MessageReceived += OnChatMessage;
        _chatClient.ModerationReceived += OnModeration;
        _chatClient.EventReceived += OnChatEvent;
        _eventSubClient.EventReceived += OnChatEvent;
        _eventSubClient.RedemptionReceived += OnRedemption;
        // The queue can also be worked from Twitch's own dashboard: a pet refunded there has to come
        // down here, and a reading refunded there must be neither read out nor answered. Readings
        // are asked first because they own their redemptions outright; the ledger never sees them.
        _eventSubClient.RedemptionUpdated += change =>
        {
            if (_tts.HandleExternalUpdate(change.RedemptionId, change.Status)) return;
            // A gift still waiting can be refunded from the dashboard too; the pending entry then
            // has to go, or the winner could give away a prize whose points are already back.
            _spins.HandleExternalUpdate(change.RedemptionId, change.Status);
            _ledger.HandleExternalUpdate(change.RedemptionId, change.Status);
        };
        // The two signals that say whether a pet is being seen at all: a lawn reporting what it has
        // drawn, and whether any lawn is connected in the first place.
        _hub.PetShown += _ledger.MarkShown;
        _hub.PetOverlayCountChanged += _ledger.OverlayCountChanged;
        // The bot holds the same signal against a grace period of its own: a scene change in OBS
        // drops every lawn for a second or two, which must not be announced to the channel.
        _hub.PetOverlayCountChanged += _bot.OnPetOverlayCountChanged;
        // A full lawn pushes the oldest pet home to fit a new one, and that pet may be one somebody
        // paid for. The chat route and the test button both do it; a refundable reward refuses
        // instead, so it is only ever on the receiving end.
        _petService.PetEvicted += _ledger.PetEvicted;
        _ledger.Answered += notice =>
        {
            // Twitch having taken the verdict is the only moment the spin can strike a booked win
            // off its list of debts: earlier the redemption is still open, and skipping it would
            // carry the debt for ever.
            if (notice.RedemptionId.Length > 0) _spins.Settled(notice.RedemptionId);
            RunOnUi(() => ShowRedemptionNotice(notice));
            // The same sentence, said in chat to the viewer it is about.
            _bot.OnRedemptionAnswered(notice);
        };
        // Nothing is connected yet, and the ledger has to start out knowing that rather than
        // assuming a lawn it has never seen.
        _ledger.OverlayCountChanged(_hub.PetOverlayCount);
        // The spin: the overlay's curtain call frees the queue, the verdicts ride the ledger's
        // retrying path, and the announcements go out through the bot.
        _hub.SpinShown += _spins.OnSpinShown;
        _spins.Verdict += verdict => _ = _ledger.AnswerNow(
            verdict.RedemptionId, verdict.RewardId, verdict.ViewerName, verdict.Cost,
            verdict.Refund ? RedemptionStatus.Canceled : RedemptionStatus.Fulfilled, verdict.Reason, "spin");
        _spins.Announced += _bot.Announce;
        _eventSubClient.GigantifyReceived += OnGigantify;
        _eventSubClient.CustomPowerUpReceived += OnCustomPowerUp;
        // The dock's approval bar reads the queue from the service rather than keeping a copy, so
        // there is one answer to "what is still waiting" and it is the one that gets acted on.
        _hub.TtsPending = _tts.Snapshot;
        _tts.Changed += () => _hub.PublishTts();
        // The browser source saying it has finished a clip is what releases the next one in the
        // queue – and, on the channel points route, the only evidence the reading was delivered.
        _hub.TtsPlaybackFinished += _browserTts.OnFinished;
        // A reading page that goes away mid-clip can no longer report, so the app settles the clip
        // on its behalf instead of holding the queue until the timeout.
        _hub.TtsOverlayCountChanged += _browserTts.OnOverlayCountChanged;
        // The sweep and the reading queue answer for the same reward, so the sweep has to be able to
        // tell "left over from before we were listening" from "on screen right now, being decided".
        // The spin holds redemptions too: a gift waiting for its winner is a prize already won, not
        // a leftover to pay back.
        _ledger.ClaimedElsewhere = id => _tts.Holds(id) || _spins.Holds(id);
        // Only the readings that can actually be paid back ever raise this; the bits route has
        // nobody to tell.
        _tts.Answered += (request, status, reason) => _ = AnswerTtsAsync(request, status, reason);
        _tts.Noticed += text => RunOnUi(() => TtsStatusText.Text = text);
        // Checked here rather than in RaiseEdgeAlert, the same way the chat triggers do it: the
        // settings window's test buttons preview a switched-off glow on purpose.
        _tts.ApprovalNeeded += request =>
        {
            if (_settings.EdgeAlerts.TtsAlert.Enabled) RaiseEdgeAlert(EdgeAlertKind.TtsRequest);
            _bot.OnReadingPending(request);
        };
        // Every ending, refundable or not; the bot works out what to say from the verdict.
        _tts.Finished += _bot.OnReadingFinished;
        _eventSubClient.HypeTrainChanged += OnHypeTrain;
        _eventSubClient.StatusChanged += status => RunOnUi(() => SetEventStatus(status));
        _eventSubClient.CoverageChanged += coverage => RunOnUi(() => ApplyEventCoverage(coverage));
        _chatClient.StatusChanged += status => RunOnUi(() => SetStatus(status));
        _chatClient.RoomDiscovered += room => RunOnUi(() => _ = PrepareRoomAsync(room));
        _chatClient.ConnectionStopped += () => RunOnUi(() => SetConnectionButtons(false));
        _session.StateChanged += () => RunOnUi(UpdateLoginUi);
        // A device flow finishing is what turns "logged out" into a connection, so the bot's login is
        // also the moment it can join the channel.
        _botAccount.StateChanged += () => RunOnUi(ApplyBotConfiguration);
        _botAccount.StatusChanged += status => RunOnUi(() => BotStatusText.Text = status);
        _botSender.Sent += line => RunOnUi(() => BotLastLineText.Text = "Senast skrivet: " + line);
        _botSender.Failed += reason => RunOnUi(() => BotStatusText.Text = "Boten kunde inte skriva: " + reason);

        UpdateLoginUi();
        ApplyBotConfiguration();
        UpdateLoginButtonState();
        ApplySpeechConfiguration();
        RestoreChatHistory();
        _ = StartDockServerAsync();
        // Held until the window is up, so the warning dialog has an owner to sit on top of.
        Loaded += (_, _) => ReportProfileState();
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    /// <summary>
    /// Puts back what was on screen last time, or shows the sample lines when there is nothing to
    /// put back. Limited to the lines this app saw itself, from this channel, within
    /// <see cref="ChatHistoryStore.MaxAge"/>; Twitch sends no history on join.
    ///
    /// <para>The restored lines go straight into the two views and never through
    /// <see cref="OnChatMessage"/>, which would spawn pets, read names aloud, welcome first-time
    /// chatters and light the edges all over again.</para>
    /// </summary>
    private void RestoreChatHistory()
    {
        IReadOnlyList<ChatTimelineItem> history = _historyStore.Load(_settings.Channel, DateTimeOffset.Now);
        if (history.Count == 0)
        {
            // Sample lines so the dock shows what the reading settings look like before anything is connected.
            _overlay.AddWelcomeMessages();
            _hub.ShowSamples();
            return;
        }

        _overlay.AddItems(history);
        _hub.ReplaceHistory(history);
        _lastSavedHistoryVersion = _hub.HistoryVersion;
        AppLog.Info($"Återställde {history.Count} rader från förra körningen i #{_settings.Channel}.");
    }

    /// <summary>
    /// Asks the recent-messages service for what was said before we joined, and weaves it into what
    /// is already on screen. Runs once per channel per run, and only while the setting is on.
    /// Like the saved history, nothing fetched here goes through <see cref="OnChatMessage"/>.
    /// </summary>
    private async Task BackfillRecentMessagesAsync(string channel)
    {
        if (!_settings.FetchRecentMessages || channel.Length == 0) return;
        if (string.Equals(_backfilledChannel, channel, StringComparison.OrdinalIgnoreCase)) return;
        // Marked before the await: a reconnect landing while this is in flight must not start a
        // second fetch of the same thing.
        _backfilledChannel = channel;

        IReadOnlyList<ChatTimelineItem> fetched;
        try
        {
            fetched = await _recentMessages.GetAsync(channel, ChatHistoryStore.MaxItems);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            // Somebody else's free service being unreachable is not this app's problem to report.
            AppLog.Warn($"Kunde inte hämta tidigare meddelanden för #{channel}: {ex.Message}");
            // Nothing was drawn, so this channel is not done – a reconnect may as well try again.
            // Only if the marker is still ours: a channel switch during the request has already
            // handed it to the new room, and clearing it there would fetch that room twice.
            if (string.Equals(_backfilledChannel, channel, StringComparison.OrdinalIgnoreCase)) _backfilledChannel = null;
            return;
        }
        // The request outlived the channel it was about: its lines would be drawn, and saved to the
        // history file, under the new channel's name.
        if (!string.Equals(_settings.Channel, channel, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Info($"Hoppade över {fetched.Count} hämtade rader för #{channel} – kanalen är nu #{_settings.Channel}.");
            return;
        }
        if (fetched.Count == 0) return;

        IReadOnlyList<ChatTimelineItem> merged = ChatHistoryMerge.Combine(
            _hub.SnapshotHistory(), fetched, ChatHistoryStore.MaxItems, ChatHistoryStore.MaxAge, DateTimeOffset.Now);
        // The fetch took long enough for live lines to have arrived, and some of them are still
        // waiting to be drawn on the overlay while already being part of what is drawn here.
        DropPendingAlreadyDrawn(merged);
        _overlay.ReplaceItems(merged);
        _hub.ReplaceHistory(merged);
        AppLog.Info($"Hämtade {fetched.Count} tidigare rader för #{channel}; chatten visar nu {merged.Count}.");
    }

    /// <summary>
    /// A message was deleted, somebody was timed out or banned, or the room was cleared – the
    /// broadcaster's own /clear arrives as a CLEARCHAT too. Removes the lines from the hub (the
    /// dock and the stream overlay), from the overlay window's own cards, and from the saved history.
    /// </summary>
    private void OnModeration(ChatModerationEvent moderation)
    {
        _hub.PublishModeration(moderation);
        RunOnUi(() =>
        {
            // A line the action reached may still be queued for a card, and a single flush takes
            // only fifty at a time, so the whole queue is drawn before the moderation is applied.
            DrainPendingMessages();
            _overlay.ApplyModeration(moderation);
            // Saved at once: a restart before the next timer tick would bring a cleared room back.
            SaveChatHistory();
        });
    }

    /// <summary>
    /// An earlier sitting was put away from the dock. Redraws the overlay, which keeps its own
    /// cards, from what is left, and saves the history at once so a restart cannot bring it back.
    /// </summary>
    private void OnHistoryTrimmed()
    {
        IReadOnlyList<ChatTimelineItem> remaining = _hub.SnapshotHistory();
        DropPendingAlreadyDrawn(remaining);
        _overlay.ReplaceItems(remaining);
        AppLog.Info($"Tidigare pass dolt; chatten visar nu {remaining.Count} rader.");
        SaveChatHistory();
    }

    /// <summary>
    /// Removes from the overlay's pending queue the lines a redraw is about to draw anyway, so
    /// they do not get a second card on the next flush.
    /// </summary>
    private void DropPendingAlreadyDrawn(IReadOnlyList<ChatTimelineItem> drawn)
    {
        // Matched on identity, not emptied wholesale: a hype train card is queued for the overlay
        // alone and is in no timeline. Safe without a lock, because every line in the timeline was
        // queued for the overlay before it was published to the hub.
        HashSet<string> ids = new(drawn.Select(ChatHistoryMerge.IdOf).OfType<string>(), StringComparer.Ordinal);
        List<ChatTimelineItem> keep = [];
        while (_pendingMessages.TryDequeue(out ChatTimelineItem item))
        {
            Interlocked.Decrement(ref _pendingMessageCount);
            if (ChatHistoryMerge.IdOf(item) is { } id && ids.Contains(id)) continue;
            keep.Add(item);
        }
        // Back on in the order they arrived; Queue also keeps the flush timer running.
        foreach (ChatTimelineItem item in keep) Queue(item);
    }

    /// <summary>
    /// Writes the history to disk when it has changed since the last save, so a quiet chat does
    /// not rewrite the same file every twenty seconds.
    /// </summary>
    private void SaveChatHistory()
    {
        if (_settings.Channel.Length == 0) return;
        long version = _hub.HistoryVersion;
        if (version == _lastSavedHistoryVersion) return;
        // Only a write that happened counts as saved: otherwise a file locked for a second (a backup
        // tool, a virus scanner) would leave the history unwritten until the next line arrives.
        if (!_historyStore.Save(_settings.Channel, _hub.SnapshotHistory(), DateTimeOffset.Now))
        {
            if (!_historySaveFailing) AppLog.Warn("Chatthistoriken kunde inte skrivas till disk. Försöker igen.");
            _historySaveFailing = true;
            return;
        }
        if (_historySaveFailing) AppLog.Info("Chatthistoriken kunde skrivas igen.");
        _historySaveFailing = false;
        _lastSavedHistoryVersion = version;
    }

    private async Task StartDockServerAsync()
    {
        if (!_settings.DockServerEnabled)
        {
            SetDockStatus("Chattservern är avstängd.", "idle");
            return;
        }

        bool started = await _dockServer.StartAsync();
        DockUrlBox.Text = started ? _dockServer.DockUrl : string.Empty;
        CopyDockUrlButton.IsEnabled = started;
        OpenDockButton.IsEnabled = started;
        PetsUrlBox.Text = started ? _dockServer.PetsUrl : string.Empty;
        CopyPetsUrlButton.IsEnabled = started;
        OpenPetsButton.IsEnabled = started;
        StreamUrlBox.Text = started ? _dockServer.StreamUrl : string.Empty;
        CopyStreamUrlButton.IsEnabled = started;
        OpenStreamButton.IsEnabled = started;
        TtsUrlBox.Text = started ? _dockServer.TtsUrl : string.Empty;
        CopyTtsUrlButton.IsEnabled = started;
        FightUrlBox.Text = started ? _dockServer.FightUrl : string.Empty;
        CopyFightUrlButton.IsEnabled = started;
        OpenFightButton.IsEnabled = started;
        SetDockStatus(started ? "Servern kör – klistra in adressen i OBS." : _dockServer.LastError ?? "Servern kunde inte starta.", started ? "live" : "error");
    }

    private void SetDockStatus(string text, string state)
    {
        DockStatusText.Text = text;
        DockStatusDot.Fill = new SolidColorBrush(state switch
        {
            "live" => Color.FromRgb(34, 197, 94),
            "error" => Color.FromRgb(239, 68, 68),
            _ => Color.FromRgb(107, 114, 128)
        });
    }

    private void CopyDockUrl_Click(object sender, RoutedEventArgs e)
    {
        if (DockUrlBox.Text.Length == 0) return;
        try
        {
            Clipboard.SetText(DockUrlBox.Text);
            SetDockStatus("Adressen är kopierad – klistra in den i OBS.", "live");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard can be locked by another process; the address is still selectable.
            SetDockStatus("Kunde inte kopiera – markera adressen och kopiera manuellt.", "error");
        }
    }

    private void OpenDock_Click(object sender, RoutedEventArgs e)
    {
        if (DockUrlBox.Text.Length == 0) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DockUrlBox.Text) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetDockStatus("Ingen webbläsare kunde öppnas.", "error");
        }
    }

    private void CopyPetsUrl_Click(object sender, RoutedEventArgs e)
    {
        if (PetsUrlBox.Text.Length == 0) return;
        try { Clipboard.SetText(PetsUrlBox.Text); }
        catch (System.Runtime.InteropServices.COMException) { /* the address is still selectable */ }
    }

    private void OpenPets_Click(object sender, RoutedEventArgs e)
    {
        if (PetsUrlBox.Text.Length == 0) return;
        OpenInBrowser(PetsUrlBox.Text);
    }

    private void CopyStreamUrl_Click(object sender, RoutedEventArgs e)
    {
        if (StreamUrlBox.Text.Length == 0) return;
        try { Clipboard.SetText(StreamUrlBox.Text); }
        catch (System.Runtime.InteropServices.COMException) { /* the address is still selectable */ }
    }

    private void OpenStream_Click(object sender, RoutedEventArgs e)
    {
        if (StreamUrlBox.Text.Length == 0) return;
        OpenInBrowser(StreamUrlBox.Text);
    }

    private void PetSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool wasEnabled = _settings.Pets.Enabled;
        _settings.Pets.Enabled = PetsEnabledCheck.IsChecked == true;
        _settings.Pets.ShowNames = PetNamesCheck.IsChecked == true;
        _settings.Pets.RarityEffects = PetRarityFxCheck.IsChecked == true;
        _settings.Pets.Scale = PetScaleSlider.Value;
        PetScaleValue.Text = $"{PetScaleSlider.Value:P0}";
        _settings.Pets.AnimationSpeed = PetAnimSpeedSlider.Value;
        PetAnimSpeedValue.Text = $"{PetAnimSpeedSlider.Value:P0}";
        _hub.PublishPetSettings();
        // Switching pets off hides the whole lawn while the pets on it live out their time unseen,
        // so anyone who paid for one gets their points back.
        if (wasEnabled && !_settings.Pets.Enabled) _ledger.RefundAll("pets stängdes av");
        SaveSettings();
    }

    private void PetLifetime_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!int.TryParse(PetLifetimeInput.Text, out int minutes) || minutes is < 1 or > 60) return;
        _settings.Pets.LifetimeMinutes = minutes;
        _hub.PublishPetSettings();
        SaveSettings();
    }

    private void PetLifetime_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!int.TryParse(PetLifetimeInput.Text, out int minutes) || minutes is < 1 or > 60)
            PetLifetimeInput.Text = _settings.Pets.LifetimeMinutes.ToString();
    }

    private void PetRarityFade_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!int.TryParse(PetRarityFadeInput.Text, out int seconds) || seconds is < 0 or > 300) return;
        _settings.Pets.RarityFadeSeconds = seconds;
        _hub.PublishPetSettings();
        SaveSettings();
    }

    private void PetRarityFade_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!int.TryParse(PetRarityFadeInput.Text, out int seconds) || seconds is < 0 or > 300)
            PetRarityFadeInput.Text = _settings.Pets.RarityFadeSeconds.ToString();
    }

    private void PetMax_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!int.TryParse(PetMaxInput.Text, out int max) || max is < 1 or > 20) return;
        _settings.Pets.MaxPets = max;
        _hub.PublishPetSettings();
        SaveSettings();
    }

    private void PetMax_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!int.TryParse(PetMaxInput.Text, out int max) || max is < 1 or > 20)
            PetMaxInput.Text = _settings.Pets.MaxPets.ToString();
    }

    private void PetReward_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        SavePetRewards();
    }

    /// <summary>Puts back the last accepted value when the box is left holding something unusable.</summary>
    private void PetRewardMinutes_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not PetRewardRule rule) return;
        if (!int.TryParse(box.Text, out int minutes) || minutes is < 1 or > 60)
            box.Text = rule.Minutes.ToString();
    }

    private void PetRewardCost_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not PetRewardRule rule) return;
        if (!int.TryParse(box.Text, out int cost) || cost < 1) box.Text = rule.Cost.ToString();
    }

    /// <summary>
    /// Creates the row's reward on Twitch, which is what makes its redemptions refundable: Twitch
    /// lets an app answer a redemption only on a reward its own client id made, and there is no
    /// API for adopting a reward set up by hand in the dashboard.
    /// </summary>
    private async void CreatePetReward_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PetRewardRule rule) return;

        if (rule.Managed)
        {
            PetRewardStatusText.Text = $"“{rule.Label}” är redan skapad av appen.";
            return;
        }
        if (!_session.IsLoggedIn)
        {
            PetRewardStatusText.Text = "Logga in på Twitch först.";
            return;
        }
        if (!_session.HasScope(TwitchAuth.ManageRedemptionsScope))
        {
            PetRewardStatusText.Text = "Din inloggning är från innan återbetalning fanns – logga ut och in igen så du kan godkänna behörigheten.";
            return;
        }
        if (_hub.BroadcasterId.Length == 0 || !string.Equals(_hub.BroadcasterId, _session.UserId, StringComparison.Ordinal))
        {
            PetRewardStatusText.Text = "Anslut till din egen kanal först – belöningar kan bara skapas där.";
            return;
        }
        if (rule.Label.Trim().Length == 0)
        {
            PetRewardStatusText.Text = "Ge raden ett namn först; det blir belöningens namn i Twitch.";
            return;
        }

        (sender as Button)!.IsEnabled = false;
        PetRewardStatusText.Text = $"Skapar “{rule.Label}” i Twitch …";
        try
        {
            CustomReward created = await _apiClient.CreateCustomRewardAsync(_hub.BroadcasterId, new NewCustomReward(
                rule.Label.Trim(),
                rule.Cost,
                $"Skriv namnet på den pet du vill ha, t.ex. boo. Peten syns i {rule.Minutes} minuter.",
                RequireInput: true,
                CooldownSeconds: 0,
                BackgroundColor: null));

            rule.RewardId = created.Id;
            rule.RewardName = created.Title;
            rule.Cost = created.Cost;
            rule.Managed = true;
            _rewards.Remember(created);
            SavePetRewards();
            // The rows read their state once when drawn, so the list is rebuilt to show the lock.
            RefreshPetRewardList();
            PetRewardStatusText.Text = $"“{created.Title}” skapad. Sätt bilden i Twitchs dashboard – den går inte att ladda upp via API:t.";
        }
        catch (Exception ex) when (ex is TwitchApiException or TwitchAuthException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            // Twitch refuses a second reward with a title the channel already uses, which is what a
            // streamer moving off a hand-made reward hits.
            PetRewardStatusText.Text = $"Kunde inte skapa belöningen: {ex.Message} Har du redan en belöning med samma namn? Döp om eller ta bort den först.";
            AppLog.Warn($"Pets: kunde inte skapa belöning “{rule.Label}”: {ex.Message}");
        }
        finally
        {
            if (sender is Button button) button.IsEnabled = true;
        }
    }

    private void AddPetReward_Click(object sender, RoutedEventArgs e) =>
        AddPetReward(new PetRewardRule { Minutes = _settings.Pets.LifetimeMinutes });

    private void RemovePetReward_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PetRewardRule rule) return;
        _petRewards.Remove(rule);
        SavePetRewards();
    }

    /// <summary>
    /// Adds the reward that was redeemed last, so the streamer can point a row at a reward by
    /// redeeming it once instead of digging a GUID out of Twitch's dashboard.
    /// </summary>
    private void UseLastReward_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSeenRewardId is not { Length: > 0 } rewardId) return;
        string shown = _lastSeenRewardName ?? rewardId;
        if (_petRewards.Any(rule => rule.Matches(rewardId, _lastSeenRewardName)))
        {
            LastRewardText.Text = $"{shown} finns redan i listan.";
            return;
        }
        // Both are written down: the name is what the streamer reads, the id is what still matches
        // in a channel where the names are not readable.
        AddPetReward(new PetRewardRule
        {
            Label = _lastSeenRewardName ?? string.Empty,
            RewardId = rewardId,
            RewardName = _lastSeenRewardName ?? string.Empty,
            Minutes = _settings.Pets.LifetimeMinutes
        });
    }

    private void AddPetReward(PetRewardRule rule)
    {
        _petRewards.Add(rule);
        SavePetRewards();
    }

    private void SavePetRewards()
    {
        _settings.Pets.Rewards = _petRewards.ToList();
        ShowPetRewardTable();
        SaveSettings();
    }

    /// <summary>Shows the column headings when there are rows, and the empty-list line when there are none.</summary>
    private void ShowPetRewardTable()
    {
        bool empty = _petRewards.Count == 0;
        PetRewardHeader.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        PetRewardEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Draws the rows again. The rules have no change notification, so a row becoming refundable
    /// needs the list rebuilt to show up.
    /// </summary>
    private void RefreshPetRewardList()
    {
        PetRewardRule[] rules = _petRewards.ToArray();
        _petRewards.Clear();
        foreach (PetRewardRule rule in rules) _petRewards.Add(rule);
    }

    private void SpawnTestPet_Click(object sender, RoutedEventArgs e) => _petService.SpawnTest();

    private void PetDefault_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _refreshingPetCatalog) return;
        _settings.Pets.DefaultPet = (PetDefaultBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        SaveSettings();
    }

    private void OpenPetsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_petCatalog.PetsFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_petCatalog.PetsFolder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            PetListText.Text = $"Kunde inte öppna mappen: {_petCatalog.PetsFolder}";
        }
    }

    /// <summary>
    /// Opens the profile folder in Utforskaren. Every backup copy there is a plain file that can
    /// be copied over a broken one with the app closed.
    /// </summary>
    private void OpenProfileFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ProfilePaths.Root);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ProfilePaths.Root) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            SetStatus($"Kunde inte öppna mappen: {ProfilePaths.Root}", true);
        }
    }

    private void ReloadPets_Click(object sender, RoutedEventArgs e) => ReloadPets();

    private void ReloadPets()
    {
        _petCatalog.Reload();
        OnPetsEdited(source: null);
    }

    /// <summary>
    /// Everything that has to follow a rewritten pet.json or a reloaded folder: the settings saved
    /// (a rename can move the default pet), the overlays told, and every pet list in the app read
    /// again – except the one in <paramref name="source"/>, which made the change and already shows it.
    /// </summary>
    private void OnPetsEdited(Window? source)
    {
        SaveSettings();
        // Overlays that are open right now learn the change without a reload in OBS.
        _hub.PublishPetCatalog();
        RefreshPetCatalogUi();
        if (_petsWindow is not null && _petsWindow != source) _petsWindow.RefreshPets();
        if (_spinSettingsWindow is not null && _spinSettingsWindow != source) _spinSettingsWindow.RefreshPets();
    }

    private void OpenPetsWindow_Click(object sender, RoutedEventArgs e) => OpenPetsWindow();

    private void OpenPetsWindow()
    {
        if (_petsWindow is { IsLoaded: true })
        {
            _petsWindow.Activate();
            return;
        }

        _petsWindow = new PetsWindow(
            _settings,
            _petCatalog,
            _spinWins,
            () => OnPetsEdited(_petsWindow),
            ReloadPets,
            // Null while the server is down: both pages are served by it.
            petId => _dockServer.IsRunning ? _dockServer.InspectUrl(petId) : null,
            petId => _dockServer.IsRunning ? _dockServer.PreviewUrl(petId) : null) { Owner = this };
        _petsWindow.Closed += (_, _) => _petsWindow = null;
        _petsWindow.Show();
    }

    /// <summary>Rebuilds the species summary and the default-pet picker from the catalog.</summary>
    private void RefreshPetCatalogUi()
    {
        _refreshingPetCatalog = true;
        try
        {
            IReadOnlyList<PetDefinition> all = _petCatalog.Pets;
            int winOnly = all.Count(pet => pet.WinOnly);
            string summary = winOnly == 0
                ? $"{all.Count} pets i mappen."
                : $"{all.Count} pets i mappen, varav {winOnly} bara går att vinna i lyckosnurren.";
            IEnumerable<string> warnings = _petCatalog.Warnings.Select(w => $"⚠ {w}");
            PetListText.Text = string.Join("\n", warnings.Prepend(summary));

            PetDefaultBox.Items.Clear();
            PetDefaultBox.Items.Add(new ComboBoxItem { Content = "Slumpad – låt texten avgöra", Tag = "" });
            foreach (PetDefinition pet in _petCatalog.Pets)
                PetDefaultBox.Items.Add(new ComboBoxItem { Content = pet.Name, Tag = pet.Id });

            string current = _settings.Pets.DefaultPet;
            PetDefaultBox.SelectedIndex = 0;
            foreach (ComboBoxItem item in PetDefaultBox.Items)
                if (string.Equals(item.Tag as string, current, StringComparison.OrdinalIgnoreCase) && current.Length > 0)
                    PetDefaultBox.SelectedItem = item;

            UpdateSpinSummary();
        }
        finally { _refreshingPetCatalog = false; }
    }

    /// <summary>
    /// Remembers the latest redeemed reward id so the user can pick it from the UI instead of
    /// digging a GUID out of Twitch's dashboard.
    /// </summary>
    private void TrackLastReward(ChatMessage message)
    {
        if (message.RewardId is not { Length: > 0 } rewardId) return;
        RunOnUi(() => ShowLastReward(rewardId, message.RewardTitle, message.DisplayName));
    }

    /// <summary>Remembers the last redeemed reward and shows it, by name when there is one.</summary>
    private void ShowLastReward(string rewardId, string? rewardTitle, string displayName)
    {
        _lastSeenRewardId = rewardId;
        _lastSeenRewardName = string.IsNullOrWhiteSpace(rewardTitle) ? null : rewardTitle.Trim();
        LastRewardText.Text = $"Senast inlösta: {_lastSeenRewardName ?? rewardId} ({displayName})";
        UseLastRewardButton.IsEnabled = true;
    }

    private void ClientId_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        UpdateLoginButtonState();
    }

    private void UpdateLoginButtonState() =>
        LoginButton.IsEnabled = _session.IsLoggedIn || ClientIdBox.Text.Trim().Length > 0;

    private void OpenDevConsole_Click(object sender, RoutedEventArgs e) =>
        OpenInBrowser("https://dev.twitch.tv/console/apps");

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_session.IsLoggedIn)
        {
            await _session.LogoutAsync();
            _hub.PublishAuth(_chatClient.CanSend);
            return;
        }

        string clientId = ClientIdBox.Text.Trim();
        if (clientId.Length == 0)
        {
            LoginStatusText.Text = "Fyll i Client ID först – se stegen ovan.";
            ClientIdBox.Focus();
            return;
        }

        _settings.ClientId = clientId;
        SaveSettings();
        LoginButton.IsEnabled = false;
        LoginStatusText.Text = "Kontaktar Twitch …";
        try
        {
            DeviceCodePrompt prompt = await _session.BeginLoginAsync(clientId);
            DeviceCodeCard.Visibility = Visibility.Visible;
            DeviceCodeHint.Text = $"Gå till {prompt.VerificationUri} och skriv in koden:";
            DeviceCodeText.Text = prompt.UserCode;
            LoginStatusText.Text = "Väntar på att du godkänner i webbläsaren …";
            OpenInBrowser(prompt.VerificationUri);
            _hub.PublishAuth(_chatClient.CanSend);
        }
        catch (Exception ex) when (ex is TwitchAuthException or System.Net.Http.HttpRequestException)
        {
            DeviceCodeCard.Visibility = Visibility.Collapsed;
            LoginStatusText.Text = ex.Message;
        }
        finally { UpdateLoginButtonState(); }
    }

    private void DockAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (_dockSettingsWindow is { IsLoaded: true })
        {
            _dockSettingsWindow.Activate();
            return;
        }

        _dockSettingsWindow = new DockSettingsWindow(_settings, () =>
        {
            SaveSettings();
            _hub.PublishSettings();
        }) { Owner = this };
        _dockSettingsWindow.Closed += (_, _) => _dockSettingsWindow = null;
        _dockSettingsWindow.Show();
    }

    private void StreamAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (_streamSettingsWindow is { IsLoaded: true })
        {
            _streamSettingsWindow.Activate();
            return;
        }

        _streamSettingsWindow = new StreamSettingsWindow(_settings, () =>
        {
            SaveSettings();
            _hub.PublishStreamSettings();
        }) { Owner = this };
        _streamSettingsWindow.Closed += (_, _) => _streamSettingsWindow = null;
        _streamSettingsWindow.Show();
    }

    private void SpeechSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_speechSettingsWindow is { IsLoaded: true })
        {
            _speechSettingsWindow.Activate();
            return;
        }

        _speechSettingsWindow = new SpeechSettingsWindow(_settings, _speechSecrets, _nameSpeech, () =>
        {
            SaveSettings();
            ApplySpeechConfiguration();
        }) { Owner = this };
        _speechSettingsWindow.Closed += (_, _) => _speechSettingsWindow = null;
        _speechSettingsWindow.Show();
    }

    // Bot

    /// <summary>
    /// Sends a bot line as whichever account the mode names at send time, so a line still in the
    /// queue follows a mode change. Does nothing while the bot is off.
    /// </summary>
    private Task SendBotLineAsync(string text, CancellationToken cancellationToken) => _settings.Bot.Mode switch
    {
        BotMode.Bot => _botAccount.SendAsync(text, cancellationToken),
        BotMode.Streamer => _chatClient.SendMessageAsync(text, cancellationToken),
        _ => Task.CompletedTask
    };

    /// <summary>
    /// Brings the bot's connection in line with the settings and with whether the app is in a channel
    /// at all. Safe to call on any change.
    /// </summary>
    private void ApplyBotConfiguration()
    {
        BotSettings bot = _settings.Bot;
        if (!bot.IsActive || !_chatClient.IsRunning)
        {
            // Lines queued under the old mode or channel would otherwise go out as the new
            // account, minutes later.
            _bot.Reset();
        }
        else
        {
            // Told the current count directly: with no pet overlay running there is no change
            // event coming, and the bot's overlay-down clock would never start.
            _bot.OnPetOverlayCountChanged(_hub.PetOverlayCount);
        }
        _ = _botAccount.ApplyAsync(_settings.Channel, bot.Mode == BotMode.Bot && _chatClient.IsRunning);
        UpdateBotUi();
    }

    private void UpdateBotUi()
    {
        BotSettings bot = _settings.Bot;
        SessionState state = _botAccount.Snapshot();

        BotLoginButton.Content = state.IsLoggedIn ? "Logga ut boten" : "Logga in som bot";
        BotDeviceCard.Visibility = state.PendingUserCode is null ? Visibility.Collapsed : Visibility.Visible;
        if (state.PendingUserCode is not null) BotDeviceText.Text = state.PendingUserCode;

        BotStatusText.Text = state.Error ?? bot.Mode switch
        {
            BotMode.Off => "Avstängd – ingenting skrivs i chatten.",
            BotMode.Streamer => "Skriver som ditt eget konto över anslutningen appen redan har.",
            _ when !state.IsLoggedIn => "Inget botkonto inloggat – välj Logga in som bot, eller byt till ditt eget konto.",
            _ when _botAccount.CanSend => $"Inloggad som {state.Login} och ansluten till #{_settings.Channel}.",
            _ when !_chatClient.IsRunning => $"Inloggad som {state.Login}. Boten ansluter när du kopplar upp chatten.",
            _ => $"Inloggad som {state.Login} – ansluter …"
        };

        int on = bot.Messages.Count(rule => rule.Enabled);
        int commands = bot.Commands.Count(command => command.IsUsable);
        string counted = commands == 0
            ? $"{on} av {bot.Messages.Count} meddelanden är påslagna, inga egna kommandon."
            : $"{on} av {bot.Messages.Count} meddelanden är påslagna, och {commands} {(commands == 1 ? "eget kommando" : "egna kommandon")}.";
        BotMessageCountText.Text = bot.IsActive ? counted : counted[..^1] + " – men boten är avstängd.";
    }

    private void PopulateBotControls()
    {
        BotSettings bot = _settings.Bot;
        BotModeOffRadio.IsChecked = bot.Mode == BotMode.Off;
        BotModeBotRadio.IsChecked = bot.Mode == BotMode.Bot;
        BotModeStreamerRadio.IsChecked = bot.Mode == BotMode.Streamer;
        BotStreamerNameBox.Text = bot.StreamerName;
        BotPetWordBox.Text = bot.PetWord;
        BotPetPluralBox.Text = bot.PetWordPlural;
        BotPetDefiniteBox.Text = bot.PetWordDefinite;
        BotWaitSecondsBox.Text = bot.TtsWaitingSeconds.ToString();
        BotRateBox.Text = bot.MessagesPer30Seconds.ToString();
        BotBatchBox.Text = bot.RefundBatchThreshold.ToString();
        BotHideOwnCheck.IsChecked = bot.HideOwnMessagesInOverlay;
        BotIgnoreOwnCheck.IsChecked = bot.IgnoreOwnMessages;
    }

    private void BotMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Bot.Mode = BotModeBotRadio.IsChecked == true ? BotMode.Bot
            : BotModeStreamerRadio.IsChecked == true ? BotMode.Streamer
            : BotMode.Off;
        SaveSettings();
        ApplyBotConfiguration();
    }

    private void BotSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        BotSettings bot = _settings.Bot;
        bot.StreamerName = BotStreamerNameBox.Text;
        bot.PetWord = BotPetWordBox.Text;
        bot.PetWordPlural = BotPetPluralBox.Text;
        bot.PetWordDefinite = BotPetDefiniteBox.Text;
        bot.HideOwnMessagesInOverlay = BotHideOwnCheck.IsChecked == true;
        bot.IgnoreOwnMessages = BotIgnoreOwnCheck.IsChecked == true;
        // The three numbers are only taken when they parse. Normalize would otherwise turn a box the
        // user has emptied on the way to typing "120" into the clamped minimum under their cursor.
        if (int.TryParse(BotWaitSecondsBox.Text, out int wait)) bot.TtsWaitingSeconds = wait;
        if (int.TryParse(BotRateBox.Text, out int rate)) bot.MessagesPer30Seconds = rate;
        if (int.TryParse(BotBatchBox.Text, out int batch)) bot.RefundBatchThreshold = batch;
        SaveSettings();
        UpdateBotUi();
    }

    /// <summary>Writes the clamped value back once the box is done being typed in.</summary>
    private void BotNumber_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _settings.Bot.Normalize();
        bool loading = _loading;
        _loading = true;
        BotWaitSecondsBox.Text = _settings.Bot.TtsWaitingSeconds.ToString();
        BotRateBox.Text = _settings.Bot.MessagesPer30Seconds.ToString();
        BotBatchBox.Text = _settings.Bot.RefundBatchThreshold.ToString();
        _loading = loading;
        SaveSettings();
    }

    private void BotUseChannelName_Click(object sender, RoutedEventArgs e)
    {
        string channel = _settings.Channel.Trim();
        if (channel.Length == 0) { BotStatusText.Text = "Fyll i kanalen under Anslutning först."; return; }
        BotStreamerNameBox.Text = channel;
    }

    private async void BotLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_botAccount.IsLoggedIn)
        {
            await _botAccount.LogoutAsync();
            ApplyBotConfiguration();
            return;
        }

        string clientId = ClientIdBox.Text.Trim();
        if (clientId.Length == 0)
        {
            BotStatusText.Text = "Fyll i Client ID under Anslutning först – boten använder samma app.";
            return;
        }

        BotLoginButton.IsEnabled = false;
        BotStatusText.Text = "Kontaktar Twitch …";
        try
        {
            DeviceCodePrompt prompt = await _botAccount.BeginLoginAsync(clientId);
            BotDeviceCard.Visibility = Visibility.Visible;
            BotDeviceHint.Text = $"Gå till {prompt.VerificationUri} – inloggad som boten, inte som du själv – och skriv in koden:";
            BotDeviceText.Text = prompt.UserCode;
            BotStatusText.Text = "Väntar på att koden godkänns i webbläsaren …";
            OpenInBrowser(prompt.VerificationUri);
        }
        catch (Exception ex) when (ex is TwitchAuthException or System.Net.Http.HttpRequestException)
        {
            BotDeviceCard.Visibility = Visibility.Collapsed;
            BotStatusText.Text = ex.Message;
        }
        finally { BotLoginButton.IsEnabled = true; }
    }

    private void OpenSpinSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_spinSettingsWindow is { IsLoaded: true })
        {
            _spinSettingsWindow.Activate();
            return;
        }

        _spinSettingsWindow = new SpinSettingsWindow(
            _settings,
            _petCatalog,
            _spinWins,
            () =>
            {
                SaveSettings();
                UpdateSpinSummary();
            },
            // The reel draws from the catalog the overlay holds, so a pet that just became winnable
            // has to reach it before the next spin rather than at the next reload.
            () => OnPetsEdited(_spinSettingsWindow),
            _spins.SpinTest,
            CreateSpinRewardAsync,
            OpenPetsWindow) { Owner = this };
        _spinSettingsWindow.Closed += (_, _) => _spinSettingsWindow = null;
        _spinSettingsWindow.Show();
    }

    /// <summary>
    /// Creates the spin's reward in Twitch and returns its id. Like a pet reward it needs a login
    /// with the redemption scope in our own channel, and throws
    /// <see cref="InvalidOperationException"/> naming whichever is missing.
    /// </summary>
    private async Task<string> CreateSpinRewardAsync(string title, int cost)
    {
        if (!_session.IsLoggedIn) throw new InvalidOperationException("Logga in på Twitch först.");
        if (!_session.HasScope(TwitchAuth.ManageRedemptionsScope))
            throw new InvalidOperationException("Din inloggning är från innan återbetalning fanns – logga ut och in igen så du kan godkänna behörigheten.");
        if (_hub.BroadcasterId.Length == 0 || !string.Equals(_hub.BroadcasterId, _session.UserId, StringComparison.Ordinal))
            throw new InvalidOperationException("Anslut till din egen kanal först – belöningar kan bara skapas där.");

        CustomReward created = await _apiClient.CreateCustomRewardAsync(_hub.BroadcasterId, new NewCustomReward(
            title,
            cost,
            "Snurra fram en pet som blir din för gott!",
            // Nothing the viewer could type would change the draw, and a reward that demands a
            // message would only leave them guessing at what to write.
            RequireInput: false,
            CooldownSeconds: 0,
            BackgroundColor: null));

        _rewards.Remember(created);
        _settings.Spin.RewardName = created.Title;
        _settings.Spin.Cost = created.Cost;
        return created.Id;
    }

    /// <summary>The one line about the spin on the pets tab, so its state is readable without opening it.</summary>
    private void UpdateSpinSummary()
    {
        SpinSettings spin = _settings.Spin;
        int winnable = _petCatalog.Winnable.Count;
        SpinSummaryText.Text = !spin.Enabled
            ? "Avstängd."
            : winnable == 0
                ? "Ingen pet är markerad som vinstbar än, så snurren har inget att dela ut."
                : spin.RewardId.Length == 0 && spin.RewardName.Length == 0
                    ? $"{winnable} pets går att vinna, men ingen belöning är vald än."
                    : $"{winnable} pets går att vinna. {(spin.CanRefund ? "🔒 Belöningen kan återbetalas." : "— Belöningen kan inte återbetalas.")}";
    }

    private void BotMessages_Click(object sender, RoutedEventArgs e)
    {
        if (_botSettingsWindow is { IsLoaded: true })
        {
            _botSettingsWindow.Activate();
            return;
        }

        _botSettingsWindow = new BotSettingsWindow(_settings, line => _botSender.Enqueue(line), () =>
        {
            SaveSettings();
            UpdateBotUi();
        }) { Owner = this };
        _botSettingsWindow.Closed += (_, _) => _botSettingsWindow = null;
        _botSettingsWindow.Show();
    }

    /// <summary>Keeps the dock's speaker button in step with what is actually configured.</summary>
    private void ApplySpeechConfiguration()
    {
        _hub.SpeechEnabled = _nameSpeech.IsConfigured;
        _hub.PublishSpeech();
        SpeechStatusText.Text = _hub.SpeechEnabled
            ? $"Påslaget – högtalarknappen visas i docken. Röst: {(_settings.Speech.VoiceName.Length > 0 ? _settings.Speech.VoiceName : _settings.Speech.VoiceId)}."
            : _nameSpeech.CanSpeak
                ? "Nycklar och röst är klara, men knappen är avstängd."
                : "Inte konfigurerat – ingen knapp visas i docken.";
    }

    private void SupportLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        OpenInBrowser(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void OpenInBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Not being able to launch a browser is not worth blocking on; the address is on screen.
        }
    }

    private void UpdateLoginUi()
    {
        SessionState state = _session.Snapshot();
        LoginButton.Content = state.IsLoggedIn ? "Logga ut" : "Logga in med Twitch";
        DeviceCodeCard.Visibility = state.PendingUserCode is null ? Visibility.Collapsed : Visibility.Visible;
        if (state.PendingUserCode is not null) DeviceCodeText.Text = state.PendingUserCode;

        LoginStatusText.Text = state.Error
            ?? (state.IsLoggedIn
                ? $"Inloggad som {state.Login}. Timeout, ban och raid är upplåsta i docken."
                : "Inte inloggad. Chatten visas men går inte att moderera.");

        if (state.IsLoggedIn && !string.Equals(_settings.UserName, state.Login, StringComparison.OrdinalIgnoreCase))
        {
            _settings.UserName = state.Login;
            SaveSettings();
            ScheduleOverlayApply();
        }

        _hub.PublishAuth(_chatClient.CanSend);

        if (!_chatClient.IsRunning || _reconnecting) return;

        // Logging in mid-session leaves an anonymous IRC connection behind, which cannot send.
        // Reconnecting upgrades it so the dock's composer works without the user restarting anything.
        if (state.IsLoggedIn && !_chatClient.CanSend) _ = ReconnectAsync(authenticated: true);

        // Logging out has to take the socket with it, otherwise the connection keeps sending as the
        // account that just signed out. Reconnecting anonymously keeps the chat readable.
        else if (!state.IsLoggedIn && _chatClient.CanSend) _ = ReconnectAsync(authenticated: false);
    }

    private async Task ReconnectAsync(bool authenticated)
    {
        _reconnecting = true;
        try
        {
            await _chatClient.DisconnectAsync();
            // Checked first so an authenticated connection is not attempted when no token can be
            // had. The connection asks again per attempt, but the answer is cached.
            if (authenticated && await _session.TryGetIrcTokenAsync() is null) return;
            await _chatClient.ConnectAsync(
                _settings.Channel,
                _session.Login,
                authenticated ? _session.TryGetIrcTokenAsync : null,
                _session.UserId);
            SetConnectionButtons(true);
            _hub.PublishAuth(_chatClient.CanSend);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TwitchAuthException)
        {
            SetStatus(ex.Message, true);
        }
        finally { _reconnecting = false; }
    }

    /// <summary>
    /// <see cref="OnRoomDiscoveredAsync"/> with every exception caught and logged. It runs on every
    /// connect and reconnect and calls Twitch several times, so a stalled request is ordinary, and
    /// everything it sets up – badges, reward names, the emote picker, the extra events – is
    /// optional and never worth losing the chat over.
    /// </summary>
    private async Task PrepareRoomAsync(string roomId)
    {
        try { await OnRoomDiscoveredAsync(roomId); }
        catch (Exception ex)
        {
            AppLog.Error($"Kunde inte förbereda kanalen (rum {roomId}). Chatten fortsätter ändå.", ex);
            SetStatus("Chat ansluten • extra funktioner kunde inte laddas: " + ex.Message, true);
        }
    }

    private async Task OnRoomDiscoveredAsync(string roomId)
    {
        _hub.BroadcasterId = roomId;
        _hub.PublishAuth(_chatClient.CanSend);
        // The preview lines come down on joining, not at the first real message: in a quiet room
        // that can be twenty minutes away, with invented chat on the stream overlay until then.
        _hub.ClearSamples();
        // First, so the chat reads on from where it left off before the slower Twitch calls below.
        await BackfillRecentMessagesAsync(_settings.Channel);
        // Which channel we are in decides what EventSub can carry, so it can only start once the
        // room id is known – not at login, and not when the socket opens.
        await RestartEventSubAsync(roomId);
        await LoadBadgesAsync(roomId);
        await LoadUsableEmotesAsync(roomId);
    }

    /// <summary>
    /// Loads which emotes we may send here. Done when the room becomes known, because the first
    /// line the streamer writes needs its emotes resolved before it reaches the overlay.
    /// </summary>
    private async Task LoadUsableEmotesAsync(string roomId)
    {
        // Another channel's emotes drawn onto our own lines would be worse than none at all.
        _emotes.Forget();
        if (!_session.IsLoggedIn) return;
        // TaskCanceledException is what HttpClient throws when a call runs past its timeout.
        try { await _emotes.GetAsync(roomId, _session.UserId); }
        catch (Exception ex) when (ex is TwitchApiException or TwitchAuthException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            // Not worth surfacing: without the list our own lines read as they were typed.
        }
    }

    /// <summary>
    /// Points EventSub at a channel. Logged out, a login without the needed scopes, or someone
    /// else's channel all end with the extra events switched off and the chat unaffected.
    /// </summary>
    private async Task RestartEventSubAsync(string broadcasterId)
    {
        // A reconnect or a channel switch can start this again while an earlier run is still waiting
        // on Twitch. Only the newest run may finish: two of them reaching StartAsync would throw.
        // Read and written on the UI thread only, so a plain counter is enough.
        int generation = ++_eventSubGeneration;

        await _eventSubClient.StopAsync();
        // Another channel's reward names would be wrong here, and a stale name is worse than none.
        _rewards.Clear();
        _powerUps.Clear();
        // Nothing can tell us a train ended during the gap, so the strip goes even when the channel
        // is the same one. A live train writes itself back on the next progress.
        _hub.ClearHypeTrain();
        _hypeCards.Clear();
        _petService.RedemptionsFromEventSub = false;
        // Whatever brought us here, redemptions stopped being delivered for a stretch, so the queue
        // has to be read again on the way back.
        _swept = false;

        // Pending redemptions belong to the channel they were made in, so a switch lets them go;
        // Twitch refuses answering them from another channel. A plain reconnect must keep them:
        // this also runs when IRC drops mid-stream, and the pets living through that gap were watched.
        if (!string.Equals(_ledgerChannel, broadcasterId, StringComparison.Ordinal))
        {
            _ledger.Reset();
            // A reading bought in the channel we have left has no business being read out over the
            // one we have joined – and the viewer who paid for it is not in this room.
            _tts.Reset();
            // The staged reels and the gifts still waiting for a recipient go the same way. The
            // wins themselves stay: they are the viewers' property, not the connection's.
            _spins.LeaveChannel();
            _ledgerChannel = broadcasterId;
            // Another channel's pets do not belong on this lawn. They are let go, not settled: they
            // stay unfulfilled in their own channel, where the sweep pays them back on return.
            _petRegistry.Clear();
            _hub.PublishPetsCleared();
        }
        if (generation != _eventSubGeneration) return;

        EventSubPlan plan = _eventSubClient.Plan(broadcasterId);
        if (plan.WorthConnecting) SetEventStatus("Slår på extra händelser …");
        // Fetched before the socket opens so the very first redemption already reads as a name
        // rather than teaching us the name only after it has scrolled past.
        if (plan.Redemptions) await LoadRewardNamesAsync(broadcasterId);
        if (generation != _eventSubGeneration) return;

        // The generation guard keeps a second start from happening; swallowed anyway, since a crash
        // here would cost the whole session over an optional feature.
        try { await _eventSubClient.StartAsync(broadcasterId); }
        catch (InvalidOperationException) { }
        // The sweep is not started here: StartAsync only kicks off the background task, and anything
        // redeemed before the subscription is confirmed would be invisible to EventSub and too new
        // for a sweep cut at this moment. See ApplyEventCoverage.
    }

    /// <summary>Runs the sweep and lets the next one in afterwards, however this one ended.</summary>
    private async Task SweepThenReleaseAsync(DateTimeOffset listeningSince, int generation)
    {
        try { await SweepLeftoverRedemptionsAsync(listeningSince, generation); }
        finally { _sweeping = false; }
    }

    /// <summary>
    /// Pays back every redemption of one of our rewards that was made while we were not listening.
    /// Pets and readings live in memory only, so after an exit, a crash, a spell in another channel
    /// or a connection gap, what such a redemption bought no longer exists.
    /// </summary>
    /// <param name="listeningSince">
    /// The moment EventSub confirmed the subscription. Everything redeemed before it is paid back;
    /// everything after arrives through the normal path.
    /// </param>
    private async Task SweepLeftoverRedemptionsAsync(DateTimeOffset listeningSince, int generation)
    {
        var managed = _settings.Pets.ManagedRewardIds.ToList();
        if (_settings.Tts.CanRefund && !managed.Contains(_settings.Tts.RewardId, StringComparer.OrdinalIgnoreCase))
            managed.Add(_settings.Tts.RewardId);
        // The spin reward is swept too. Its wins live on disk and survive anything, but a redemption
        // nobody was listening for never got a draw at all – the points are owed back. The gifts
        // still waiting are the exception, and ClaimedElsewhere shields those.
        if (_settings.Spin.CanRefund && !managed.Contains(_settings.Spin.RewardId, StringComparer.OrdinalIgnoreCase))
            managed.Add(_settings.Spin.RewardId);

        if (managed.Count == 0)
        {
            _swept = true;
            return;
        }

        bool complete;
        try { complete = await _ledger.SweepAsync(managed, listeningSince); }
        catch (Exception ex)
        {
            AppLog.Error("Inlösen: kunde inte städa kön från förra körningen", ex);
            complete = false;
        }
        if (generation != _eventSubGeneration) return;

        // Redemptions can be answered again, so anything the spin still owes is raised afresh.
        // After the sweep on purpose: a debt that has run out of attempts is written off here, and
        // the sweep that has just run is the one thing that would have paid it back on top of a pet
        // the viewer keeps.
        _spins.ResumeOwed();

        // Only a sweep that read every queue may stop the next connection from trying again;
        // otherwise one failed call leaves a viewer's points in Twitch's queue for the whole stream.
        _swept = complete;
        RunOnUi(() => PetRewardStatusText.Text = complete
            ? "Kön är genomgången."
            : "Kunde inte läsa hela kön – försöker igen vid nästa anslutning.");
    }

    private async Task LoadRewardNamesAsync(string broadcasterId)
    {
        try { _rewards.RememberAll(await _apiClient.GetCustomRewardsAsync(broadcasterId)); }
        catch (Exception ex) when (ex is TwitchApiException or TwitchAuthException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            // Not worth surfacing: without the list the names are learned from the redemptions
            // themselves, one reward at a time.
        }
    }

    private void SetEventStatus(string text) => EventStatusText.Text = text;

    /// <summary>
    /// Says what the extra events are doing and, when the stored login lacks a scope, offers to
    /// log in again. A missing permission is shown as an explanation, never as an error. Also
    /// starts the leftover-redemption sweep once redemptions are being delivered.
    /// </summary>
    private void ApplyEventCoverage(EventSubCoverage coverage)
    {
        _petService.RedemptionsFromEventSub = coverage.Redemptions;

        // The first moment redemptions are known to be delivered, so the sweep is cut here:
        // everything redeemed before now was redeemed while nobody was listening. Every gap – a
        // reconnect, a revoked subscription, a channel switch – arms it again, and the flag is
        // only set by a sweep that finished, so a failed one comes round again too.
        if (!coverage.Redemptions) _swept = false;
        else if (!_swept && !_sweeping)
        {
            _sweeping = true;
            _ = SweepThenReleaseAsync(DateTimeOffset.UtcNow, _eventSubGeneration);
        }

        var on = new List<string>();
        if (coverage.Redemptions) on.Add("inlösta belöningar visas med namn och kostnad");
        if (coverage.Shoutouts) on.Add("shoutouts visas");
        if (coverage.PowerUps) on.Add("power-ups visas");
        if (coverage.CustomPowerUps) on.Add("egna power-ups kan läsas upp");
        if (coverage.HypeTrain) on.Add("hypetåg visas");

        // A stored login only misses a scope when it was granted before we started asking for it.
        bool missingEventScopes = _session.IsLoggedIn && coverage.MissingScopes.Count > 0;

        // The reason has to be the real one. Blaming the channel role when the actual cause is a
        // permission we never asked for would send the user looking in the wrong place.
        string headline =
            on.Count > 0 ? "Extra händelser: " + string.Join(", ", on) + "."
            : !_session.IsLoggedIn ? "Inte inloggad, så subs och raids visas men inte belöningar, shoutouts, power-ups eller hypetåg."
            : missingEventScopes ? "Inga extra händelser än."
            : "Inga extra händelser i den här kanalen – belöningar, power-ups och hypetåg kräver din egen kanal, shoutouts att du är moderator.";

        // The emote scope is not an event, so it stays out of the headline, but the same re-login
        // grants it, so it joins the same sentence and the one button.
        var missing = coverage.MissingScopes.ToList();
        if (_session.IsLoggedIn && !_session.HasScope(TwitchAuth.EmotesScope)) missing.Add(TwitchAuth.EmotesScope);
        bool offerLogin = _session.IsLoggedIn && missing.Count > 0;

        EventStatusText.Text = offerLogin
            ? $"{headline} Logga in igen för att slå på {string.Join(" och ", missing.Select(TwitchAuth.DescribeScope))}."
            : headline;
        ReauthorizeButton.Visibility = offerLogin ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Logs out and starts a fresh login. New scopes only arrive through a fresh approval:
    /// refreshing a token hands back only the scopes it was granted.
    /// </summary>
    private async void Reauthorize_Click(object sender, RoutedEventArgs e)
    {
        ReauthorizeButton.Visibility = Visibility.Collapsed;
        string clientId = _session.ClientId.Length > 0 ? _session.ClientId : ClientIdBox.Text.Trim();
        await _session.LogoutAsync();
        _hub.PublishAuth(_chatClient.CanSend);
        if (clientId.Length == 0) return;
        ClientIdBox.Text = clientId;
        Login_Click(sender, e);
    }

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Öppna inställningar", null, (_, _) => RestoreFromTray());
        // Reachable from the tray, so the log can be found without the main window or a path into
        // %LOCALAPPDATA%.
        menu.Items.Add("Visa loggar", null, (_, _) => AppLog.OpenFolder());
        menu.Items.Add("Sök efter uppdateringar", null, async (_, _) =>
        {
            // The dialogs need a visible owner, and the tray is exactly where the window is not.
            ShowAndActivate();
            await UpdateCoordinator.CheckAsync(this, manual: true);
        });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Avsluta", null, (_, _) => ExitApplication());

        Stream? iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
        var icon = iconStream is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(iconStream);
        var trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Twitch Overlay Helper",
            ContextMenuStrip = menu,
            Visible = true
        };
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        return trayIcon;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested) return;

        e.Cancel = true;
        Hide();
    }

    private void RestoreFromTray()
    {
        ShowAndActivate();
    }

    public void StartHiddenInTray()
    {
        ShowInTaskbar = false;
        new WindowInteropHelper(this).EnsureHandle();
        Hide();
    }

    public void ShowAndActivate()
    {
        if (_closing) return;
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();

        // Helps Windows reliably foreground a window restored from the tray.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitApplication()
    {
        if (_closing) return;
        _exitRequested = true;
        Close();
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) =>
        await UpdateCoordinator.CheckAsync(this, manual: true);

    /// <summary>
    /// Ends the app the ordinary way while the updater waits for this process to exit, so settings
    /// and the last minutes of chat are written during shutdown.
    /// </summary>
    internal void ExitForUpdate() => ExitApplication();

    /// <summary>
    /// Everything one chat line sets off. The reward name is put on first, so the overlay, the dock
    /// and the pet rules all see the same message rather than three versions of it.
    /// </summary>
    private void OnChatMessage(ChatMessage message)
    {
        message = _rewards.Enrich(message);
        // A line the bot wrote itself is chat like any other to the views, but must not set anything
        // off: a welcome greeting the bot's own welcome, or "{pet} levde klart" spawning a pet
        // through the chat route, would loop.
        bool own = _bot.IsOwnMessage(message);
        // Marking and publishing are one step, see _publishGate.
        lock (_publishGate)
        {
            message = _powerUps.Enrich(message);
            // Over a game the bot's answers are notifications nobody asked for; in the dock they
            // are part of the conversation, so only the overlay drops them.
            if (!own || !_settings.Bot.HideOwnMessagesInOverlay) Queue(ChatTimelineItem.Of(message));
            _hub.PublishMessage(message);
        }
        if (own) return;

        _petService.HandleMessage(message);
        _fight.HandleMessage(message);
        TrackLastReward(message);
        // The bot's welcome and commands, independent of the edge alerts below.
        _bot.OnChatMessage(message);
        // The edge glow. A mod calling outranks the welcome: when a moderator's very first message
        // is the command, the streamer is being called, not introduced to their own mod.
        if (_settings.EdgeAlerts.TriggersModAlert(message))
        {
            RaiseEdgeAlert(EdgeAlertKind.ModCall);
            _bot.OnModCall(message);
        }
        else
        {
            ExplainMissedModCall(message);
            if (_settings.EdgeAlerts.TriggersNewChatterAlert(message)) RaiseEdgeAlert(EdgeAlertKind.NewChatter);
        }
    }

    /// <summary>
    /// Logs why a line that was the call command still did not light the edges – the alert is
    /// switched off, or the sender is not a moderator – since neither shows anywhere on screen.
    /// Does nothing for a line that is not the command.
    /// </summary>
    private void ExplainMissedModCall(ChatMessage message)
    {
        EdgeAlertSettings edge = _settings.EdgeAlerts;
        string text = message.Text.Trim();
        if (text.Length == 0 || text[0] != '!') return;

        string command = EdgeAlertSettings.CleanCommand(edge.ModCommand);
        bool isTheCommand = text.Equals(command, StringComparison.OrdinalIgnoreCase)
            || text.StartsWith(command + " ", StringComparison.OrdinalIgnoreCase);
        if (!isTheCommand) return;

        // The badges are quoted raw rather than summarised: if Twitch did not send the moderator
        // badge, that is the finding, and a "nej" of our own would hide it.
        string badges = message.Badges.Count == 0
            ? "inga"
            : string.Join(", ", message.Badges.Select(badge => badge.SetId));
        bool alertDisabled = !edge.ModAlert.Enabled;
        string reason = alertDisabled
            ? "mod-ljuset är avstängt i inställningarna"
            : "avsändaren är varken moderator eller broadcaster";
        // The sender cannot see the log, so the bot words the same two answers for them: a
        // moderator wants to know what to do, the log what happened.
        _bot.OnModCallMissed(message, alertDisabled);
        // The mod tag is what the decision rests on, so it is logged next to the badges: badges
        // alone can make a lead moderator look like an ordinary viewer.
        string modTag = message.HasModTag ? "ja" : "nej";
        AppLog.Info($"\"{text}\" från {message.DisplayName} tände inte kanterna: {reason}. Mod-tagg: {modTag}. Badges: {badges}.");
    }

    /// <summary>
    /// Lights the edges, unless <see cref="_edgeScheduler"/> says this one has nothing to add to
    /// what is already lit. Does not check whether the alert is switched on; callers do.
    /// </summary>
    private void RaiseEdgeAlert(EdgeAlertKind kind)
    {
        EdgeAlertStyle style = kind switch
        {
            EdgeAlertKind.ModCall => _settings.EdgeAlerts.ModAlert,
            EdgeAlertKind.TtsRequest => _settings.EdgeAlerts.TtsAlert,
            _ => _settings.EdgeAlerts.NewChatterAlert
        };
        // The length comes from the scheduler, not from the setting: a glow being held open has only
        // the rest of its ceiling to run, or a busy chat would keep the edges lit past the limit.
        if (_edgeScheduler.PlayFor(kind, style.DurationSeconds, DateTimeOffset.UtcNow) is not { } lit)
        {
            AppLog.Info($"Kantljuset ({kind}) hoppades över – ett ljus pågår redan eller taket är nått.");
            return;
        }
        AppLog.Info($"Kantljuset tänds ({kind}) i {lit.TotalSeconds:0.0} s.");
        RunOnUi(() => _edgeAlerts.Play(style, _settings.EdgeAlerts.EdgeWidth, lit.TotalSeconds));
    }

    private void OnChatEvent(ChatEvent chatEvent)
    {
        Queue(ChatTimelineItem.Of(chatEvent));
        _hub.PublishEvent(chatEvent);
        _bot.OnChatEvent(chatEvent);
    }

    /// <summary>
    /// A step in a hype train. Bypasses <see cref="OnChatEvent"/> because the two views want
    /// different things: the dock is handed the whole state and draws a strip for as long as the
    /// train lasts, while the overlay gets a card for the start, one for the end and nothing between.
    /// </summary>
    private void OnHypeTrain(HypeTrainState train)
    {
        _hub.PublishHypeTrain(train);
        // The card's id names the train and which of its two moments this is, so a moment that has
        // already been carded cannot be carded again – whatever order Twitch delivers in.
        if (train.ToChatEvent() is not { } card || !_hypeCards.IsNew(card.Id)) return;
        Queue(ChatTimelineItem.Of(card));
        // Deduplicated first, so the bot cannot congratulate the same train twice when Twitch
        // delivers a begin after the progress that started it.
        _bot.OnChatEvent(card);
    }

    /// <summary>
    /// A redemption from EventSub. It carries the name and price IRC never sends, so it teaches the
    /// catalogue before it reaches the pets – and a reward with no text field gets here even though
    /// it never produces a chat line at all.
    /// </summary>
    private void OnRedemption(RewardRedemption redemption)
    {
        if (redemption.RewardId.Length > 0)
            _rewards.Remember(new CustomReward(redemption.RewardId, redemption.RewardTitle, redemption.RewardCost ?? 0));

        RunOnUi(() => ShowLastReward(redemption.RewardId, redemption.RewardTitle, redemption.DisplayName));

        // The reading reward is claimed before the pets see it: a channel with no pet rules spawns
        // a pet for every redemption, and the same redemption would be answered twice.
        if (_settings.Tts.MatchesReward(redemption.RewardId))
        {
            HandleTtsRedemption(redemption);
            return;
        }

        // The spin reward is claimed before the pets for the same reason.
        if (_settings.Spin.MatchesReward(redemption.RewardId, redemption.RewardTitle))
        {
            HandleSpinRedemption(redemption);
            return;
        }

        PetRedemptionResult result = _petService.HandleRedemption(redemption);
        AnswerRedemption(redemption, result);
        // Told the outcome, not the verdict: a redemption that bought nothing is worth a word in
        // chat whether or not the points can be handed back.
        _bot.OnPetOutcome(
            redemption.DisplayName.Length > 0 ? redemption.DisplayName : redemption.UserLogin,
            redemption.RewardCost ?? 0,
            result.Outcome);
        // Announced separately because it needs the pet's name, which the outcome does not carry.
        if (result is { Outcome: PetSpawnOutcome.NotOwned, Asked: { } asked })
            _bot.Announce(BotFlow.SpinNotOwned, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["viewer"] = redemption.DisplayName.Length > 0 ? redemption.DisplayName : redemption.UserLogin,
                ["prize"] = asked.Name
            });
    }

    /// <summary>
    /// A redemption of the spin reward. The service books the draw and raises its own verdicts;
    /// all that is left here is paying back the redemptions that bought nothing.
    /// </summary>
    private void HandleSpinRedemption(RewardRedemption redemption)
    {
        SpinRedemptionResult result = _spins.HandleRedemption(redemption);
        if (!result.Refundable) return;

        string? reason = result.Outcome switch
        {
            SpinOutcome.Disabled => "lyckosnurren är avstängd i appen",
            SpinOutcome.PetsOff => "pets är avstängda i appen",
            SpinOutcome.NoOverlay => "pet-overlayen var inte igång",
            SpinOutcome.NothingToWin => "det finns inget att vinna just nu",
            SpinOutcome.AllOwned => "hela samlingen är redan vunnen",
            _ => null
        };
        if (reason is null) return;
        _ = _ledger.AnswerNow(
            redemption.Id, redemption.RewardId,
            redemption.DisplayName.Length > 0 ? redemption.DisplayName : redemption.UserLogin,
            redemption.RewardCost ?? 0,
            RedemptionStatus.Canceled, reason, "spin");
    }

    /// <summary>
    /// A channel points redemption bought a reading. Whether it can be paid back is decided here and
    /// carried along: the settings can be changed while a request is waiting, and a redemption has
    /// to be answered on the terms it was accepted under.
    /// </summary>
    private void HandleTtsRedemption(RewardRedemption redemption)
    {
        var request = new TtsRequest(
            redemption.Id,
            TtsSource.Reward,
            redemption.RewardId,
            _settings.Tts.CanRefund,
            redemption.UserId,
            redemption.DisplayName.Length > 0 ? redemption.DisplayName : redemption.UserLogin,
            redemption.UserInput ?? string.Empty,
            redemption.RewardCost ?? 0,
            redemption.At);
        _tts.Handle(request);
    }

    /// <summary>
    /// A viewer redeemed one of the channel's own bits Power-ups. Nothing here can ever be paid back:
    /// Twitch has no endpoint for answering a Power-up redemption, so the bits are spent whatever the
    /// streamer decides. Everything else works exactly as it does for a channel points reward.
    /// </summary>
    private void OnCustomPowerUp(CustomPowerUpRedemption redemption)
    {
        if (!_settings.Tts.MatchesPowerUp(redemption.PowerUpId)) return;

        _tts.Handle(new TtsRequest(
            redemption.Id,
            TtsSource.PowerUp,
            string.Empty,
            false,
            redemption.UserId,
            redemption.DisplayName.Length > 0 ? redemption.DisplayName : redemption.UserLogin,
            redemption.UserInput ?? string.Empty,
            redemption.Bits,
            redemption.At));
    }

    /// <summary>
    /// Plays a reading where the settings say: the browser source, which is what reaches the viewers
    /// through OBS, the streamer's own speakers, or both. With both, only the browser is awaited
    /// and only its failure counts; a busy sound card is no reason to pay a viewer back.
    /// </summary>
    private async Task PlayReadingAsync(TtsClip clip, CancellationToken cancellationToken)
    {
        TtsSettings tts = _settings.Tts;

        if (tts.UsesDesktop)
        {
            Task desktop = _audioPlayer.PlayToEndAsync(clip.FilePath, clip.Volume, cancellationToken);
            // Only awaited when it is the whole output; alongside the browser it is left to run and
            // its failures are logged rather than charged to anybody.
            if (!tts.UsesBrowser) { await desktop.ConfigureAwait(false); return; }
            _ = desktop.ContinueWith(
                task => AppLog.Warn($"Uppläsning: kunde inte spela upp lokalt: {task.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        await _browserTts.PlayAsync(clip, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells Twitch what became of a reading that was bought with channel points. Only called for
    /// requests the app accepted as refundable, so the reward is one this client id created.
    /// </summary>
    private Task AnswerTtsAsync(TtsRequest request, RedemptionStatus status, string reason) =>
        _ledger.AnswerNow(request.Id, request.RewardId, request.DisplayName, request.Cost, status, reason, "tts");

    /// <summary>
    /// Tells Twitch what became of a redemption, for the rewards this app created – the only ones
    /// it may answer.
    ///
    /// <para>A pet that did spawn is not answered yet: it goes into the ledger and stays in the
    /// channel's queue until it has lived its time, the only window in which it can still be paid
    /// back if an OBS browser source never drew it. Everything that failed outright is refunded on
    /// the spot.</para>
    /// </summary>
    private void AnswerRedemption(RewardRedemption redemption, PetRedemptionResult result)
    {
        if (!result.Refundable) return;

        string viewer = redemption.DisplayName.Length > 0 ? redemption.DisplayName : redemption.UserLogin;
        int cost = redemption.RewardCost ?? 0;

        if (result is { Outcome: PetSpawnOutcome.Spawned, Pet: { } pet })
        {
            _ledger.Track(redemption.Id, redemption.RewardId, pet.Id, viewer, cost,
                DateTimeOffset.FromUnixTimeMilliseconds(pet.ExpiresAt));
            return;
        }

        string? reason = result.Outcome switch
        {
            PetSpawnOutcome.Disabled => "pets är avstängda i appen",
            PetSpawnOutcome.Full => "det var fullt på gräsmattan",
            PetSpawnOutcome.NoOverlay => "pet-overlayen var inte igång",
            PetSpawnOutcome.NotOwned => "arten kan bara användas av den som vunnit den",
            _ => null
        };
        if (reason is null) return;
        _ = _ledger.RefundNow(redemption.Id, redemption.RewardId, viewer, cost, reason);
    }

    /// <summary>
    /// Shows what the ledger just did, in the status line of the feature the redemption paid for.
    /// </summary>
    private void ShowRedemptionNotice(RedemptionNotice notice)
    {
        if (notice.Subject == "tts")
        {
            TtsStatusText.Text = notice.Refunded
                ? $"↩ {notice.ViewerName} fick tillbaka {notice.Cost} poäng – {notice.Reason}."
                : $"✓ {notice.ViewerName}s uppläsning är bokförd som klar.";
            return;
        }

        if (notice.Subject == "spin")
        {
            PetRewardStatusText.Text = notice.Refunded
                ? $"↩ {notice.ViewerName} fick tillbaka {notice.Cost} poäng – {notice.Reason}."
                : $"✓ Lyckosnurren: {notice.ViewerName}s inlösen är bokförd – {notice.Reason}.";
            return;
        }

        PetRewardStatusText.Text = notice.Refunded
            ? $"↩ {notice.ViewerName} fick tillbaka {notice.Cost} poäng – {notice.Reason}."
            : $"✓ {notice.ViewerName}s pet levde klart.";
    }

    /// <summary>
    /// A Gigantify an Emote power-up. It carries no message id, so the tracker pairs it with the
    /// chat line it belongs to – and when the line got here first, that line has to be sent again
    /// with the marker on it. A power-up whose message has not arrived yet answers null and waits
    /// inside the tracker instead, where <see cref="OnChatMessage"/> picks it up.
    /// </summary>
    private void OnGigantify(GigantifiedEmote powerUp)
    {
        ChatMessage marked;
        lock (_publishGate)
        {
            if (_powerUps.Match(powerUp) is not { } matched) return;
            marked = matched;
            _hub.PublishMessageUpdate(marked);
        }
        RunOnUi(() =>
        {
            // The line may still be in the pending queue, with no card to update until it is drawn.
            // Drained, not flushed once: a flush takes only fifty at a time.
            DrainPendingMessages();
            _overlay.UpdateMessage(marked);
        });
    }

    private void Queue(ChatTimelineItem item)
    {
        _pendingMessages.Enqueue(item);
        int count = Interlocked.Increment(ref _pendingMessageCount);
        while (count > 500 && _pendingMessages.TryDequeue(out _))
            count = Interlocked.Decrement(ref _pendingMessageCount);

        if (Interlocked.Exchange(ref _chatTimerRequested, 1) == 0)
            RunOnUi(() => _chatFlushTimer.Start());
    }

    /// <summary>
    /// Empties the pending queue instead of taking the usual batch off it. Bounded at twenty passes,
    /// twice what the 500-capped queue can hold, so a fast chat cannot hold the UI thread here.
    /// </summary>
    private void DrainPendingMessages()
    {
        for (int pass = 0; pass < 20 && !_pendingMessages.IsEmpty; pass++)
            FlushPendingMessages(null, EventArgs.Empty);
    }

    private void FlushPendingMessages(object? sender, EventArgs e)
    {
        var batch = new List<ChatTimelineItem>(50);
        while (batch.Count < 50 && _pendingMessages.TryDequeue(out ChatTimelineItem item))
        {
            Interlocked.Decrement(ref _pendingMessageCount);
            batch.Add(item);
        }
        if (batch.Count > 0) _overlay.AddItems(batch);

        if (!_pendingMessages.IsEmpty) return;
        _chatFlushTimer.Stop();
        Interlocked.Exchange(ref _chatTimerRequested, 0);
        if (!_pendingMessages.IsEmpty && Interlocked.Exchange(ref _chatTimerRequested, 1) == 0)
            _chatFlushTimer.Start();
    }

    private void PopulateControls()
    {
        PopulateBotControls();
        PopulateFightControls();
        ChannelBox.Text = _settings.Channel;
        RecentMessagesCheck.IsChecked = _settings.FetchRecentMessages;
        ClientIdBox.Text = _settings.ClientId;
        FontSizeSlider.Value = _settings.FontSize;
        LineSpacingSlider.Value = _settings.LineSpacing;
        OpacitySlider.Value = _settings.BackgroundOpacity;
        MessageOpacitySlider.Value = _settings.MessageBackgroundOpacity;
        BadgesCheck.IsChecked = _settings.ShowBadges;
        NameColorsCheck.IsChecked = _settings.UseTwitchNameColors;
        TimestampCheck.IsChecked = _settings.ShowTimestamps;
        MentionsCheck.IsChecked = _settings.EmphasizeMentions;
        EmotesCheck.IsChecked = _settings.ShowEmotes;
        GiantEmotesCheck.IsChecked = _settings.GiantEmotes;
        GifsCheck.IsChecked = _settings.ShowGifs;
        EventSubsCheck.IsChecked = _settings.Events.Subs;
        EventRaidsCheck.IsChecked = _settings.Events.Raids;
        EventAnnouncementsCheck.IsChecked = _settings.Events.Announcements;
        EventBitsCheck.IsChecked = _settings.Events.Bits;
        EventMilestonesCheck.IsChecked = _settings.Events.Milestones;
        EventRewardsCheck.IsChecked = _settings.Events.Rewards;
        EventShoutoutsCheck.IsChecked = _settings.Events.Shoutouts;
        EventHypeCheck.IsChecked = _settings.Events.HypeTrain;
        EventOtherCheck.IsChecked = _settings.Events.Other;
        OutlineCheck.IsChecked = _settings.TextOutline;
        StartWithWindowsCheck.IsChecked = _settings.StartWithWindows;
        SelectComboByText(FontFamilyBox, _settings.FontFamily);
        _settings.MaxMessages = Math.Clamp(_settings.MaxMessages, 1, 200);
        MaxMessagesInput.Text = _settings.MaxMessages.ToString();
        VisibilityButton.Content = _settings.OverlayVisible ? "Dölj overlay" : "Visa overlay";
        PetsEnabledCheck.IsChecked = _settings.Pets.Enabled;
        PetNamesCheck.IsChecked = _settings.Pets.ShowNames;
        PetRarityFxCheck.IsChecked = _settings.Pets.RarityEffects;
        PetRarityFadeInput.Text = _settings.Pets.RarityFadeSeconds.ToString();
        PetScaleSlider.Value = _settings.Pets.Scale;
        PetScaleValue.Text = $"{_settings.Pets.Scale:P0}";
        PetAnimSpeedSlider.Value = _settings.Pets.AnimationSpeed;
        PetAnimSpeedValue.Text = $"{_settings.Pets.AnimationSpeed:P0}";
        PetLifetimeInput.Text = _settings.Pets.LifetimeMinutes.ToString();
        PetMaxInput.Text = _settings.Pets.MaxPets.ToString();
        foreach (PetRewardRule rule in _settings.Pets.Rewards) _petRewards.Add(rule);
        PetRewardList.ItemsSource = _petRewards;
        ShowPetRewardTable();
        RefreshPetCatalogUi();
        ToggleHotkeyLabel.Text = _settings.ToggleHotkeyText;
        EditHotkeyLabel.Text = _settings.EditHotkeyText;
        PopulateColorBox(EdgeModColorBox);
        PopulateColorBox(EdgeNewColorBox);
        PopulateColorBox(EdgeTtsColorBox);
        EdgeModEnabledCheck.IsChecked = _settings.EdgeAlerts.ModAlert.Enabled;
        EdgeModCommandBox.Text = _settings.EdgeAlerts.ModCommand;
        SelectColor(EdgeModColorBox, _settings.EdgeAlerts.ModAlert.Color);
        EdgeModIntensitySlider.Value = _settings.EdgeAlerts.ModAlert.Intensity;
        EdgeModDurationSlider.Value = _settings.EdgeAlerts.ModAlert.DurationSeconds;
        EdgeNewEnabledCheck.IsChecked = _settings.EdgeAlerts.NewChatterAlert.Enabled;
        SelectColor(EdgeNewColorBox, _settings.EdgeAlerts.NewChatterAlert.Color);
        EdgeNewIntensitySlider.Value = _settings.EdgeAlerts.NewChatterAlert.Intensity;
        EdgeNewDurationSlider.Value = _settings.EdgeAlerts.NewChatterAlert.DurationSeconds;
        SelectColor(EdgeTtsColorBox, _settings.EdgeAlerts.TtsAlert.Color);
        EdgeTtsIntensitySlider.Value = _settings.EdgeAlerts.TtsAlert.Intensity;
        EdgeTtsDurationSlider.Value = _settings.EdgeAlerts.TtsAlert.DurationSeconds;
        EdgeWidthSlider.Value = _settings.EdgeAlerts.EdgeWidth;
        PopulateTtsControls();
        UpdateValueLabels();
    }

    /// <summary>The colours the edge glow can have.</summary>
    private static readonly (string Name, string Hex)[] EdgeColors =
    [
        ("Orange", "#F59E0B"), ("Röd", "#EF4444"), ("Rosa", "#DB2777"), ("Lila", "#A970FF"),
        ("Blå", "#3B82F6"), ("Turkos", "#5FD6C8"), ("Grön", "#22C55E"), ("Gul", "#FACC15"), ("Vit", "#FFFFFF")
    ];

    private static void PopulateColorBox(ComboBox box)
    {
        foreach ((string name, string hex) in EdgeColors)
        {
            var swatch = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(swatch);
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            box.Items.Add(new ComboBoxItem { Content = row, Tag = hex });
        }
    }

    private static void SelectColor(ComboBox box, string hex)
    {
        foreach (object item in box.Items)
            if (item is ComboBoxItem option && string.Equals(option.Tag as string, hex, StringComparison.OrdinalIgnoreCase)) { box.SelectedItem = option; return; }
        box.SelectedIndex = 0;
    }

    private static string SelectedColor(ComboBox box, string fallback) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource?.AddHook(WndProc);
        RegisterHotkeys();
    }

    private void RegisterHotkeys()
    {
        bool toggleOk = GlobalHotkeys.ReRegister(this, ToggleHotkeyId, _settings.ToggleHotkeyModifiers, _settings.ToggleHotkeyVk);
        bool editOk = GlobalHotkeys.ReRegister(this, EditHotkeyId, _settings.EditHotkeyModifiers, _settings.EditHotkeyVk);
        HotkeyHint.Text = toggleOk && editOk
            ? "Välj Ändra och tryck sedan önskad tangentkombination. Esc avbryter."
            : "En snabbtangent kunde inte registreras – den används troligen av ett annat program. Välj en annan kombination.";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == GlobalHotkeys.WmHotkey)
        {
            switch (wParam.ToInt32())
            {
                case ToggleHotkeyId: ToggleOverlayVisibility(); handled = true; break;
                case EditHotkeyId: ToggleEditMode(); handled = true; break;
            }
        }
        return IntPtr.Zero;
    }

    private void RecordToggleHotkey_Click(object sender, RoutedEventArgs e) => StartRecording(RecordToggleHotkeyButton);
    private void RecordEditHotkey_Click(object sender, RoutedEventArgs e) => StartRecording(RecordEditHotkeyButton);

    private void StartRecording(Button button)
    {
        if (_recordingButton is not null) StopRecording();
        _recordingButton = button;
        button.Content = "⏺ Tryck tangenter …";
        button.Background = new SolidColorBrush(Color.FromRgb(90, 39, 39));
        PreviewKeyDown += OnHotkeyRecordKeyDown;
        Focus();
    }

    private void OnHotkeyRecordKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        if (key == Key.Escape)
        {
            StopRecording();
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        uint mod = 0;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) { mod |= 0x0002; parts.Add("Ctrl"); }
        if (modifiers.HasFlag(ModifierKeys.Alt)) { mod |= 0x0001; parts.Add("Alt"); }
        if (modifiers.HasFlag(ModifierKeys.Shift)) { mod |= 0x0004; parts.Add("Shift"); }
        if (modifiers.HasFlag(ModifierKeys.Windows)) { mod |= 0x0008; parts.Add("Win"); }

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        parts.Add(key.ToString());
        string display = string.Join(" + ", parts);

        if (_recordingButton == RecordToggleHotkeyButton)
        {
            _settings.ToggleHotkeyModifiers = mod;
            _settings.ToggleHotkeyVk = vk;
            _settings.ToggleHotkeyText = display;
            ToggleHotkeyLabel.Text = display;
        }
        else
        {
            _settings.EditHotkeyModifiers = mod;
            _settings.EditHotkeyVk = vk;
            _settings.EditHotkeyText = display;
            EditHotkeyLabel.Text = display;
        }

        StopRecording();
        RegisterHotkeys();
        SaveSettings();
    }

    private void StopRecording()
    {
        PreviewKeyDown -= OnHotkeyRecordKeyDown;
        if (_recordingButton is not null)
        {
            _recordingButton.Content = "⌨ Ändra";
            _recordingButton.Background = new SolidColorBrush(Color.FromRgb(52, 59, 80));
            _recordingButton = null;
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        string channel = TwitchChatClient.NormalizeChannel(ChannelBox.Text);
        if (channel.Length == 0)
        {
            SetStatus("Skriv ett giltigt kanalnamn", true);
            ChannelBox.Focus();
            return;
        }

        // Another channel's chat must not survive into this one – not on screen, and not on disk
        // waiting for the next restart to put it back.
        if (!string.Equals(_settings.Channel, channel, StringComparison.OrdinalIgnoreCase))
        {
            _historyStore.Clear();
            // The new channel has its own older lines, and they have not been fetched yet.
            _backfilledChannel = null;
        }

        _settings.Channel = channel;
        _settings.ClientId = ClientIdBox.Text.Trim();
        _lastBadgeRoom = null;
        _badgeLoadCancellation?.Cancel();
        // The previous streamer's subscriber icons are dropped at once, not when the new set arrives:
        // logged out, Twitch hands out no badges, and the new set may never come.
        _badgeCatalog.ForgetChannel();
        _overlay.RefreshMessages();
        _hub.PublishBadgesLoaded();
        // The old channel's subscriptions and reward names have nothing to do with the new one;
        // room discovery on the new connection starts them again.
        await _eventSubClient.StopAsync();
        _rewards.Clear();
        // Whatever the old channel had lit is over; the new one's first alert should not be held
        // back by a cooldown someone else's chat started.
        _edgeScheduler.Reset();
        _hub.SetChannel(channel);
        ScheduleOverlayApply();
        SaveSettings();
        SetConnectionButtons(true);
        try
        {
            // An authenticated connection is what lets the dock send messages; anonymous still reads fine.
            await _chatClient.ConnectAsync(
                channel,
                _session.Login,
                _session.IsLoggedIn ? _session.TryGetIrcTokenAsync : null,
                _session.UserId);
            _hub.PublishAuth(_chatClient.CanSend);
            // The bot follows the app into the channel, also on a channel switch.
            ApplyBotConfiguration();
        }
        catch (Exception ex) { SetConnectionButtons(false); SetStatus(ex.Message, true); }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await _chatClient.DisconnectAsync();
        await _eventSubClient.StopAsync();
        // Nothing the bot was about to say is still true of a channel the app has left, and the
        // account leaves with it.
        _bot.Reset();
        await _botAccount.DisconnectAsync();
        UpdateBotUi();
        // Nothing will reach us about the train from here, so the strip must not outlive the
        // connection that was feeding it.
        _hub.ClearHypeTrain();
        _hypeCards.Clear();
        SetConnectionButtons(false);
    }

    private void EditButton_Click(object sender, RoutedEventArgs e) => ToggleEditMode();

    private void ToggleEditMode()
    {
        _editing = !_editing;
        if (_editing && !_settings.OverlayVisible)
        {
            _settings.OverlayVisible = true;
            VisibilityButton.Content = "Dölj overlay";
            SaveSettings();
        }
        _overlay.SetEditMode(_editing);
        EditButton.Content = _editing ? "Lås overlay" : "Redigera overlay";
        EditButton.Background = new SolidColorBrush(_editing ? Color.FromRgb(25, 145, 125) : Color.FromRgb(109, 63, 209));
    }

    private void VisibilityButton_Click(object sender, RoutedEventArgs e) => ToggleOverlayVisibility();

    private void ToggleOverlayVisibility()
    {
        _settings.OverlayVisible = !_settings.OverlayVisible;
        if (_settings.OverlayVisible) _overlay.Show(); else _overlay.Hide();
        VisibilityButton.Content = _settings.OverlayVisible ? "Dölj overlay" : "Visa overlay";
        SaveSettings();
    }

    private void Setting_ValueChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _overlay is null) return;
        _settings.FontSize = FontSizeSlider.Value;
        _settings.LineSpacing = LineSpacingSlider.Value;
        _settings.BackgroundOpacity = OpacitySlider.Value;
        _settings.MessageBackgroundOpacity = MessageOpacitySlider.Value;
        _settings.ShowBadges = BadgesCheck.IsChecked == true;
        _settings.UseTwitchNameColors = NameColorsCheck.IsChecked == true;
        _settings.ShowTimestamps = TimestampCheck.IsChecked == true;
        _settings.EmphasizeMentions = MentionsCheck.IsChecked == true;
        _settings.ShowEmotes = EmotesCheck.IsChecked == true;
        _settings.GiantEmotes = GiantEmotesCheck.IsChecked == true;
        _settings.ShowGifs = GifsCheck.IsChecked == true;
        _settings.Events.Subs = EventSubsCheck.IsChecked == true;
        _settings.Events.Raids = EventRaidsCheck.IsChecked == true;
        _settings.Events.Announcements = EventAnnouncementsCheck.IsChecked == true;
        _settings.Events.Bits = EventBitsCheck.IsChecked == true;
        _settings.Events.Milestones = EventMilestonesCheck.IsChecked == true;
        _settings.Events.Rewards = EventRewardsCheck.IsChecked == true;
        _settings.Events.Shoutouts = EventShoutoutsCheck.IsChecked == true;
        _settings.Events.HypeTrain = EventHypeCheck.IsChecked == true;
        _settings.Events.Other = EventOtherCheck.IsChecked == true;
        _settings.TextOutline = OutlineCheck.IsChecked == true;
        _settings.FontFamily = SelectedText(FontFamilyBox) ?? "Verdana";
        UpdateValueLabels();
        ScheduleOverlayApply();
        SaveSettings();
    }

    private void EdgeSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _overlay is null) return;
        EdgeAlertSettings edge = _settings.EdgeAlerts;
        edge.ModAlert.Enabled = EdgeModEnabledCheck.IsChecked == true;
        edge.ModAlert.Color = SelectedColor(EdgeModColorBox, edge.ModAlert.Color);
        edge.ModAlert.Intensity = EdgeModIntensitySlider.Value;
        edge.ModAlert.DurationSeconds = EdgeModDurationSlider.Value;
        edge.NewChatterAlert.Enabled = EdgeNewEnabledCheck.IsChecked == true;
        edge.NewChatterAlert.Color = SelectedColor(EdgeNewColorBox, edge.NewChatterAlert.Color);
        edge.NewChatterAlert.Intensity = EdgeNewIntensitySlider.Value;
        edge.NewChatterAlert.DurationSeconds = EdgeNewDurationSlider.Value;
        // Deliberately no Enabled here: that switch lives on the reading tab, next to the feature
        // it belongs to.
        edge.TtsAlert.Color = SelectedColor(EdgeTtsColorBox, edge.TtsAlert.Color);
        edge.TtsAlert.Intensity = EdgeTtsIntensitySlider.Value;
        edge.TtsAlert.DurationSeconds = EdgeTtsDurationSlider.Value;
        edge.EdgeWidth = EdgeWidthSlider.Value;
        UpdateValueLabels();
        SaveSettings();
    }

    /// <summary>
    /// Stores the setting only. The fetch belongs to a connection, so switching it on mid-session
    /// takes effect on the next connect.
    /// </summary>
    private void RecentMessages_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.FetchRecentMessages = RecentMessagesCheck.IsChecked == true;
        SaveSettings();
    }

    private void EdgeModCommand_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _settings.EdgeAlerts.ModCommand = EdgeAlertSettings.CleanCommand(EdgeModCommandBox.Text);
        SaveSettings();
    }

    /// <summary>Shows the cleaned command that actually counts: "psst hej" is stored as "!psst".</summary>
    private void EdgeModCommand_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => EdgeModCommandBox.Text = _settings.EdgeAlerts.ModCommand;

    // The test buttons play even when the alert is switched off, so the look can be tuned first
    // and the switch flipped after.
    private void EdgeModTest_Click(object sender, RoutedEventArgs e)
        => _edgeAlerts.Play(_settings.EdgeAlerts.ModAlert, _settings.EdgeAlerts.EdgeWidth);

    private void EdgeTtsTest_Click(object sender, RoutedEventArgs e)
        => _edgeAlerts.Play(_settings.EdgeAlerts.TtsAlert, _settings.EdgeAlerts.EdgeWidth);

    // Uppläsning

    private void PopulateTtsControls()
    {
        TtsSettings tts = _settings.Tts;
        TtsEnabledCheck.IsChecked = tts.Enabled;
        TtsPowerUpRadio.IsChecked = tts.Trigger == TtsTrigger.PowerUp;
        TtsRewardRadio.IsChecked = tts.Trigger == TtsTrigger.Reward;
        TtsPowerUpIdBox.Text = tts.PowerUpId;
        TtsRewardTitleBox.Text = tts.RewardTitle.Length > 0 ? tts.RewardTitle : "Läs upp mitt meddelande";
        TtsRewardIdBox.Text = tts.RewardId;
        TtsRewardCostBox.Text = tts.RewardCost.ToString();
        TtsApprovalCheck.IsChecked = tts.RequireApproval;
        TtsEdgeAlertCheck.IsChecked = _settings.EdgeAlerts.TtsAlert.Enabled;
        TtsTimeoutBox.Text = Math.Max(1, tts.ApprovalTimeoutSeconds / 60).ToString();
        TtsQueueBox.Text = tts.QueueLimit.ToString();
        TtsVoiceIdBox.Text = tts.VoiceId;
        TtsModelBox.Text = tts.ElevenLabsModel;
        TtsVolumeSlider.Value = tts.Volume;
        TtsMaxCharsSlider.Value = tts.MaxCharacters;
        TtsAnnounceNameCheck.IsChecked = tts.AnnounceName;
        TtsOutputBrowserRadio.IsChecked = tts.Output == TtsOutput.Browser;
        TtsOutputDesktopRadio.IsChecked = tts.Output == TtsOutput.Desktop;
        TtsOutputBothRadio.IsChecked = tts.Output == TtsOutput.Both;
        if (tts.VoiceName.Length > 0) TtsVoiceHint.Text = $"Vald röst: {tts.VoiceName}";
        ShowTtsTrigger();
        ShowTtsWidget();
    }

    private void TtsOutput_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Tts.Output = TtsOutputDesktopRadio.IsChecked == true
            ? TtsOutput.Desktop
            : TtsOutputBothRadio.IsChecked == true ? TtsOutput.Both : TtsOutput.Browser;
        SaveSettings();
        // Where the sound goes decides whether the card can be drawn: no clip reaches the reading
        // page when readings play on this machine's speakers, so the page is told about the change.
        ShowTtsWidget();
        _hub.PublishTtsWidget();
    }

    /// <summary>
    /// Shows what the OBS source should be right now: its size, and whether it draws anything.
    /// </summary>
    private void ShowTtsWidget()
    {
        // A card that is switched on but whose sound never goes through the browser cannot be
        // drawn, so the hint must not ask for the source to be resized for it.
        bool card = _settings.Tts.ShowsWidget;
        TtsSourceHint.Text = card
            ? "Lägg till som Browser Source i OBS och gör den lika stor som scenen – rutan placerar sig själv i bilden och resten av sidan är genomskinlig. Kryssa i “Control audio via OBS” om du vill ha ett eget reglage i mixern."
            : "Lägg till som Browser Source i OBS, 1×1 px – den syns inte, den låter bara. Kryssa i “Control audio via OBS” om du vill ha den som ett eget reglage i mixern.";

        TtsWidgetStatusText.Text = !_settings.Tts.Widget.Enabled
            ? "Av – browserkällan ritar ingenting."
            : card
                ? "På. Kom ihåg att göra browserkällan lika stor som scenen i OBS."
                : "På, men ljudet är satt till den här datorns högtalare – då går uppläsningen aldrig via browserkällan och rutan syns aldrig.";
    }

    private void TtsWidget_Click(object sender, RoutedEventArgs e)
    {
        if (_ttsWidgetWindow is { IsLoaded: true })
        {
            _ttsWidgetWindow.Activate();
            return;
        }

        _ttsWidgetWindow = new TtsWidgetWindow(
            _settings,
            () =>
            {
                SaveSettings();
                ShowTtsWidget();
                _hub.PublishTtsWidget();
            },
            PreviewTtsWidget) { Owner = this };
        _ttsWidgetWindow.Closed += (_, _) => _ttsWidgetWindow = null;
        _ttsWidgetWindow.Show();
    }

    /// <summary>
    /// Draws the card in OBS with an invented reading and returns what to tell the user. Nothing
    /// is synthesised, nothing is queued and nobody is charged.
    /// </summary>
    private string PreviewTtsWidget()
    {
        if (!_dockServer.IsRunning) return "Chattservern kör inte – starta den under Anslutning först.";
        if (!_settings.Tts.Widget.Enabled) return "Kryssa i “Visa rutan under uppläsning” först.";
        if (!_settings.Tts.UsesBrowser) return "Uppläsningen är satt till den här datorns högtalare, så browserkällan får aldrig något att rita.";

        int pages = _hub.PublishTtsPreview(
            _settings.UserName.Length > 0 ? _settings.UserName : "Kajsa_92",
            "Så här ser rutan ut medan ett meddelande läses upp. Ändringarna syns direkt i OBS.",
            _settings.Tts.Trigger == TtsTrigger.PowerUp ? 300 : _settings.Tts.RewardCost,
            _settings.Tts.Trigger == TtsTrigger.PowerUp ? TtsSource.PowerUp : TtsSource.Reward,
            5000);

        return pages > 0
            ? "Visas i OBS i fem sekunder."
            : "Ingen browserkälla för uppläsning är ansluten – lägg till adressen från Uppläsning-fliken i din scen.";
    }

    private void CopyTtsUrl_Click(object sender, RoutedEventArgs e)
    {
        if (TtsUrlBox.Text.Length == 0) return;
        try { Clipboard.SetText(TtsUrlBox.Text); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>Shows only the panel of the trigger in use, power-up or reward, and its refund hint.</summary>
    private void ShowTtsTrigger()
    {
        bool powerUp = _settings.Tts.Trigger == TtsTrigger.PowerUp;
        TtsPowerUpPanel.Visibility = powerUp ? Visibility.Visible : Visibility.Collapsed;
        TtsRewardPanel.Visibility = powerUp ? Visibility.Collapsed : Visibility.Visible;
        TtsRewardHint.Text = _settings.Tts.CanRefund
            ? "🔒 Skapad av appen – att neka ger tillbaka poängen."
            : _settings.Tts.RewardId.Length > 0
                ? "— Belöningen är inte skapad av appen, så poängen kan inte lämnas tillbaka härifrån."
                : "Skapa belöningen härifrån om du vill kunna refundera. Twitch låter bara en app svara på inlösen av belöningar den själv skapat.";
    }

    private void TtsTrigger_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Tts.Trigger = TtsPowerUpRadio.IsChecked == true ? TtsTrigger.PowerUp : TtsTrigger.Reward;
        ShowTtsTrigger();
        SaveSettings();
    }

    private void TtsSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        TtsSettings tts = _settings.Tts;

        tts.Enabled = TtsEnabledCheck.IsChecked == true;
        tts.PowerUpId = TtsPowerUpIdBox.Text;
        tts.RewardTitle = TtsRewardTitleBox.Text;
        tts.RequireApproval = TtsApprovalCheck.IsChecked == true;
        tts.VoiceId = TtsVoiceIdBox.Text;
        tts.ElevenLabsModel = TtsModelBox.Text;
        tts.Volume = TtsVolumeSlider.Value;
        tts.MaxCharacters = (int)Math.Round(TtsMaxCharsSlider.Value);
        tts.AnnounceName = TtsAnnounceNameCheck.IsChecked == true;
        _settings.EdgeAlerts.TtsAlert.Enabled = TtsEdgeAlertCheck.IsChecked == true;

        // A hand-typed id points the app at a reward it did not create, so the claim that it may be
        // refunded goes with it – also when the id that was edited belonged to a reward the app made.
        string rewardId = TtsRewardIdBox.Text.Trim();
        if (!string.Equals(rewardId, tts.RewardId, StringComparison.OrdinalIgnoreCase))
        {
            tts.RewardId = rewardId;
            tts.RewardManaged = false;
        }

        if (int.TryParse(TtsRewardCostBox.Text, out int cost) && cost is >= 1 and <= 10_000_000) tts.RewardCost = cost;
        if (int.TryParse(TtsTimeoutBox.Text, out int minutes) && minutes is >= 1 and <= 120) tts.ApprovalTimeoutSeconds = minutes * 60;
        if (int.TryParse(TtsQueueBox.Text, out int queue) && queue is >= 1 and <= 100) tts.QueueLimit = queue;

        tts.Normalize();
        ShowTtsTrigger();
        UpdateValueLabels();
        SaveSettings();
    }

    private void TtsRewardCost_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => TtsRewardCostBox.Text = _settings.Tts.RewardCost.ToString();

    private void TtsTimeout_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => TtsTimeoutBox.Text = Math.Max(1, _settings.Tts.ApprovalTimeoutSeconds / 60).ToString();

    private void TtsQueue_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => TtsQueueBox.Text = _settings.Tts.QueueLimit.ToString();

    private async void TtsFetchPowerUps_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.IsLoggedIn) { TtsStatusText.Text = "Logga in på Twitch först."; return; }
        if (_hub.BroadcasterId.Length == 0 || _hub.BroadcasterId != _session.UserId)
        {
            TtsStatusText.Text = "Anslut till din egen kanal först – Power-ups går bara att läsa där.";
            return;
        }

        TtsFetchPowerUpsButton.IsEnabled = false;
        TtsStatusText.Text = "Hämtar Power-ups …";
        try
        {
            IReadOnlyList<CustomPowerUp> powerUps = await _apiClient.GetCustomPowerUpsAsync(_hub.BroadcasterId);
            TtsPowerUpBox.ItemsSource = powerUps;
            TtsPowerUpBox.SelectedItem = powerUps.FirstOrDefault(powerUp =>
                string.Equals(powerUp.Id, _settings.Tts.PowerUpId, StringComparison.OrdinalIgnoreCase));
            TtsStatusText.Text = powerUps.Count > 0
                ? $"{powerUps.Count} Power-ups hämtade. Välj den som ska läsa upp meddelanden."
                : "Kanalen har inga custom Power-ups – skapa en i Twitchs Creator Dashboard först.";
        }
        catch (Exception ex) when (ex is TwitchApiException or TwitchAuthException or System.Net.Http.HttpRequestException)
        {
            TtsStatusText.Text = $"Kunde inte hämta Power-ups: {ex.Message}";
        }
        finally { TtsFetchPowerUpsButton.IsEnabled = true; }
    }

    private void TtsPowerUp_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TtsPowerUpBox.SelectedItem is not CustomPowerUp powerUp) return;
        _settings.Tts.PowerUpTitle = powerUp.Title;
        // Writing the id back runs TtsSetting_Changed, which is what saves; the extra save covers
        // picking the Power-up that was already selected, where the text never changes.
        TtsPowerUpIdBox.Text = powerUp.Id;
        TtsStatusText.Text = powerUp.RequiresInput
            ? $"Vald: {powerUp.Title} ({powerUp.Bits} bits)."
            : $"Vald: {powerUp.Title} – men den ber inte tittaren skriva något, så det kommer ingen text att läsa upp.";
        SaveSettings();
    }

    private async void TtsCreateReward_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.IsLoggedIn) { TtsStatusText.Text = "Logga in på Twitch först."; return; }
        if (!_session.HasScope(TwitchAuth.ManageRedemptionsScope))
        {
            TtsStatusText.Text = "Din inloggning är från innan återbetalning fanns – logga ut och in igen.";
            return;
        }
        if (_hub.BroadcasterId.Length == 0 || _hub.BroadcasterId != _session.UserId)
        {
            TtsStatusText.Text = "Anslut till din egen kanal först – belöningar kan bara skapas där.";
            return;
        }

        string title = TtsRewardTitleBox.Text.Trim();
        if (title.Length == 0) { TtsStatusText.Text = "Ge belöningen ett namn först."; return; }

        TtsCreateRewardButton.IsEnabled = false;
        TtsStatusText.Text = $"Skapar “{title}” i Twitch …";
        try
        {
            // RequireInput is not a choice here the way it is for the pets: a reward that asks for
            // nothing hands us nothing to read out loud.
            CustomReward created = await _apiClient.CreateCustomRewardAsync(_hub.BroadcasterId, new NewCustomReward(
                title,
                _settings.Tts.RewardCost,
                "Skriv meddelandet du vill få uppläst i sändningen.",
                RequireInput: true,
                CooldownSeconds: 0,
                BackgroundColor: "#A970FF"));

            _settings.Tts.RewardId = created.Id;
            _settings.Tts.RewardTitle = created.Title;
            _settings.Tts.RewardManaged = true;
            _settings.Tts.RewardCost = created.Cost > 0 ? created.Cost : _settings.Tts.RewardCost;
            _rewards.Remember(created);

            _loading = true;
            TtsRewardIdBox.Text = created.Id;
            TtsRewardCostBox.Text = _settings.Tts.RewardCost.ToString();
            _loading = false;

            ShowTtsTrigger();
            SaveSettingsNow();
            TtsStatusText.Text = $"“{created.Title}” skapad. Sätt bilden i Twitchs dashboard – den går inte att ladda upp via API:t.";
        }
        catch (Exception ex) when (ex is TwitchApiException or TwitchAuthException or System.Net.Http.HttpRequestException)
        {
            TtsStatusText.Text = $"Kunde inte skapa belöningen: {ex.Message} Har du redan en belöning med samma namn?";
            AppLog.Warn($"Uppläsning: kunde inte skapa belöning “{title}”: {ex.Message}");
        }
        finally { TtsCreateRewardButton.IsEnabled = true; }
    }

    private async void TtsFetchVoices_Click(object sender, RoutedEventArgs e)
    {
        string apiKey = _speechSecrets.Current.ElevenLabsApiKey;
        if (apiKey.Length == 0)
        {
            TtsVoiceHint.Text = "Spara ElevenLabs-nyckeln under Uppläsning av namn först.";
            return;
        }

        TtsFetchVoicesButton.IsEnabled = false;
        TtsVoiceHint.Text = "Hämtar röster …";
        try
        {
            IReadOnlyList<VoiceOption> voices = await _nameSpeech.GetVoicesAsync(apiKey);
            TtsVoiceBox.ItemsSource = voices;
            TtsVoiceBox.SelectedItem = voices.FirstOrDefault(voice => voice.VoiceId == _settings.Tts.VoiceId);
            TtsVoiceHint.Text = voices.Count > 0
                ? $"{voices.Count} röster hämtade. Välj en i listan."
                : "Kontot har inga röster – lägg till en i ElevenLabs först.";
        }
        catch (Exception ex) when (ex is SpeechException or System.Net.Http.HttpRequestException)
        {
            TtsVoiceHint.Text = ex.Message;
        }
        finally { TtsFetchVoicesButton.IsEnabled = true; }
    }

    private void TtsVoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TtsVoiceBox.SelectedItem is not VoiceOption voice) return;
        _settings.Tts.VoiceName = voice.Name;
        TtsVoiceHint.Text = voice.Description.Length > 0
            ? $"Vald röst: {voice.Name} ({voice.Description})"
            : $"Vald röst: {voice.Name}";
        TtsVoiceIdBox.Text = voice.VoiceId;
        SaveSettings();
    }

    private async void TtsTest_Click(object sender, RoutedEventArgs e)
    {
        TtsTestButton.IsEnabled = false;
        TtsStatusText.Text = "Läser upp …";
        try
        {
            await _tts.SpeakTestAsync(TtsTestBox.Text);
            TtsStatusText.Text = "Klart – hörde du det?";
        }
        catch (Exception ex) when (ex is SpeechException or System.Net.Http.HttpRequestException)
        {
            TtsStatusText.Text = ex.Message;
        }
        finally { TtsTestButton.IsEnabled = true; }
    }

    /// <summary>
    /// Puts an invented request in the queue so the dock's bar can be tried without a viewer
    /// spending anything. It goes through the ordinary path, so approving it reads it out, and it
    /// is never refundable: there is no redemption behind it.
    /// </summary>
    private void TtsTestBanner_Click(object sender, RoutedEventArgs e)
    {
        TtsOutcome outcome = _tts.Handle(new TtsRequest(
            "test-" + Guid.NewGuid().ToString("N")[..8],
            _settings.Tts.Trigger == TtsTrigger.PowerUp ? TtsSource.PowerUp : TtsSource.Reward,
            string.Empty,
            Refundable: false,
            "test",
            "Testtittaren",
            TtsTestBox.Text,
            _settings.Tts.Trigger == TtsTrigger.PowerUp ? 500 : _settings.Tts.RewardCost,
            DateTimeOffset.UtcNow));

        TtsStatusText.Text = outcome.Accepted
            ? _settings.Tts.RequireApproval
                ? "Testinlösen ligger nu i OBS-docken och väntar på ditt svar."
                : "Testinlösen köades – uppläsningen börjar strax."
            : $"Testinlösen gick inte igenom: {outcome.Reason}.";
    }

    private void EdgeNewTest_Click(object sender, RoutedEventArgs e)
        => _edgeAlerts.Play(_settings.EdgeAlerts.NewChatterAlert, _settings.EdgeAlerts.EdgeWidth);

    private void MaxMessages_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _overlay is null) return;
        if (!int.TryParse(MaxMessagesInput.Text, out int max) || max is < 1 or > 200) return;

        _settings.MaxMessages = max;
        ScheduleOverlayApply();
        SaveSettings();
    }

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _updatingStartWithWindows) return;

        bool previousValue = _settings.StartWithWindows;
        _settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;

        if (SyncStartWithWindows())
        {
            SaveSettings();
            return;
        }

        _updatingStartWithWindows = true;
        try
        {
            _settings.StartWithWindows = previousValue;
            StartWithWindowsCheck.IsChecked = previousValue;
        }
        finally
        {
            _updatingStartWithWindows = false;
        }

        SetStatus("Autostart kunde inte uppdateras i Windows.", true);
    }

    private bool SyncStartWithWindows()
    {
        try
        {
            return _startupRegistrySyncService.Sync(_settings.StartWithWindows, Environment.ProcessPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private void MaxMessages_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!int.TryParse(MaxMessagesInput.Text, out int max) || max is < 1 or > 200)
            MaxMessagesInput.Text = _settings.MaxMessages.ToString();
    }

    private async Task LoadBadgesAsync(string roomId)
    {
        if (_lastBadgeRoom == roomId || !_session.IsLoggedIn) return;
        _badgeLoadCancellation?.Cancel();
        _badgeLoadCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = cancellation.Token;
        _badgeLoadCancellation = cancellation;
        try
        {
            string accessToken = await _session.GetAccessTokenAsync(cancellationToken);
            await _badgeCatalog.LoadAsync(_session.ClientId, accessToken, roomId, cancellationToken);
            _lastBadgeRoom = roomId;
            _overlay.RefreshMessages();
            _hub.PublishBadgesLoaded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { SetStatus("Chat ansluten • badges kunde inte laddas: " + ex.Message, true); }
        finally
        {
            if (ReferenceEquals(_badgeLoadCancellation, cancellation)) _badgeLoadCancellation = null;
            cancellation.Dispose();
        }
    }

    private void SetConnectionButtons(bool connectedOrConnecting)
    {
        ConnectButton.IsEnabled = !connectedOrConnecting;
        DisconnectButton.IsEnabled = connectedOrConnecting;
    }

    private void SetStatus(string text, bool error = false)
    {
        // Every connect, drop and retry passes through here, so the log can tell a dead connection
        // apart from a quiet chat afterwards.
        if (error) AppLog.Warn("Chattstatus: " + text);
        else AppLog.Info("Chattstatus: " + text);
        StatusText.Text = text;
        bool live = text.StartsWith("Live", StringComparison.Ordinal);
        StatusDot.Fill = new SolidColorBrush(error ? Color.FromRgb(239, 68, 68) : live ? Color.FromRgb(34, 197, 94) : Color.FromRgb(245, 158, 11));
        _hub.PublishStatus(text, error ? "error" : live ? "live" : "busy");
    }

    private void UpdateValueLabels()
    {
        FontSizeValue.Text = $"{FontSizeSlider.Value:0} px";
        LineSpacingValue.Text = $"{LineSpacingSlider.Value:0.00}×";
        OpacityValue.Text = $"{OpacitySlider.Value:P0}";
        MessageOpacityValue.Text = $"{MessageOpacitySlider.Value:P0}";
        EdgeModIntensityValue.Text = $"{EdgeModIntensitySlider.Value:P0}";
        EdgeModDurationValue.Text = $"{EdgeModDurationSlider.Value:0} s";
        EdgeNewIntensityValue.Text = $"{EdgeNewIntensitySlider.Value:P0}";
        EdgeNewDurationValue.Text = $"{EdgeNewDurationSlider.Value:0} s";
        EdgeTtsIntensityValue.Text = $"{EdgeTtsIntensitySlider.Value:P0}";
        EdgeTtsDurationValue.Text = $"{EdgeTtsDurationSlider.Value:0} s";
        EdgeWidthValue.Text = $"{EdgeWidthSlider.Value:0} px";
        TtsVolumeValue.Text = $"{TtsVolumeSlider.Value:P0}";
        TtsMaxCharsValue.Text = $"{TtsMaxCharsSlider.Value:0}";
    }

    private void SaveSettings()
    {
        if (_loading || _closing) return;
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    /// <summary>
    /// A chatter was given a name, or lost one. Raised from whichever thread the dock request came
    /// in on. Saves the nicknames to disk at once and tells the dock and the overlay.
    /// </summary>
    private void OnNicknameChanged(Nickname entry)
    {
        try
        {
            // Saved at once, not on the settings timer: a nickname cannot be fetched again from
            // anywhere, and one typed seconds before a crash should survive it.
            _nicknameStore.Save(_nicknames);
            if (_nicknameStore.LastBackupError is { Length: > 0 } backupError)
                RunOnUi(() => SetStatus("Smeknamnet sparades, men säkerhetskopian kunde inte skapas: " + backupError, true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RunOnUi(() => SetStatus("Smeknamnet kunde inte sparas: " + ex.Message, true));
        }

        _hub.PublishNickname(entry);
        // The name is baked into the cards the overlay has already drawn, so they are rebuilt.
        RunOnUi(_overlay.RefreshMessages);
    }

    /// <summary>
    /// Takes a copy of the whole profile when the build has changed, and notes whatever reading
    /// the settings ran into. Must run between reading the settings and the first write: the
    /// snapshot has to be of the profile as the previous build left it.
    /// </summary>
    private void GuardProfile()
    {
        SettingsLoadReport report = _settingsStore.LastLoad ?? new SettingsLoadReport(false, null, new SettingsMigrationResult(0, 0, false), true);

        ProfileVersionResult version = ProfileVersionGuard.Check(_settings.LastRunVersion, AppVersion.DisplayText);

        if (version.Changed)
        {
            string from = version.PreviousVersion.Length == 0 ? "en tidigare version" : version.PreviousVersion;
            ProfileSnapshot snapshot = version.Snapshot;
            switch (snapshot.Outcome)
            {
                case ProfileSnapshotOutcome.Written:
                    AppLog.Info($"Profilen kommer från {from}; kopia av {snapshot.Copied} filer sparad: {snapshot.Path}");
                    break;
                case ProfileSnapshotOutcome.Partial:
                    AppLog.Warn($"Profilen kommer från {from}, men {snapshot.Skipped} av {snapshot.Copied + snapshot.Skipped} "
                        + $"filer gick inte att läsa. Ofullständig kopia: {snapshot.Path}. Nästa start försöker igen.");
                    break;
                case ProfileSnapshotOutcome.Failed:
                    AppLog.Warn($"Profilen kommer från {from}, men ingen kopia kunde sparas i {ProfilePaths.Snapshots}. "
                        + "Nästa start försöker igen.");
                    break;
            }

            // Only when there is nothing left to try: the stamp stops the next start from taking
            // this snapshot, so it must not be written after a copy that failed or came out short.
            if (version.MayStamp)
            {
                _settings.LastRunVersion = AppVersion.DisplayText;
                // Written at once, since a session can end without a save. Through the store, not
                // SaveSettingsNow, which reports failures on a status line that does not exist yet.
                try { _settingsStore.Save(_settings); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Error("Versionsstämpeln kunde inte sparas.", ex);
                }
            }
        }

        if (report.Migration.FromFuture)
        {
            // Nothing is lost by carrying on – the settings this build has no property for ride
            // along in AppSettings.Unknown – but it is worth saying out loud.
            _profileNotice = $"Inställningarna är skrivna av en nyare version (format {report.Migration.From}, "
                + $"den här läser {SettingsMigrations.CurrentVersion}). Det som är nyare än den här versionen lämnas orört.";
        }
        else if (report.Migration.Changed)
        {
            AppLog.Info($"Inställningsformat {report.Migration.From} → {report.Migration.To}.");
        }

        if (report.QuarantinedPath is { Length: > 0 } broken)
        {
            // The snapshot is named only when one was actually taken this start; otherwise the
            // message points at the older per-file copies.
            string alsoCopied = version.Snapshot.Path is { Length: > 0 } zip
                ? $"\n\nHela profilen kopierades till:\n{zip}"
                : $"\n\nÄldre kopior av filen kan finnas i:\n{_settingsStore.BackupFolder}";
            _profileNotice = "Inställningsfilen gick inte att läsa och ingen säkerhetskopia kunde svara heller, "
                + $"så appen startade med standardvärden.\n\nDen trasiga filen sparades som:\n{broken}{alsoCopied}";
            _profileNoticeIsLoss = true;
            AppLog.Error("Inställningarna kunde inte läsas; filen flyttades undan till " + broken, new IOException(broken));
        }
        else if (report.RecoveredFromBackup)
        {
            _profileNotice = "Inställningsfilen var trasig och återställdes från en säkerhetskopia.";
            AppLog.Warn("Inställningarna återställdes från en säkerhetskopia i " + _settingsStore.BackupFolder);
        }
    }

    /// <summary>Says what <see cref="GuardProfile"/> found, now that there is a window to say it in.</summary>
    private void ReportProfileState()
    {
        if (_profileNotice is not { Length: > 0 } notice) return;
        _profileNotice = null;

        if (_profileNoticeIsLoss)
            MessageBox.Show(this, notice, "Twitch Overlay Helper – inställningar", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            SetStatus(notice, true);
    }

    private void SaveSettingsNow()
    {
        try { _settingsStore.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_closing) SetStatus("Inställningarna kunde inte sparas: " + ex.Message, true);
        }
    }

    private void ScheduleOverlayApply()
    {
        _settingsApplyTimer.Stop();
        _settingsApplyTimer.Start();
    }

    /// <summary>
    /// Posts work to the UI thread, catching and logging any exception it throws: almost everything
    /// here is posted from a socket read loop, with no caller left to catch it. Does nothing once
    /// the window is closing.
    /// </summary>
    private void RunOnUi(Action action)
    {
        if (_closing || Dispatcher.HasShutdownStarted) return;
        Action guarded = () =>
        {
            try { action(); }
            catch (Exception ex) { AppLog.Error("Fel i en åtgärd på UI-tråden.", ex); }
        };
        _ = Dispatcher.BeginInvoke(guarded, DispatcherPriority.Background);
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _closing = true;
        _trayIcon.Visible = false;
        _chatFlushTimer.Stop();
        _settingsApplyTimer.Stop();
        _settingsSaveTimer.Stop();
        _historySaveTimer.Stop();
        _badgeLoadCancellation?.Cancel();
        _badgeLoadCancellation?.Dispose();
        _badgeLoadCancellation = null;
        // Anything still waiting on a verdict is left in Twitch's queue: the streamer can refund it
        // there, and the next start sweeps the rest.
        // The bot goes first: a verdict raised on the way out would otherwise be queued for a chat
        // connection that is being torn down.
        _bot.Dispose();
        // The readings go before the ledger they settle through. Reset marks them abandoned without
        // answering, and cuts off the sentence being read.
        _tts.Reset();
        _tts.Dispose();
        // Before the ledger like the readings: a gift timing out on the way down raises a verdict
        // the ledger must still be alive to hold.
        _spins.Reset();
        _spins.Dispose();
        _ledger.Reset();
        _ledger.Dispose();
        SaveSettingsNow();
        // The last twenty seconds of chat would otherwise be the one gap a clean shutdown leaves.
        SaveChatHistory();
        GlobalHotkeys.Unregister(this, ToggleHotkeyId);
        GlobalHotkeys.Unregister(this, EditHotkeyId);
        _hwndSource?.RemoveHook(WndProc);
        _overlay.Close();
        _edgeAlerts.Close();
        _audioPlayer.Close();
        await _dockServer.DisposeAsync();
        // The sender before the account it writes through, so a line still in the queue cannot be
        // handed to a chat connection that has already gone.
        await _botSender.DisposeAsync();
        await _botAccount.DisposeAsync();
        await _chatClient.DisposeAsync();
        await _eventSubClient.DisposeAsync();
        _session.Dispose();
        _httpClient.Dispose();
        _speechHttpClient.Dispose();
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        Application.Current.Shutdown();
    }

    private static string? SelectedText(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString();
    private static void SelectComboByText(ComboBox box, string text)
    {
        foreach (object item in box.Items)
            if (item is ComboBoxItem option && string.Equals(option.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase)) { box.SelectedItem = option; return; }
        box.SelectedIndex = 0;
    }
}
