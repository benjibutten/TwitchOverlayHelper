using System.Windows;
using System.Windows.Controls;
using TwitchOverlayHelper.Settings;

namespace TwitchOverlayHelper;

/// <summary>
/// Reading settings for the OBS dock. They live here rather than in the dock itself: the dock is a
/// reading surface where space is scarce, and this window can afford to explain what each option does.
/// </summary>
public partial class DockSettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _onChanged;
    private bool _loading = true;

    public DockSettingsWindow(AppSettings settings, Action onChanged)
    {
        InitializeComponent();
        _settings = settings;
        _onChanged = onChanged;
        Populate();
        _loading = false;
    }

    private void Populate()
    {
        DockSettings dock = _settings.Dock;
        SelectByTag(ThemeBox, dock.Theme);
        FontBox.Text = dock.FontFamily;
        FontSizeSlider.Value = dock.FontSize;
        LineHeightSlider.Value = dock.LineHeight;
        LetterSlider.Value = dock.LetterSpacing;
        WordSlider.Value = dock.WordSpacing;
        GapSlider.Value = dock.MessageGap;
        PaceSlider.Value = dock.MessagesPerSecond;
        PinSecondsSlider.Value = dock.PinnedMentionSeconds;
        MaxMessagesSlider.Value = dock.MaxMessages;
        PinMentionsCheck.IsChecked = dock.PinMentions;
        ZebraCheck.IsChecked = dock.ZebraRows;
        NameLineCheck.IsChecked = dock.NameOnOwnLine;
        BadgesCheck.IsChecked = dock.ShowBadges;
        TimestampsCheck.IsChecked = dock.ShowTimestamps;
        NameColorsCheck.IsChecked = dock.UseTwitchNameColors;
        EmotesCheck.IsChecked = dock.ShowEmotes;
        LinksCheck.IsChecked = dock.CollapseLinks;
        ShoutingCheck.IsChecked = dock.CalmShouting;
        CommandsCheck.IsChecked = dock.DimCommands;
        UpdateValueLabels();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        DockSettings dock = _settings.Dock;
        dock.Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "cream";
        dock.FontFamily = string.IsNullOrWhiteSpace(FontBox.Text) ? "Verdana" : FontBox.Text.Trim();
        dock.FontSize = FontSizeSlider.Value;
        dock.LineHeight = LineHeightSlider.Value;
        dock.LetterSpacing = LetterSlider.Value;
        dock.WordSpacing = WordSlider.Value;
        dock.MessageGap = GapSlider.Value;
        dock.MessagesPerSecond = PaceSlider.Value;
        dock.PinnedMentionSeconds = (int)Math.Round(PinSecondsSlider.Value);
        dock.MaxMessages = (int)Math.Round(MaxMessagesSlider.Value);
        dock.PinMentions = PinMentionsCheck.IsChecked == true;
        dock.ZebraRows = ZebraCheck.IsChecked == true;
        dock.NameOnOwnLine = NameLineCheck.IsChecked == true;
        dock.ShowBadges = BadgesCheck.IsChecked == true;
        dock.ShowTimestamps = TimestampsCheck.IsChecked == true;
        dock.UseTwitchNameColors = NameColorsCheck.IsChecked == true;
        dock.ShowEmotes = EmotesCheck.IsChecked == true;
        dock.CollapseLinks = LinksCheck.IsChecked == true;
        dock.CalmShouting = ShoutingCheck.IsChecked == true;
        dock.DimCommands = CommandsCheck.IsChecked == true;
        dock.Normalize();

        UpdateValueLabels();
        _onChanged();
    }

    private void UpdateValueLabels()
    {
        DockSettings dock = _settings.Dock;
        FontSizeValue.Text = $"{dock.FontSize:0} px";
        LineHeightValue.Text = $"{dock.LineHeight:0.00}×";
        LetterValue.Text = $"{dock.LetterSpacing:P0}";
        WordValue.Text = $"{dock.WordSpacing:P0}";
        GapValue.Text = $"{dock.MessageGap:0} px";
        PaceValue.Text = dock.MessagesPerSecond > 0 ? $"{dock.MessagesPerSecond:0.#}/sek" : "ingen broms";
        PinSecondsValue.Text = $"{dock.PinnedMentionSeconds} s";
        MaxMessagesValue.Text = dock.MaxMessages.ToString();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _settings.Dock = new DockSettings();
        _loading = true;
        Populate();
        _loading = false;
        _onChanged();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (object item in box.Items)
            if (item is ComboBoxItem option && string.Equals(option.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                box.SelectedItem = option;
                return;
            }
        box.SelectedIndex = 0;
    }
}
