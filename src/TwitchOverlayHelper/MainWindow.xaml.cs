using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Overlay;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly TwitchBadgeCatalog _badgeCatalog = new();
    private readonly TwitchChatClient _chatClient = new();
    private readonly AppSettings _settings;
    private readonly OverlayWindow _overlay;
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
        BadgesCheck.IsChecked = _settings.ShowBadges;
        NameColorsCheck.IsChecked = _settings.UseTwitchNameColors;
        TimestampCheck.IsChecked = _settings.ShowTimestamps;
        MentionsCheck.IsChecked = _settings.EmphasizeMentions;
        SelectComboByText(FontFamilyBox, _settings.FontFamily);
        SelectComboByText(MaxMessagesBox, _settings.MaxMessages.ToString());
        UpdateValueLabels();
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

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        _editing = !_editing;
        if (_editing && !_settings.OverlayVisible)
        {
            _settings.OverlayVisible = true;
            VisibilityButton.Content = "Dölj overlay";
        }
        _overlay.SetEditMode(_editing);
        EditButton.Content = _editing ? "Lås overlay" : "Redigera overlay";
        EditButton.Background = new SolidColorBrush(_editing ? Color.FromRgb(25, 145, 125) : Color.FromRgb(109, 63, 209));
    }

    private void VisibilityButton_Click(object sender, RoutedEventArgs e)
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
        _settings.ShowBadges = BadgesCheck.IsChecked == true;
        _settings.UseTwitchNameColors = NameColorsCheck.IsChecked == true;
        _settings.ShowTimestamps = TimestampCheck.IsChecked == true;
        _settings.EmphasizeMentions = MentionsCheck.IsChecked == true;
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
    }

    private void SaveSettings()
    {
        if (!_loading) _settingsStore.Save(_settings);
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        SaveSettings();
        _overlay.Close();
        await _chatClient.DisposeAsync();
    }

    private void AuthExpander_Expanded(object sender, RoutedEventArgs e) { }
    private static string? SelectedText(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString();
    private static void SelectComboByText(ComboBox box, string text)
    {
        foreach (object item in box.Items)
            if (item is ComboBoxItem option && string.Equals(option.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase)) { box.SelectedItem = option; return; }
        box.SelectedIndex = 0;
    }
}
