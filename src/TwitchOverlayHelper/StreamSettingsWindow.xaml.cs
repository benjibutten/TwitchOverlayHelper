using System.Windows;
using TwitchOverlayHelper.Interop;
using TwitchOverlayHelper.Settings;

namespace TwitchOverlayHelper;

/// <summary>
/// Appearance of the chat the viewers see. A window of its own next to the dock's rather than a tab
/// inside it: the two are read by different people at different distances, and the questions worth
/// asking are different too. Nothing here decides what the streamer sees, and nothing in the dock's
/// window reaches the broadcast.
/// </summary>
public partial class StreamSettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _onChanged;
    private bool _loading = true;

    public StreamSettingsWindow(AppSettings settings, Action onChanged)
    {
        InitializeComponent();
        DarkTitleBar.Enable(this);
        _settings = settings;
        _onChanged = onChanged;
        Populate();
        _loading = false;
    }

    private void Populate()
    {
        StreamSettings stream = _settings.Stream;
        FontBox.Text = stream.FontFamily;
        FontSizeSlider.Value = stream.FontSize;
        LineHeightSlider.Value = stream.LineHeight;
        GapSlider.Value = stream.MessageGap;
        PlateSlider.Value = stream.MessageBackgroundOpacity;
        MaxMessagesSlider.Value = stream.MaxMessages;
        FadeSlider.Value = stream.FadeAfterSeconds;
        OutlineCheck.IsChecked = stream.TextOutline;
        NameLineCheck.IsChecked = stream.NameOnOwnLine;
        TopCheck.IsChecked = stream.NewestOnTop;
        AnimateCheck.IsChecked = stream.Animate;
        BadgesCheck.IsChecked = stream.ShowBadges;
        NameColorsCheck.IsChecked = stream.UseTwitchNameColors;
        EmotesCheck.IsChecked = stream.ShowEmotes;
        GiantEmotesCheck.IsChecked = stream.GiantEmotes;
        RepliesCheck.IsChecked = stream.ShowReplies;
        TimestampsCheck.IsChecked = stream.ShowTimestamps;
        LinksCheck.IsChecked = stream.CollapseLinks;
        ShoutingCheck.IsChecked = stream.CalmShouting;
        CommandsCheck.IsChecked = stream.HideCommands;
        IgnoreBox.Text = stream.IgnoredAccounts;
        EventSubsCheck.IsChecked = stream.Events.Subs;
        EventRaidsCheck.IsChecked = stream.Events.Raids;
        EventAnnouncementsCheck.IsChecked = stream.Events.Announcements;
        EventBitsCheck.IsChecked = stream.Events.Bits;
        EventMilestonesCheck.IsChecked = stream.Events.Milestones;
        EventRewardsCheck.IsChecked = stream.Events.Rewards;
        EventShoutoutsCheck.IsChecked = stream.Events.Shoutouts;
        EventOtherCheck.IsChecked = stream.Events.Other;
        UpdateValueLabels();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        StreamSettings stream = _settings.Stream;
        stream.FontFamily = string.IsNullOrWhiteSpace(FontBox.Text) ? "Verdana" : FontBox.Text.Trim();
        stream.FontSize = FontSizeSlider.Value;
        stream.LineHeight = LineHeightSlider.Value;
        stream.MessageGap = GapSlider.Value;
        stream.MessageBackgroundOpacity = PlateSlider.Value;
        stream.MaxMessages = (int)Math.Round(MaxMessagesSlider.Value);
        stream.FadeAfterSeconds = (int)Math.Round(FadeSlider.Value);
        stream.TextOutline = OutlineCheck.IsChecked == true;
        stream.NameOnOwnLine = NameLineCheck.IsChecked == true;
        stream.NewestOnTop = TopCheck.IsChecked == true;
        stream.Animate = AnimateCheck.IsChecked == true;
        stream.ShowBadges = BadgesCheck.IsChecked == true;
        stream.UseTwitchNameColors = NameColorsCheck.IsChecked == true;
        stream.ShowEmotes = EmotesCheck.IsChecked == true;
        stream.GiantEmotes = GiantEmotesCheck.IsChecked == true;
        stream.ShowReplies = RepliesCheck.IsChecked == true;
        stream.ShowTimestamps = TimestampsCheck.IsChecked == true;
        stream.CollapseLinks = LinksCheck.IsChecked == true;
        stream.CalmShouting = ShoutingCheck.IsChecked == true;
        stream.HideCommands = CommandsCheck.IsChecked == true;
        stream.IgnoredAccounts = IgnoreBox.Text;
        stream.Events.Subs = EventSubsCheck.IsChecked == true;
        stream.Events.Raids = EventRaidsCheck.IsChecked == true;
        stream.Events.Announcements = EventAnnouncementsCheck.IsChecked == true;
        stream.Events.Bits = EventBitsCheck.IsChecked == true;
        stream.Events.Milestones = EventMilestonesCheck.IsChecked == true;
        stream.Events.Rewards = EventRewardsCheck.IsChecked == true;
        stream.Events.Shoutouts = EventShoutoutsCheck.IsChecked == true;
        stream.Events.Other = EventOtherCheck.IsChecked == true;
        stream.Normalize();

        UpdateValueLabels();
        _onChanged();
    }

    private void UpdateValueLabels()
    {
        StreamSettings stream = _settings.Stream;
        FontSizeValue.Text = $"{stream.FontSize:0} px";
        LineHeightValue.Text = $"{stream.LineHeight:0.00}×";
        GapValue.Text = $"{stream.MessageGap:0} px";
        PlateValue.Text = stream.MessageBackgroundOpacity > 0 ? $"{stream.MessageBackgroundOpacity:P0}" : "ingen";
        MaxMessagesValue.Text = stream.MaxMessages.ToString();
        FadeValue.Text = stream.FadeAfterSeconds > 0 ? $"{stream.FadeAfterSeconds} s" : "ligger kvar";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _settings.Stream = new StreamSettings();
        _loading = true;
        Populate();
        _loading = false;
        _onChanged();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
