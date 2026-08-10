using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TwitchOverlayHelper.Diagnostics;
using TwitchOverlayHelper.History;
using TwitchOverlayHelper.Interop;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Nicknames;
using TwitchOverlayHelper.Overlay;
using TwitchOverlayHelper.Pets;
using TwitchOverlayHelper.Services;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Speech;
using TwitchOverlayHelper.Twitch;
using TwitchOverlayHelper.Updates;
using TwitchOverlayHelper.Web;

namespace TwitchOverlayHelper;

public partial class MainWindow : Window
{
    private const int ToggleHotkeyId = 9001;
    private const int EditHotkeyId = 9002;

    private readonly SettingsStore _settingsStore = new();
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
    /// Nicknames are the one thing in the app the user typed by hand and cannot get back from
    /// Twitch, so they get a file of their own and are written the moment they change – with a
    /// dated copy kept beside every save.
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
    /// The lines said before we connected. The only thing in the app that asks anyone other than
    /// Twitch, because Twitch has nothing to ask – see <see cref="RecentMessagesClient"/>.
    /// </summary>
    private readonly RecentMessagesClient _recentMessages;
    /// <summary>
    /// What we may send in this channel. Asked for once when the room becomes known, because it is
    /// needed the moment a line is written rather than the moment somebody opens the picker.
    /// </summary>
    private readonly UsableEmoteCatalog _emotes;
    private readonly TwitchEventSubClient _eventSubClient;
    private readonly RewardCatalog _rewards = new();
    private readonly PowerUpTracker _powerUps = new();
    /// <summary>
    /// Which hype train moments have already been given a card. The strip can refuse an update for
    /// being out of order and still owe the overlay its card – a begin arriving after the progress
    /// it started is late, not wrong – so the two are kept apart, and the card is deduplicated on
    /// the one thing that identifies the moment rather than on whether the strip moved.
    /// </summary>
    private readonly RecentMessageIds _hypeCards = new();
    /// <summary>
    /// Keeps a chat line and the power-up marker for it in that order. The two are raised on
    /// different threads – IRC and EventSub – and the moment the tracker knows about a line, the
    /// marked copy can go out. If it overtook the line itself, the views would be asked to update a
    /// message they had never been given.
    /// </summary>
    private readonly System.Threading.Lock _publishGate = new();
    private readonly SpeechSecretStore _speechSecrets = new();
    private readonly NameAudioPlayer _namePlayer;
    private readonly NameSpeechService _nameSpeech;
    private readonly ChatHub _hub;
    private readonly PetRegistry _petRegistry = new();
    private readonly PetCatalog _petCatalog = new();
    private readonly PetService _petService;

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
        _namePlayer = new NameAudioPlayer(Dispatcher);
        _nameSpeech = new NameSpeechService(_speechHttpClient, _settings, _speechSecrets, _namePlayer.PlayAsync);
        _hub = new ChatHub(_settings, _badgeCatalog, _session, _petRegistry, _petCatalog, _nicknames) { SpeechEnabled = _nameSpeech.IsConfigured };
        _petService = new PetService(_settings, _petCatalog, _petRegistry, _hub);
        // The broadcaster is read at call time rather than captured: the app can be pointed at
        // another channel while it runs, and a refund aimed at the channel we have left is refused.
        _ledger = new RedemptionLedger(
            new TwitchRedemptionGateway(_apiClient, () => _hub.BroadcasterId),
            _petRegistry,
            _hub.PublishPetRemoved);
        _dockContext = new DockServerContext
        {
            Settings = _settings,
            Hub = _hub,
            Session = _session,
            Api = _apiClient,
            Chat = _chatClient,
            Speech = _nameSpeech,
            Pets = _petCatalog,
            Nicknames = _nicknames,
            Emotes = _emotes
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
        // Written on a timer rather than per message: chat arrives in bursts, and a file write per
        // line during a raid would be the app's busiest piece of disk work for no gain.
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
        // The queue can also be worked from Twitch's own dashboard, and a pet whose redemption was
        // refunded there has to come down here too.
        _eventSubClient.RedemptionUpdated += change => _ledger.HandleExternalUpdate(change.RedemptionId, change.Status);
        // The two signals that say whether a pet is being seen at all: a lawn reporting what it has
        // drawn, and whether any lawn is connected in the first place.
        _hub.PetShown += _ledger.MarkShown;
        _hub.PetOverlayCountChanged += _ledger.OverlayCountChanged;
        // A full lawn pushes the oldest pet home to fit a new one, and that pet may be one somebody
        // paid for. The chat route and the test button both do it; a refundable reward refuses
        // instead, so it is only ever on the receiving end.
        _petService.PetEvicted += _ledger.PetEvicted;
        _ledger.Answered += notice => RunOnUi(() => ShowRedemptionNotice(notice));
        // Nothing is connected yet, and the ledger has to start out knowing that rather than
        // assuming a lawn it has never seen.
        _ledger.OverlayCountChanged(_hub.PetOverlayCount);
        _eventSubClient.GigantifyReceived += OnGigantify;
        _eventSubClient.HypeTrainChanged += OnHypeTrain;
        _eventSubClient.StatusChanged += status => RunOnUi(() => SetEventStatus(status));
        _eventSubClient.CoverageChanged += coverage => RunOnUi(() => ApplyEventCoverage(coverage));
        _chatClient.StatusChanged += status => RunOnUi(() => SetStatus(status));
        _chatClient.RoomDiscovered += room => RunOnUi(() => _ = PrepareRoomAsync(room));
        _chatClient.ConnectionStopped += () => RunOnUi(() => SetConnectionButtons(false));
        _session.StateChanged += () => RunOnUi(UpdateLoginUi);

        UpdateLoginUi();
        UpdateLoginButtonState();
        ApplySpeechConfiguration();
        RestoreChatHistory();
        _ = StartDockServerAsync();
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    /// <summary>
    /// Puts back what was on screen last time, or shows the sample lines when there is nothing worth
    /// putting back. Twitch cannot help here – it sends no history on join – so this is limited to
    /// the lines this app saw itself, from this channel, within
    /// <see cref="ChatHistoryStore.MaxAge"/>.
    ///
    /// <para>The restored lines go straight into the two views and never through
    /// <see cref="OnChatMessage"/>. That is the whole point: replaying them through the normal path
    /// would spawn yesterday's pets again, read the names out loud again, welcome the same first-time
    /// chatters again and light the edges for a "!psst" that was answered hours ago.</para>
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
    /// is already on screen. This is the only part of the chat that does not come from Twitch, and
    /// the only one that can be switched off.
    ///
    /// <para>Once per channel per run: it answers with the same stretch of chat every time, so
    /// fetching again on each reconnect would cost a request to redraw the same lines.</para>
    ///
    /// <para>Like the saved history, nothing fetched here goes through <see cref="OnChatMessage"/> –
    /// these lines were already said, already answered, and already reacted to.</para>
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
        // The request outlived the channel it was about. Lines from a room we have left would go to
        // the overlay, the dock and – on the next tick – the history file, all under the new
        // channel's name, and the disk copy would still be there tomorrow.
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
    /// A message was deleted, somebody was timed out or banned, or a moderator cleared the room –
    /// including the broadcaster typing /clear in their own chat, which is what Twitch turns into a
    /// CLEARCHAT for everyone listening.
    ///
    /// <para>Three surfaces hold chat and each has to be told. The hub drops the lines from its
    /// timeline, which is what takes them off the dock and the stream overlay; the overlay window
    /// keeps its own cards and is told separately; and the file is written at once rather than at
    /// the next tick, because a restart in the twenty seconds in between is exactly when a cleared
    /// room would come back as though nothing had happened.</para>
    /// </summary>
    private void OnModeration(ChatModerationEvent moderation)
    {
        _hub.PublishModeration(moderation);
        RunOnUi(() =>
        {
            // A line the action reached may still be sitting in the queue waiting for a card. Drawing
            // it a moment after it was deleted is the one thing this must not do, and a single flush
            // takes fifty at a time – the line we are after can be further back than that.
            DrainPendingMessages();
            _overlay.ApplyModeration(moderation);
            SaveChatHistory();
        });
    }

    /// <summary>
    /// An earlier sitting was put away from the dock. The overlay keeps its own cards rather than
    /// reading the hub's timeline, so it is redrawn from what is left; the file is written at once
    /// rather than at the next tick, because a restart in the twenty seconds in between is exactly
    /// when it would look as though nothing had been put away at all.
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
    /// Takes the lines a redraw of the overlay is about to draw anyway out of the queue of lines
    /// waiting to be drawn on it. Without this the two accounts are added together: a line that is
    /// both in the timeline being drawn and still in the queue gets a card from the redraw and
    /// another one on the next flush.
    ///
    /// <para>Matched on identity rather than emptied wholesale, because not everything in this queue
    /// is in the hub's timeline. A hype train card is drawn on the overlay alone – the dock is handed
    /// the state and draws a strip instead – so throwing the queue away would lose it for good.</para>
    ///
    /// <para>Every line in the timeline was queued for the overlay before it was published to the hub,
    /// so anything the redraw covers is already in the queue by the time this runs. A line the other
    /// way around – queued, not yet published – is simply not in the redraw, stays in the queue, and
    /// is drawn after it. Which is what makes this safe without a lock.</para>
    /// </summary>
    private void DropPendingAlreadyDrawn(IReadOnlyList<ChatTimelineItem> drawn)
    {
        HashSet<string> ids = new(drawn.Select(ChatHistoryMerge.IdOf).OfType<string>(), StringComparer.Ordinal);
        List<ChatTimelineItem> keep = [];
        while (_pendingMessages.TryDequeue(out ChatTimelineItem item))
        {
            Interlocked.Decrement(ref _pendingMessageCount);
            if (ChatHistoryMerge.IdOf(item) is { } id && ids.Contains(id)) continue;
            keep.Add(item);
        }
        // Back on in the order they arrived; none of them has been drawn, so the flush timer still
        // has work to do and Queue is what keeps it running.
        foreach (ChatTimelineItem item in keep) Queue(item);
    }

    /// <summary>
    /// Writes the history to disk when it has actually changed. The version check is what keeps a
    /// quiet chat – or a minimised app nobody is talking in – from rewriting the same file every
    /// twenty seconds all evening.
    /// </summary>
    private void SaveChatHistory()
    {
        if (_settings.Channel.Length == 0) return;
        long version = _hub.HistoryVersion;
        if (version == _lastSavedHistoryVersion) return;
        // Only a write that happened counts as saved. Marking it either way turns a file locked for
        // one second – a backup tool, a virus scanner – into a history that is never written again
        // until the next line arrives, and the version check then hides that it ever went wrong.
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
        _settings.Pets.Scale = PetScaleSlider.Value;
        PetScaleValue.Text = $"{PetScaleSlider.Value:P0}";
        _hub.PublishPetSettings();
        // Switching pets off hides the whole lawn, and the creatures on it go on living out their
        // time behind it. Anyone who paid for one of them is now paying for something nobody can
        // see, so they get their points back rather than a redemption booked as delivered.
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
    /// Creates the row's reward on Twitch, which is the only thing that makes its redemptions
    /// refundable later: Twitch lets an app answer a redemption only on a reward its own client id
    /// made. A reward set up by hand in the dashboard cannot be adopted – there is no API for it –
    /// so moving an existing pet reward over means creating a new one here and retiring the old one.
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
            // The rows read their state once when they are drawn, so the list is built again to let
            // the new lock show up next to the reward that just became refundable.
            RefreshPetRewardList();
            PetRewardStatusText.Text = $"“{created.Title}” skapad. Sätt bilden i Twitchs dashboard – den går inte att ladda upp via API:t.";
        }
        catch (Exception ex) when (ex is TwitchApiException or TwitchAuthException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            // The one worth spelling out: Twitch refuses a second reward with a title the channel
            // already uses, which is exactly what a streamer moving off a hand-made reward hits.
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

    /// <summary>
    /// The table's two halves, which are only ever right together: column headings standing over an
    /// empty list are headings for nothing, and the line explaining that the list is empty has no
    /// business under a list that is not.
    /// </summary>
    private void ShowPetRewardTable()
    {
        bool empty = _petRewards.Count == 0;
        PetRewardHeader.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        PetRewardEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Draws the rows again. The rules are plain settings objects with no change notification, so
    /// the one thing that changes without the user typing it – a row becoming refundable – needs
    /// the list rebuilt to show up.
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

    private void ReloadPets_Click(object sender, RoutedEventArgs e)
    {
        _petCatalog.Reload();
        RefreshPetCatalogUi();
        // Overlays that are open right now should learn the new species without a reload in OBS.
        _hub.PublishPetCatalog();
    }

    /// <summary>Rebuilds the species list and the default-pet picker from the catalog.</summary>
    private void RefreshPetCatalogUi()
    {
        _refreshingPetCatalog = true;
        try
        {
            var lines = _petCatalog.Pets.Select(pet =>
            {
                string names = string.Join(", ", new[] { pet.Id }.Concat(pet.Aliases));
                return $"• {pet.Name}{(pet.IsDefault ? "" : " (egen)")} – tittare skriver: {names}";
            });
            IEnumerable<string> warnings = _petCatalog.Warnings.Select(w => $"⚠ {w}");
            PetListText.Text = string.Join("\n", lines.Concat(warnings));

            PetDefaultBox.Items.Clear();
            PetDefaultBox.Items.Add(new ComboBoxItem { Content = "Slumpad – låt texten avgöra", Tag = "" });
            foreach (PetDefinition pet in _petCatalog.Pets)
                PetDefaultBox.Items.Add(new ComboBoxItem { Content = pet.Name, Tag = pet.Id });

            string current = _settings.Pets.DefaultPet;
            PetDefaultBox.SelectedIndex = 0;
            foreach (ComboBoxItem item in PetDefaultBox.Items)
                if (string.Equals(item.Tag as string, current, StringComparison.OrdinalIgnoreCase) && current.Length > 0)
                    PetDefaultBox.SelectedItem = item;
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

    /// <summary>
    /// Shows the reward by name when we have one. A GUID is not something anyone recognises, so a
    /// name is the difference between a list the streamer can read and one they have to decode.
    /// </summary>
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
            // Checked here so an upgrade to an authenticated connection is not attempted when no
            // token can be had; the connection itself then asks again per attempt, and the answer
            // is cached, so this costs nothing extra.
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
    /// <see cref="OnRoomDiscoveredAsync"/> with a net under it. This runs on every connect *and every
    /// reconnect*, and it calls Twitch over the network several times – so a stalled request during a
    /// network blip is an ordinary Tuesday, not an exceptional case. It used to be started from an
    /// async void lambda, which means an exception here had nowhere to go but the dispatcher, and the
    /// process ended mid-stream with nothing written down anywhere.
    ///
    /// <para>Everything it sets up is optional: badges, reward names, the emote picker, the extra
    /// events. Losing them costs a little polish, and is never worth losing the chat over.</para>
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
        // We are in the room, so the preview lines have had their moment. Taken down here rather
        // than by the first real message: in a quiet room that message can be twenty minutes away,
        // and until it came the stream overlay was showing invented chat to actual viewers.
        _hub.ClearSamples();
        // First, so the chat reads on from where it left off as early as possible – and before the
        // slower Twitch calls below have a chance to hold it up.
        await BackfillRecentMessagesAsync(_settings.Channel);
        // Which channel we are in decides what EventSub can carry, so it can only start once the
        // room id is known – not at login, and not when the socket opens.
        await RestartEventSubAsync(roomId);
        await LoadBadgesAsync(roomId);
        await LoadUsableEmotesAsync(roomId);
    }

    /// <summary>
    /// Which emotes we may send here. Fetched when the room becomes known rather than when the
    /// picker is opened, because the other thing it is for happens without warning: the first line
    /// the streamer writes needs its emotes resolved before it reaches the overlay.
    /// </summary>
    private async Task LoadUsableEmotesAsync(string roomId)
    {
        // Another channel's emotes drawn onto our own lines would be worse than none at all.
        _emotes.Forget();
        if (!_session.IsLoggedIn) return;
        // TaskCanceledException belongs in this list: that is what HttpClient throws when a call runs
        // past its 15 second timeout, and a timeout is the most ordinary way for any of this to fail.
        try { await _emotes.GetAsync(roomId, _session.UserId); }
        catch (Exception ex) when (ex is TwitchApiException or TwitchAuthException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            // Not worth surfacing: without the list our own lines read as they were typed, which is
            // exactly how they read before any of this existed. The dock says so where it matters.
        }
    }

    /// <summary>
    /// Points EventSub at a channel. Everything here is allowed to come up empty: logged out, a
    /// login from before these scopes existed, or simply someone else's channel all end with the
    /// extra events switched off and the chat reading exactly as it did before.
    /// </summary>
    private async Task RestartEventSubAsync(string broadcasterId)
    {
        // A reconnect or a channel switch can start this again while an earlier run is still waiting
        // on Twitch. Only the newest run may finish: two of them reaching StartAsync would throw
        // from an async void callback, which takes the whole app with it. Read and written on the
        // UI thread only, so a plain counter is enough.
        int generation = ++_eventSubGeneration;

        await _eventSubClient.StopAsync();
        // Another channel's reward names would be wrong here, and a stale name is worse than none.
        _rewards.Clear();
        _powerUps.Clear();
        // From here on nothing can tell us the train ended, so the strip has to go even when the
        // channel is the same one. A live train writes itself back on the next progress; a train
        // that ended during the gap would otherwise sit there claiming to be running.
        _hub.ClearHypeTrain();
        _hypeCards.Clear();
        _petService.RedemptionsFromEventSub = false;
        // Whatever brought us here, redemptions stopped being delivered for a stretch, so the queue
        // has to be read again on the way back.
        _swept = false;

        // Pending redemptions belong to the channel they were made in, so a switch lets them go –
        // answering them from another channel is refused by Twitch and would be wrong even if it
        // were not. A plain reconnect must not, though: this method also runs when IRC drops and
        // comes back mid-stream, and throwing away the pets living through that gap would leave
        // their viewers refunded for something they watched.
        if (!string.Equals(_ledgerChannel, broadcasterId, StringComparison.Ordinal))
        {
            _ledger.Reset();
            _ledgerChannel = broadcasterId;
            // Another channel's pets have no business on this one's lawn, and the entries that
            // vouched for them have just gone. Letting them go is not the same as settling them:
            // they stay unfulfilled in the channel they were made in, and coming back there finds
            // them sitting from before we were listening and pays them back.
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

        // Belt and braces: the guard above is the real fix, but a crash here would cost the user
        // their whole session over an optional feature.
        try { await _eventSubClient.StartAsync(broadcasterId); }
        catch (InvalidOperationException) { }
        // The sweep is not started here. StartAsync only kicks off the background task – the socket
        // is not open and nothing is subscribed yet, so anything redeemed in that gap would be
        // invisible to EventSub and too new for a sweep cut at this moment. It waits for coverage
        // to confirm the subscription instead; see ApplyEventCoverage.
    }

    /// <summary>
    /// Pays back every redemption of one of our rewards that we were not listening for.
    ///
    /// <para>The pets only ever live in memory, so a redemption still unfulfilled on an app-made
    /// reward from before we subscribed bought a pet that nobody can be watching now. A clean exit,
    /// a crash mid-stream, a spell in another channel and the seconds it takes the socket to come
    /// up all look the same from here, and the viewer is owed their points in every one of them.
    /// </para>
    /// </summary>
    /// <param name="listeningSince">
    /// The moment EventSub confirmed the subscription. Everything redeemed before it is ours to pay
    /// back; everything after arrives through the normal path. Cut here rather than at process
    /// start on purpose – that left the connection gap belonging to neither.
    /// </param>
    /// <summary>Runs the sweep and lets the next one in afterwards, however this one ended.</summary>
    private async Task SweepThenReleaseAsync(DateTimeOffset listeningSince, int generation)
    {
        try { await SweepLeftoverRedemptionsAsync(listeningSince, generation); }
        finally { _sweeping = false; }
    }

    private async Task SweepLeftoverRedemptionsAsync(DateTimeOffset listeningSince, int generation)
    {
        IReadOnlyList<string> managed = _settings.Pets.ManagedRewardIds;
        if (managed.Count == 0)
        {
            _swept = true;
            return;
        }

        bool complete;
        try { complete = await _ledger.SweepAsync(managed, listeningSince); }
        catch (Exception ex)
        {
            AppLog.Error("Pets: kunde inte städa kön från förra körningen", ex);
            complete = false;
        }
        if (generation != _eventSubGeneration) return;

        // Only a sweep that actually read every queue may stop the next connection from trying
        // again. Marking it done up front is how a single failed call leaves a viewer's points
        // sitting in Twitch's queue for the rest of the stream.
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
    /// Says what the extra events are doing and, when something is off because the stored login
    /// predates a scope, offers the one thing that fixes it. A missing permission is never an error
    /// here – it is a sentence explaining why a card is not showing up.
    /// </summary>
    private void ApplyEventCoverage(EventSubCoverage coverage)
    {
        _petService.RedemptionsFromEventSub = coverage.Redemptions;

        // This is the first moment we know redemptions are actually being delivered, which makes it
        // the only honest place to cut the sweep: everything redeemed before now was redeemed while
        // nobody was listening.
        //
        // Not listening is exactly what arms it again. Every gap – a reconnect, a revoked
        // subscription, a channel switch – is a stretch where a redemption could arrive with nobody
        // to hear it, so the queue has to be looked at once more on the way back. The flag is only
        // set by a sweep that finished, so a failed one comes round again too.
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

        // The emote picker's personal half is not an event, so it stays out of the headline – but it
        // is behind the very same "log in again", and a scope nobody is told about is a feature that
        // silently never turns on. Appended to the sentence rather than given one of its own,
        // because the button under it can only be pressed once for all of them.
        var missing = coverage.MissingScopes.ToList();
        if (_session.IsLoggedIn && !_session.HasScope(TwitchAuth.EmotesScope)) missing.Add(TwitchAuth.EmotesScope);
        bool offerLogin = _session.IsLoggedIn && missing.Count > 0;

        EventStatusText.Text = offerLogin
            ? $"{headline} Logga in igen för att slå på {string.Join(" och ", missing.Select(TwitchAuth.DescribeScope))}."
            : headline;
        ReauthorizeButton.Visibility = offerLogin ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// New scopes only arrive through a fresh approval: refreshing a token hands back the scopes it
    /// was granted, never the ones we have started asking for since.
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
        // Reachable from the tray on purpose: the log is worth having exactly when the main window is
        // not what the user is looking at, and asking someone to find %LOCALAPPDATA% over voice chat
        // is how a log nobody reads stays unread.
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
    /// Ends the app the ordinary way while the updater waits for this process to exit. It has to be the
    /// ordinary way: settings and the last minutes of chat are written during shutdown, and an update
    /// that costs the user those is not an improvement.
    /// </summary>
    internal void ExitForUpdate() => ExitApplication();

    /// <summary>
    /// Everything one chat line sets off. The reward name is put on first, so the overlay, the dock
    /// and the pet rules all see the same message rather than three versions of it.
    /// </summary>
    private void OnChatMessage(ChatMessage message)
    {
        message = _rewards.Enrich(message);
        // Marking and publishing are one step, see _publishGate.
        lock (_publishGate)
        {
            message = _powerUps.Enrich(message);
            Queue(ChatTimelineItem.Of(message));
            _hub.PublishMessage(message);
        }
        _petService.HandleMessage(message);
        TrackLastReward(message);
        // The edge glow. A mod calling outranks the welcome: when a moderator's very first message
        // is the command, the streamer is being called, not introduced to their own mod.
        if (_settings.EdgeAlerts.TriggersModAlert(message)) RaiseEdgeAlert(EdgeAlertKind.ModCall);
        else
        {
            ExplainMissedModCall(message);
            if (_settings.EdgeAlerts.TriggersNewChatterAlert(message)) RaiseEdgeAlert(EdgeAlertKind.NewChatter);
        }
    }

    /// <summary>
    /// Writes down why a line that *was* the call command still did not light the edges. There are
    /// only ever two answers – the alert is switched off, or the sender is not a moderator – and
    /// neither of them shows up anywhere on screen, which is what makes "I wrote it and nothing
    /// happened" impossible to tell apart from a broken feature.
    ///
    /// <para>Silent for anything that is not the command, so ordinary chat costs one comparison.</para>
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
        string reason = !edge.ModAlert.Enabled
            ? "mod-ljuset är avstängt i inställningarna"
            : "avsändaren är varken moderator eller broadcaster";
        // The mod tag is what the decision actually rests on, so it is written down next to the
        // badges: badges alone once made a lead moderator look like an ordinary viewer here.
        string modTag = message.HasModTag ? "ja" : "nej";
        AppLog.Info($"\"{text}\" från {message.DisplayName} tände inte kanterna: {reason}. Mod-tagg: {modTag}. Badges: {badges}.");
    }

    /// <summary>
    /// Lights the edges, unless <see cref="_edgeScheduler"/> says this one has nothing to add to
    /// what is already lit. Deciding here rather than in the window keeps the policy testable and
    /// leaves the settings window's test buttons free to preview a glow whatever chat is doing.
    /// </summary>
    private void RaiseEdgeAlert(EdgeAlertKind kind)
    {
        EdgeAlertStyle style = kind == EdgeAlertKind.ModCall
            ? _settings.EdgeAlerts.ModAlert
            : _settings.EdgeAlerts.NewChatterAlert;
        // The length comes from the scheduler, not from the setting: a glow being held open has only
        // the rest of its ceiling to run, and playing a full alert on top of it is what would keep
        // the edges lit past the limit during a busy chat.
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
    }

    /// <summary>
    /// A step in a hype train. The two views want different things from it, which is why this does
    /// not go through <see cref="OnChatEvent"/>: the dock is handed the whole state and draws a strip
    /// that stays put for the minutes the train lasts, while the overlay – which has nowhere to put
    /// a strip – gets a card for the start and one for the end and nothing in between. A bar
    /// redrawing every few seconds on top of a game is the opposite of what the overlay is for.
    /// </summary>
    private void OnHypeTrain(HypeTrainState train)
    {
        _hub.PublishHypeTrain(train);
        // The card's id names the train and which of its two moments this is, so a moment that has
        // already been carded cannot be carded again – whatever order Twitch delivers in.
        if (train.ToChatEvent() is { } card && _hypeCards.IsNew(card.Id)) Queue(ChatTimelineItem.Of(card));
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

        PetRedemptionResult result = _petService.HandleRedemption(redemption);
        AnswerRedemption(redemption, result);
        RunOnUi(() => ShowLastReward(redemption.RewardId, redemption.RewardTitle, redemption.DisplayName));
    }

    /// <summary>
    /// Tells Twitch what became of a redemption – but only for the rewards this app created, which
    /// are the only ones it may answer at all.
    ///
    /// <para>A pet that did spawn is not answered yet. It goes into the ledger and stays in the
    /// channel's queue until it has lived its time, because that is the only window in which it can
    /// still be paid back: an OBS browser source that never came up accepts the frame and draws
    /// nothing, and marking the purchase done at spawn would close the door before anyone could
    /// notice. Everything that failed outright is refunded on the spot.</para>
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
            _ => null
        };
        if (reason is null) return;
        _ = _ledger.RefundNow(redemption.Id, redemption.RewardId, viewer, cost, reason);
    }

    /// <summary>Says what the ledger just did, so a refund is not something the streamer finds out about later.</summary>
    private void ShowRedemptionNotice(RedemptionNotice notice) =>
        PetRewardStatusText.Text = notice.Refunded
            ? $"↩ {notice.ViewerName} fick tillbaka {notice.Cost} poäng – {notice.Reason}."
            : $"✓ {notice.ViewerName}s pet levde klart.";

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
            // The line may still be sitting in the pending queue, and there is no card to update
            // until it has been drawn. Emptied rather than flushed once: a flush takes fifty at a
            // time, and the line we are after can be further back than that.
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
    /// Empties the pending queue instead of taking the usual batch off it. Bounded all the same:
    /// the queue itself is capped at 500, so twenty passes is twice what it can ever hold – and a
    /// chat writing faster than this drains must not be able to hold the UI thread here.
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
        PetScaleSlider.Value = _settings.Pets.Scale;
        PetScaleValue.Text = $"{_settings.Pets.Scale:P0}";
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
        EdgeModEnabledCheck.IsChecked = _settings.EdgeAlerts.ModAlert.Enabled;
        EdgeModCommandBox.Text = _settings.EdgeAlerts.ModCommand;
        SelectColor(EdgeModColorBox, _settings.EdgeAlerts.ModAlert.Color);
        EdgeModIntensitySlider.Value = _settings.EdgeAlerts.ModAlert.Intensity;
        EdgeModDurationSlider.Value = _settings.EdgeAlerts.ModAlert.DurationSeconds;
        EdgeNewEnabledCheck.IsChecked = _settings.EdgeAlerts.NewChatterAlert.Enabled;
        SelectColor(EdgeNewColorBox, _settings.EdgeAlerts.NewChatterAlert.Color);
        EdgeNewIntensitySlider.Value = _settings.EdgeAlerts.NewChatterAlert.Intensity;
        EdgeNewDurationSlider.Value = _settings.EdgeAlerts.NewChatterAlert.DurationSeconds;
        EdgeWidthSlider.Value = _settings.EdgeAlerts.EdgeWidth;
        UpdateValueLabels();
    }

    /// <summary>The colours the edge glow can have. A handful of clear names beats a colour picker here.</summary>
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
        // The previous streamer's subscriber icons go with them. Dropped here rather than when the
        // new set arrives, because the new set may never arrive at all: logged out, Twitch hands out
        // no badges, and the alternative to a plain "SUB" would be someone else's picture.
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
        }
        catch (Exception ex) { SetConnectionButtons(false); SetStatus(ex.Message, true); }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await _chatClient.DisconnectAsync();
        await _eventSubClient.StopAsync();
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
        edge.EdgeWidth = EdgeWidthSlider.Value;
        UpdateValueLabels();
        SaveSettings();
    }

    /// <summary>
    /// Switching it on mid-session does not fetch anything by itself – the fetch belongs to a
    /// connection, and there is nothing to weave into a chat that is already running. It takes
    /// effect on the next connect, which is also when the channel name would be sent.
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

    /// <summary>Shows what will actually count – "psst hej" became "!psst" the moment it was typed.</summary>
    private void EdgeModCommand_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => EdgeModCommandBox.Text = _settings.EdgeAlerts.ModCommand;

    // The test buttons play even when the alert is switched off, so the look can be tuned first
    // and the switch flipped after.
    private void EdgeModTest_Click(object sender, RoutedEventArgs e)
        => _edgeAlerts.Play(_settings.EdgeAlerts.ModAlert, _settings.EdgeAlerts.EdgeWidth);

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
        // Every connect, drop and retry passes through here, so the log gets the chat's whole story
        // with timestamps – which is what tells a dead connection apart from a quiet chat afterwards.
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
        EdgeWidthValue.Text = $"{EdgeWidthSlider.Value:0} px";
    }

    private void SaveSettings()
    {
        if (_loading || _closing) return;
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    /// <summary>
    /// A chatter was given a name, or lost one. Raised from whichever thread the dock request came
    /// in on, so everything here either takes its own lock or is hopped onto the UI thread.
    ///
    /// Written to disk immediately rather than on the settings timer: this is the one piece of data
    /// in the app that cannot be fetched again from anywhere, and a name typed seconds before a
    /// crash should still be there afterwards. The store keeps a dated copy of every save, so a
    /// nickname that is overwritten by mistake is recoverable as well.
    /// </summary>
    private void OnNicknameChanged(Nickname entry)
    {
        try
        {
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
    /// Hands work to the UI thread. The guard is not decoration: almost everything here is posted
    /// from a socket read loop, and an exception in one of these callbacks lands on the dispatcher
    /// with no caller left to catch it – which used to end the process without a word.
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
        // Anything still waiting on a verdict is left in Twitch's queue rather than answered from a
        // window that is disappearing. The streamer can refund it there, and the next start sweeps
        // whatever they did not.
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
        _namePlayer.Close();
        await _dockServer.DisposeAsync();
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
