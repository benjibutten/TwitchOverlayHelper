using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Overlay;

public partial class OverlayWindow : Window
{
    private readonly AppSettings _settings;
    private readonly TwitchBadgeCatalog _badges;
    private readonly DispatcherTimer _topmostTimer;
    private bool _editMode;

    public event Action? PlacementChanged;

    public OverlayWindow(AppSettings settings, TwitchBadgeCatalog badges)
    {
        InitializeComponent();
        _settings = settings;
        _badges = badges;
        Left = settings.OverlayLeft;
        Top = settings.OverlayTop;
        Width = Math.Max(320, settings.OverlayWidth);
        Height = Math.Max(260, settings.OverlayHeight);
        ApplySettings();
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _topmostTimer.Tick += (_, _) => EnsureTopmost();
        _topmostTimer.Start();
    }

    public void ApplySettings()
    {
        byte alpha = (byte)Math.Round(Math.Clamp(_settings.BackgroundOpacity, 0.1, 0.96) * 255);
        Surface.Background = new SolidColorBrush(Color.FromArgb(alpha, 14, 17, 25));
        ChatMessage[] messages = MessagePanel.Children.OfType<Border>().Select(card => card.Tag).OfType<ChatMessage>().ToArray();
        if (messages.Length > 0)
        {
            MessagePanel.Children.Clear();
            foreach (ChatMessage message in messages) MessagePanel.Children.Add(CreateMessageCard(message));
            ChatScroller.ScrollToEnd();
        }
    }

    public void AddMessage(ChatMessage message)
    {
        Border card = CreateMessageCard(message);
        MessagePanel.Children.Add(card);
        while (MessagePanel.Children.Count > _settings.MaxMessages) MessagePanel.Children.RemoveAt(0);
        ChatScroller.ScrollToEnd();
    }

    public void AddWelcomeMessages()
    {
        AddMessage(new ChatMessage("welcome-1", "Twitch Overlay Helper", "Chatten visas här – stor, lugn och lätt att skanna.", "#A970FF", [new ChatBadge("broadcaster", "1")], false, false, DateTimeOffset.Now));
        AddMessage(new ChatMessage("welcome-2", "Tips", "Tryck på “Redigera overlay” i appen för att flytta eller ändra storlek.", "#5FD6C8", [], false, false, DateTimeOffset.Now));
    }

    public void SetEditMode(bool enabled)
    {
        _editMode = enabled;
        EditBanner.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ResizeThumb.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        Surface.BorderBrush = enabled ? new SolidColorBrush(Color.FromRgb(169, 112, 255)) : Brushes.Transparent;
        Focusable = enabled;
        ShowActivated = enabled;
        UpdateExtendedStyles();
        if (enabled) { Show(); Activate(); }
    }

    private Border CreateMessageCard(ChatMessage message)
    {
        var card = new Border { CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(11, 8, 11, 9), Tag = message };
        string mentionName = string.IsNullOrWhiteSpace(_settings.UserName) ? _settings.Channel : _settings.UserName;
        bool isMention = _settings.EmphasizeMentions && mentionName.Length > 0 && message.Text.Contains("@" + mentionName, StringComparison.OrdinalIgnoreCase);
        card.Background = new SolidColorBrush(isMention
            ? Color.FromArgb(112, 245, 158, 11)
            : message.IsHighlighted ? Color.FromArgb(92, 169, 112, 255) : Color.FromArgb(48, 255, 255, 255));
        if (isMention)
        {
            card.BorderBrush = new SolidColorBrush(Color.FromRgb(251, 191, 36));
            card.BorderThickness = new Thickness(2);
        }
        var stack = new StackPanel();
        var identity = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };

        if (_settings.ShowBadges)
        {
            foreach (ChatBadge badge in message.Badges)
                identity.Children.Add(CreateBadge(badge));
        }

        if (_settings.ShowTimestamps)
            identity.Children.Add(new TextBlock { Text = message.SentAt.LocalDateTime.ToString("HH:mm") + "  ", Foreground = new SolidColorBrush(Color.FromRgb(183, 180, 194)), FontSize = Math.Max(12, _settings.FontSize * 0.62), VerticalAlignment = VerticalAlignment.Center });

        Color nameColor = Color.FromRgb(214, 202, 255);
        if (_settings.UseTwitchNameColors && !string.IsNullOrWhiteSpace(message.NameColor))
            try { nameColor = (Color)ColorConverter.ConvertFromString(message.NameColor)!; } catch (FormatException) { }
        identity.Children.Add(new TextBlock { Text = message.DisplayName, Foreground = new SolidColorBrush(EnsureReadable(nameColor)), FontWeight = FontWeights.Bold, FontSize = _settings.FontSize * 0.78, FontFamily = new FontFamily(_settings.FontFamily), VerticalAlignment = VerticalAlignment.Center });
        if (message.IsFirstMessage)
            identity.Children.Add(CreateLabel("NY", Color.FromRgb(95, 214, 200)));
        if (isMention)
            identity.Children.Add(CreateLabel("TILL DIG", Color.FromRgb(217, 119, 6)));

        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Text = message.Text };
        stack.Children.Add(identity);
        stack.Children.Add(body);
        card.Child = stack;
        ApplyMessageTypography(card);
        return card;
    }

    private FrameworkElement CreateBadge(ChatBadge badge)
    {
        if (_badges.TryGet(badge.SetId, badge.Version, out BadgeInfo? info))
        {
            try
            {
                return new Image { Source = new BitmapImage(new Uri(info!.ImageUrl)), Width = 22, Height = 22, Margin = new Thickness(0, 0, 5, 0), ToolTip = info.Title };
            }
            catch (UriFormatException) { }
        }

        (string text, Color color) = badge.SetId switch
        {
            "broadcaster" => ("LIVE", Color.FromRgb(239, 68, 68)),
            "moderator" => ("MOD", Color.FromRgb(34, 197, 94)),
            "vip" => ("VIP", Color.FromRgb(219, 39, 119)),
            "subscriber" => ("SUB", Color.FromRgb(145, 92, 246)),
            "staff" => ("STAFF", Color.FromRgb(0, 173, 238)),
            _ => (badge.SetId.Length <= 4 ? badge.SetId.ToUpperInvariant() : "◆", Color.FromRgb(96, 101, 122))
        };
        return CreateLabel(text, color);
    }

    private static Border CreateLabel(string text, Color color) => new()
    {
        Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(5, 2, 5, 2),
        Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.ExtraBold, VerticalAlignment = VerticalAlignment.Center }
    };

    private void ApplyMessageTypography(Border card)
    {
        if (card.Child is not StackPanel stack || stack.Children.Count < 2 || stack.Children[1] is not TextBlock body) return;
        body.FontFamily = new FontFamily(_settings.FontFamily);
        body.FontSize = _settings.FontSize;
        body.LineHeight = _settings.FontSize * _settings.LineSpacing;
        body.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
    }

    private static Color EnsureReadable(Color source)
    {
        double luminance = 0.2126 * source.R + 0.7152 * source.G + 0.0722 * source.B;
        if (luminance >= 115) return source;
        double factor = (115 - luminance) / 140;
        return Color.FromRgb((byte)(source.R + (255 - source.R) * factor), (byte)(source.G + (255 - source.G) * factor), (byte)(source.B + (255 - source.B) * factor));
    }

    private void EditBanner_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_editMode && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void ResizeThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        Width = Math.Max(320, Width + e.HorizontalChange);
        Height = Math.Max(260, Height + e.VerticalChange);
        SavePlacement();
    }

    protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); if (_editMode) SavePlacement(); }
    protected override void OnSourceInitialized(EventArgs e) { base.OnSourceInitialized(e); UpdateExtendedStyles(); }
    protected override void OnClosed(EventArgs e) { _topmostTimer.Stop(); base.OnClosed(e); }

    private void SavePlacement()
    {
        _settings.OverlayLeft = Left; _settings.OverlayTop = Top; _settings.OverlayWidth = Width; _settings.OverlayHeight = Height;
        PlacementChanged?.Invoke();
    }

    private void EnsureTopmost()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && IsVisible) SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }

    private void UpdateExtendedStyles()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        long style = GetWindowLongPtr(hwnd, -20).ToInt64();
        style |= 0x00000080;
        if (_editMode)
            style &= ~(0x00000020L | 0x08000000L);
        else
            style |= 0x00000020 | 0x08000000;
        SetWindowLongPtr(hwnd, -20, new IntPtr(style));
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
