using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TwitchOverlayHelper.Interop;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Overlay;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper;

public partial class MainWindow : Window
{
    private const int ToggleHotkeyId = 9001;
    private const int EditHotkeyId = 9002;

    private readonly SettingsStore _settingsStore = new();
    private readonly TwitchBadgeCatalog _badgeCatalog = new();
    private readonly TwitchChatClient _chatClient = new();
    private readonly AppSettings _settings;
    private readonly OverlayWindow _overlay;
    private HwndSource? _hwndSource;
    private Button? _recordingButton;
    private bool _loading = true;
    private bool _editing;
    private string? _lastBadgeRoom;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        _overlay = new OverlayWindow(_settings, _badgeCatalog);
        _overlay.PlacementChanged += SaveSettings;
        _overlay.AddWelcomeMessages();
        PopulateControls();
        _loading = false;
        if (_settings.OverlayVisible) _overlay.Show();

        _chatClient.MessageReceived += message => Dispatcher.Invoke(() => _overlay.AddMessage(message));
        _chatClient.StatusChanged += status => Dispatcher.Invoke(() => SetStatus(status));
        _chatClient.RoomDiscovered += room => Dispatcher.InvokeAsync(() => LoadBadgesAsync(room));
        Closed += MainWindow_Closed;
    }

    private void PopulateControls()
    {
        ChannelBox.Text = _settings.Channel;
        ClientIdBox.Text = _settings.ClientId;
        UserNameBox.Text = _settings.UserName;
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
        SelectComboByText(FontFamilyBox, _settings.FontFamily);
        SelectComboByText(MaxMessagesBox, _settings.MaxMessages.ToString());
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
            ? "Tryck på Spela in och sedan tangentkombinationen. Esc avbryter."
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
            _recordingButton.Content = "⌨ Spela in";
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
        _settings.UserName = UserNameBox.Text.Trim();
        SaveSettings();
        try
        {
            await _chatClient.ConnectAsync(channel, _settings.UserName, TokenBox.Password);
            ConnectButton.IsEnabled = false;
            DisconnectButton.IsEnabled = true;
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await _chatClient.DisconnectAsync();
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
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
        if (int.TryParse(SelectedText(MaxMessagesBox), out int max)) _settings.MaxMessages = max;
        UpdateValueLabels();
        _overlay.ApplySettings();
        SaveSettings();
    }

    private async Task LoadBadgesAsync(string roomId)
    {
        if (_lastBadgeRoom == roomId || string.IsNullOrWhiteSpace(ClientIdBox.Text) || string.IsNullOrWhiteSpace(TokenBox.Password)) return;
        _lastBadgeRoom = roomId;
        try { await _badgeCatalog.LoadAsync(ClientIdBox.Text, TokenBox.Password, roomId); }
        catch (Exception ex) { SetStatus("Chat ansluten • badges kunde inte laddas: " + ex.Message, true); }
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        bool live = text.StartsWith("Live", StringComparison.Ordinal);
        StatusDot.Fill = new SolidColorBrush(error ? Color.FromRgb(239, 68, 68) : live ? Color.FromRgb(34, 197, 94) : Color.FromRgb(245, 158, 11));
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
        if (!_loading) _settingsStore.Save(_settings);
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        SaveSettings();
        GlobalHotkeys.Unregister(this, ToggleHotkeyId);
        GlobalHotkeys.Unregister(this, EditHotkeyId);
        _hwndSource?.RemoveHook(WndProc);
        _overlay.Close();
        await _chatClient.DisposeAsync();
    }

    private static string? SelectedText(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString();
    private static void SelectComboByText(ComboBox box, string text)
    {
        foreach (object item in box.Items)
            if (item is ComboBoxItem option && string.Equals(option.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase)) { box.SelectedItem = option; return; }
        box.SelectedIndex = 0;
    }
}
