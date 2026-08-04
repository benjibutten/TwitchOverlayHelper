using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TwitchOverlayHelper.Interop;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Overlay;
using TwitchOverlayHelper.Services;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Speech;
using TwitchOverlayHelper.Twitch;
using TwitchOverlayHelper.Web;

namespace TwitchOverlayHelper;

public partial class MainWindow : Window
{
    private const int ToggleHotkeyId = 9001;
    private const int EditHotkeyId = 9002;

    private readonly SettingsStore _settingsStore = new();
    private readonly TwitchBadgeCatalog _badgeCatalog = new();
    private readonly TwitchChatClient _chatClient = new();
    private readonly StartupRegistrySyncService _startupRegistrySyncService = new();
    private readonly System.Net.Http.HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    // Its own client: generating a voice clip is slower than any Twitch call, and a name that
    // takes a few seconds to come back must not push the Twitch timeout up for everything else.
    private readonly System.Net.Http.HttpClient _speechHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly TwitchSession _session;
    private readonly TwitchApiClient _apiClient;
    private readonly SpeechSecretStore _speechSecrets = new();
    private readonly NameAudioPlayer _namePlayer;
    private readonly NameSpeechService _nameSpeech;
    private readonly ChatHub _hub;
    private readonly DockServer _dockServer;
    private readonly DockServerContext _dockContext;
    private readonly AppSettings _settings;
    private readonly OverlayWindow _overlay;
    private readonly ConcurrentQueue<ChatMessage> _pendingMessages = new();
    private readonly DispatcherTimer _chatFlushTimer;
    private readonly DispatcherTimer _settingsApplyTimer;
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private HwndSource? _hwndSource;
    private Button? _recordingButton;
    private bool _loading = true;
    private bool _editing;
    private bool _closing;
    private bool _exitRequested;
    private bool _updatingStartWithWindows;
    private bool _reconnecting;
    private DockSettingsWindow? _dockSettingsWindow;
    private SpeechSettingsWindow? _speechSettingsWindow;
    private string? _lastBadgeRoom;
    private CancellationTokenSource? _badgeLoadCancellation;
    private int _pendingMessageCount;
    private int _chatTimerRequested;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = AppVersion.DisplayText;
        _settings = _settingsStore.Load();
        SyncStartWithWindows();
        _session = new TwitchSession(_httpClient);
        _apiClient = new TwitchApiClient(_httpClient, _session);
        _namePlayer = new NameAudioPlayer(Dispatcher);
        _nameSpeech = new NameSpeechService(_speechHttpClient, _settings, _speechSecrets, _namePlayer.PlayAsync);
        _hub = new ChatHub(_settings, _badgeCatalog, _session) { SpeechEnabled = _nameSpeech.IsConfigured };
        _dockContext = new DockServerContext
        {
            Settings = _settings,
            Hub = _hub,
            Session = _session,
            Api = _apiClient,
            Chat = _chatClient,
            Speech = _nameSpeech
        };
        _dockServer = new DockServer(_dockContext);
        _overlay = new OverlayWindow(_settings, _badgeCatalog);
        _chatFlushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(75) };
        _chatFlushTimer.Tick += FlushPendingMessages;
        _settingsApplyTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _settingsApplyTimer.Tick += (_, _) => { _settingsApplyTimer.Stop(); _overlay.ApplySettings(); };
        _settingsSaveTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(450) };
        _settingsSaveTimer.Tick += (_, _) => { _settingsSaveTimer.Stop(); SaveSettingsNow(); };
        _trayIcon = CreateTrayIcon();
        _overlay.PlacementChanged += SaveSettings;
        _overlay.AddWelcomeMessages();
        PopulateControls();
        _loading = false;
        if (_settings.OverlayVisible) _overlay.Show();

        _chatClient.MessageReceived += QueueMessage;
        _chatClient.MessageReceived += _hub.PublishMessage;
        _chatClient.ModerationReceived += _hub.PublishModeration;
        _chatClient.StatusChanged += status => RunOnUi(() => SetStatus(status));
        _chatClient.RoomDiscovered += room => RunOnUi(async () => await OnRoomDiscoveredAsync(room));
        _chatClient.ConnectionStopped += () => RunOnUi(() => SetConnectionButtons(false));
        _session.StateChanged += () => RunOnUi(UpdateLoginUi);

        UpdateLoginUi();
        UpdateLoginButtonState();
        ApplySpeechConfiguration();
        // Sample lines so the dock shows what the reading settings look like before anything is connected.
        _hub.ShowSamples();
        _ = StartDockServerAsync();
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
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
                authenticated ? _session.TryGetIrcTokenAsync : null);
            SetConnectionButtons(true);
            _hub.PublishAuth(_chatClient.CanSend);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TwitchAuthException)
        {
            SetStatus(ex.Message, true);
        }
        finally { _reconnecting = false; }
    }

    private async Task OnRoomDiscoveredAsync(string roomId)
    {
        _hub.BroadcasterId = roomId;
        _hub.PublishAuth(_chatClient.CanSend);
        await LoadBadgesAsync(roomId);
    }

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Öppna inställningar", null, (_, _) => RestoreFromTray());
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

    private void QueueMessage(ChatMessage message)
    {
        _pendingMessages.Enqueue(message);
        int count = Interlocked.Increment(ref _pendingMessageCount);
        while (count > 500 && _pendingMessages.TryDequeue(out _))
            count = Interlocked.Decrement(ref _pendingMessageCount);

        if (Interlocked.Exchange(ref _chatTimerRequested, 1) == 0)
            RunOnUi(() => _chatFlushTimer.Start());
    }

    private void FlushPendingMessages(object? sender, EventArgs e)
    {
        var batch = new List<ChatMessage>(50);
        while (batch.Count < 50 && _pendingMessages.TryDequeue(out ChatMessage? message))
        {
            Interlocked.Decrement(ref _pendingMessageCount);
            batch.Add(message);
        }
        if (batch.Count > 0) _overlay.AddMessages(batch);

        if (!_pendingMessages.IsEmpty) return;
        _chatFlushTimer.Stop();
        Interlocked.Exchange(ref _chatTimerRequested, 0);
        if (!_pendingMessages.IsEmpty && Interlocked.Exchange(ref _chatTimerRequested, 1) == 0)
            _chatFlushTimer.Start();
    }

    private void PopulateControls()
    {
        ChannelBox.Text = _settings.Channel;
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
        OutlineCheck.IsChecked = _settings.TextOutline;
        StartWithWindowsCheck.IsChecked = _settings.StartWithWindows;
        SelectComboByText(FontFamilyBox, _settings.FontFamily);
        _settings.MaxMessages = Math.Clamp(_settings.MaxMessages, 1, 200);
        MaxMessagesInput.Text = _settings.MaxMessages.ToString();
        VisibilityButton.Content = _settings.OverlayVisible ? "Dölj overlay" : "Visa overlay";
        ToggleHotkeyLabel.Text = _settings.ToggleHotkeyText;
        EditHotkeyLabel.Text = _settings.EditHotkeyText;
        UpdateValueLabels();
    }

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

        _settings.Channel = channel;
        _settings.ClientId = ClientIdBox.Text.Trim();
        _lastBadgeRoom = null;
        _badgeLoadCancellation?.Cancel();
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
                _session.IsLoggedIn ? _session.TryGetIrcTokenAsync : null);
            _hub.PublishAuth(_chatClient.CanSend);
        }
        catch (Exception ex) { SetConnectionButtons(false); SetStatus(ex.Message, true); }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await _chatClient.DisconnectAsync();
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
        _settings.TextOutline = OutlineCheck.IsChecked == true;
        _settings.FontFamily = SelectedText(FontFamilyBox) ?? "Verdana";
        UpdateValueLabels();
        ScheduleOverlayApply();
        SaveSettings();
    }

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
    }

    private void SaveSettings()
    {
        if (_loading || _closing) return;
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
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

    private void RunOnUi(Action action)
    {
        if (_closing || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _closing = true;
        _trayIcon.Visible = false;
        _chatFlushTimer.Stop();
        _settingsApplyTimer.Stop();
        _settingsSaveTimer.Stop();
        _badgeLoadCancellation?.Cancel();
        _badgeLoadCancellation?.Dispose();
        _badgeLoadCancellation = null;
        SaveSettingsNow();
        GlobalHotkeys.Unregister(this, ToggleHotkeyId);
        GlobalHotkeys.Unregister(this, EditHotkeyId);
        _hwndSource?.RemoveHook(WndProc);
        _overlay.Close();
        _namePlayer.Close();
        await _dockServer.DisposeAsync();
        await _chatClient.DisposeAsync();
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
