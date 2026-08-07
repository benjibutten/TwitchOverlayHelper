using System.Net.Http;
using System.IO;
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
using XamlAnimatedGif;

namespace TwitchOverlayHelper.Overlay;

public partial class OverlayWindow : Window
{
    /// <summary>How much bigger a gigantified emote is drawn. Twitch shows it at roughly triple.</summary>
    private const double GiantEmoteScale = 3;

    private static readonly System.Windows.Media.Effects.DropShadowEffect TextOutlineEffect = CreateTextOutline();
    private static readonly HttpClient EmoteHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };
    private readonly AppSettings _settings;
    private readonly TwitchBadgeCatalog _badges;
    private readonly AnimatedEmoteLoader _animatedEmotes = new(EmoteHttpClient);
    private readonly DispatcherTimer _topmostTimer;
    private readonly Dictionary<string, BitmapImage> _imageCache = new(StringComparer.Ordinal);
    private RenderSettings? _renderSettings;
    private bool _editMode;

    public event Action? PlacementChanged;

    public OverlayWindow(AppSettings settings, TwitchBadgeCatalog badges)
    {
        InitializeComponent();
        _settings = settings;
        _badges = badges;
        Width = Math.Max(320, settings.OverlayWidth);
        Height = Math.Max(260, settings.OverlayHeight);
        Left = Math.Clamp(settings.OverlayLeft, SystemParameters.VirtualScreenLeft - Width + 80, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80);
        Top = Math.Clamp(settings.OverlayTop, SystemParameters.VirtualScreenTop - Height + 80, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80);
        ApplySettings();
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _topmostTimer.Tick += (_, _) => EnsureTopmost();
        _topmostTimer.Start();
    }

    public void ApplySettings() => ApplySettings(forceRefresh: false);

    public void RefreshMessages() => ApplySettings(forceRefresh: true);

    private void ApplySettings(bool forceRefresh)
    {
        byte alpha = (byte)Math.Round(Math.Clamp(_settings.BackgroundOpacity, 0, 0.96) * 255);
        Surface.Background = new SolidColorBrush(Color.FromArgb(alpha, 14, 17, 25));
        while (MessagePanel.Children.Count > _settings.MaxMessages) RemoveOldestMessage();

        RenderSettings current = RenderSettings.From(_settings);
        if (!forceRefresh && _renderSettings == current) return;
        _renderSettings = current;

        // Rebuilt from the tags, so a settings change keeps events in the reading order they landed in.
        ChatTimelineItem[] items = MessagePanel.Children.OfType<Border>().Select(card => card.Tag)
            .OfType<ChatTimelineItem>().TakeLast(_settings.MaxMessages).ToArray();
        ClearMessages();
        foreach (ChatTimelineItem item in items) MessagePanel.Children.Add(CreateCard(item));
        if (items.Length > 0) ChatScroller.ScrollToEnd();
    }

    public void AddMessage(ChatMessage message)
        => AddItems([ChatTimelineItem.Of(message)]);

    public void AddEvent(ChatEvent chatEvent)
        => AddItems([ChatTimelineItem.Of(chatEvent)]);

    public void AddItems(IReadOnlyList<ChatTimelineItem> items)
    {
        foreach (ChatTimelineItem item in items)
            MessagePanel.Children.Add(CreateCard(item));
        // Event cards share the column with the chat lines, so they count towards the limit too.
        while (MessagePanel.Children.Count > _settings.MaxMessages) RemoveOldestMessage();
        if (items.Count > 0) ChatScroller.ScrollToEnd();
    }

    /// <summary>
    /// Replaces a line already on screen with a changed version of itself. Only one thing needs it:
    /// a Gigantify an Emote power-up that reached the app after the message it belongs to. A line
    /// that has already scrolled off the top is simply not found, which is the right answer for a
    /// marker there is no longer anywhere to put.
    /// </summary>
    public void UpdateMessage(ChatMessage message)
    {
        for (int i = MessagePanel.Children.Count - 1; i >= 0; i--)
        {
            if (MessagePanel.Children[i] is not Border card) continue;
            if (card.Tag is not ChatTimelineItem { Message: { } existing }) continue;
            if (!string.Equals(existing.Id, message.Id, StringComparison.Ordinal)) continue;

            MessagePanel.Children[i] = CreateMessageCard(message);
            // The card just grew by a couple of lines; the newest message must stay the visible one.
            if (i == MessagePanel.Children.Count - 1) ChatScroller.ScrollToEnd();
            return;
        }
    }

    private Border CreateCard(ChatTimelineItem item) =>
        item.Event is { } chatEvent ? CreateEventCard(chatEvent) : CreateMessageCard(item.Message!);

    public void AddWelcomeMessages()
    {
        AddMessage(new ChatMessage("welcome-1", "Twitch Overlay Helper", "Chatten visas här – stor, lugn och lätt att skanna.", "#A970FF", [new ChatBadge("broadcaster", "1")], false, false, DateTimeOffset.Now));
        AddMessage(new ChatMessage("welcome-2", "Tips", "Tryck på “Redigera overlay” i appen för att flytta eller ändra storlek.", "#5FD6C8", [], false, false, DateTimeOffset.Now));
        const string emoteDemoPrefix = "Emotes visas som bilder ";
        AddMessage(new ChatMessage("welcome-3", "Emotes", emoteDemoPrefix + "Kappa", "#F59E0B", [], false, false, DateTimeOffset.Now,
            [new EmoteSpan("25", emoteDemoPrefix.Length, 5)]));
        AddEvent(new ChatEvent(ChatEventType.Subscription, "welcome-4", "Kajsa_92", DateTimeOffset.Now)
        {
            UserLogin = "kajsa_92",
            Months = 8,
            Tier = "1000",
            Message = "så här ser subs, raids och meddelanden ut"
        });
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
        var card = new Border { CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(11, 8, 11, 9), Tag = ChatTimelineItem.Of(message) };
        string mentionName = string.IsNullOrWhiteSpace(_settings.UserName) ? _settings.Channel : _settings.UserName;
        // An answer to something you said is aimed at you too, even though the "@you" was cut away.
        bool isMention = _settings.EmphasizeMentions && mentionName.Length > 0
            && (message.Text.Contains("@" + mentionName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(message.Reply?.ParentLogin, mentionName, StringComparison.OrdinalIgnoreCase));
        byte messageAlpha = (byte)Math.Round(Math.Clamp(_settings.MessageBackgroundOpacity, 0, 0.9) * 255);
        card.Background = new SolidColorBrush(isMention
            ? Color.FromArgb(112, 245, 158, 11)
            : message.IsHighlighted ? Color.FromArgb(92, 169, 112, 255) : Color.FromArgb(messageAlpha, 255, 255, 255));
        if (isMention)
        {
            card.BorderBrush = new SolidColorBrush(Color.FromRgb(251, 191, 36));
            card.BorderThickness = new Thickness(2);
        }
        var stack = new StackPanel();
        if (message.Reply is { } reply) stack.Children.Add(CreateReplyLine(reply));
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
        // A cheer is a normal message carrying bits; the marker says so without a card of its own.
        if (message.Bits is > 0)
            identity.Children.Add(CreateLabel($"{message.Bits} BITS", Color.FromRgb(139, 92, 246)));
        // A redemption that asked for text is the same shape: the words are the message, the reward
        // is a marker. Named when we are allowed to read the channel's rewards, neutral when not.
        if (message.RewardId is { Length: > 0 })
            identity.Children.Add(CreateLabel(
                message.RewardTitle is { Length: > 0 } title ? title.ToUpperInvariant() : "BELÖNING",
                Color.FromRgb(15, 124, 138)));
        // Power-ups. A message effect is an animation the overlay does not reproduce, so it stays a
        // label; a gigantified emote speaks for itself and only needs saying when it is shown small.
        if (message.MessageEffectId is { Length: > 0 })
            identity.Children.Add(CreateLabel("⚡ EFFEKT", Color.FromRgb(139, 92, 246)));
        if (message.GigantifiedEmoteIndex >= 0 && !_settings.GiantEmotes)
            identity.Children.Add(CreateLabel("⚡ FÖRSTORAD", Color.FromRgb(139, 92, 246)));

        TextBlock body = CreateMessageBody(message);
        stack.Children.Add(identity);
        stack.Children.Add(body);
        card.Child = stack;
        ApplyMessageTypography(card);
        if (_settings.TextOutline)
        {
            identity.Effect = TextOutlineEffect;
            // Applying an Effect to the TextBlock also runs the pixel shader over
            // InlineUIContainer images, which makes small emotes look grey/dark.
            if (!body.Inlines.OfType<InlineUIContainer>().Any())
                body.Effect = TextOutlineEffect;
        }
        return card;
    }

    /// <summary>
    /// A sub, raid or announcement. Built like a message card so the reading rhythm holds, and set
    /// apart by a coloured band down the side rather than by shouting louder than the chat.
    /// </summary>
    private Border CreateEventCard(ChatEvent chatEvent)
    {
        (string label, Color accent) = EventLook(chatEvent);
        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(11, 8, 11, 9),
            Tag = ChatTimelineItem.Of(chatEvent),
            Background = new SolidColorBrush(Color.FromArgb(78, accent.R, accent.G, accent.B)),
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(4, 0, 0, 0)
        };

        var stack = new StackPanel();
        var identity = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        if (_settings.ShowTimestamps)
            identity.Children.Add(new TextBlock { Text = chatEvent.At.LocalDateTime.ToString("HH:mm") + "  ", Foreground = new SolidColorBrush(Color.FromRgb(183, 180, 194)), FontSize = Math.Max(12, _settings.FontSize * 0.62), VerticalAlignment = VerticalAlignment.Center });
        identity.Children.Add(CreateLabel(label, accent));
        identity.Children.Add(new TextBlock
        {
            Text = ChatEventText.Describe(chatEvent),
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = _settings.FontSize * 0.78,
            FontFamily = new FontFamily(_settings.FontFamily),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(identity);

        // Only some notices carry the chatter's own words, and those words are what people read.
        if (!string.IsNullOrWhiteSpace(chatEvent.Message))
            stack.Children.Add(CreateBody(chatEvent.Message, chatEvent.Emotes));

        card.Child = stack;
        ApplyMessageTypography(card);
        if (_settings.TextOutline) identity.Effect = TextOutlineEffect;
        return card;
    }

    /// <summary>The short label and the band colour that tell one kind of event from another.</summary>
    private static (string Label, Color Accent) EventLook(ChatEvent chatEvent) => chatEvent.Type switch
    {
        ChatEventType.Subscription => ("SUB", Color.FromRgb(169, 112, 255)),
        ChatEventType.SubGift => ("GÅVA", Color.FromRgb(169, 112, 255)),
        ChatEventType.CommunityGift => ("GÅVOR", Color.FromRgb(169, 112, 255)),
        ChatEventType.SubUpgrade => ("SUB", Color.FromRgb(169, 112, 255)),
        ChatEventType.Raid => ("RAID", Color.FromRgb(239, 68, 68)),
        ChatEventType.Unraid => ("RAID", Color.FromRgb(239, 68, 68)),
        ChatEventType.Announcement => ("MEDDELANDE", AnnouncementAccent(chatEvent.AnnouncementColor)),
        ChatEventType.BitsBadge => ("BITS", Color.FromRgb(139, 92, 246)),
        ChatEventType.WatchStreak => ("STREAK", Color.FromRgb(95, 214, 200)),
        ChatEventType.NewChatter => ("NY", Color.FromRgb(95, 214, 200)),
        ChatEventType.RewardRedemption => ("BELÖNING", Color.FromRgb(15, 124, 138)),
        ChatEventType.ShoutoutSent or ChatEventType.ShoutoutReceived => ("SHOUTOUT", Color.FromRgb(192, 40, 127)),
        ChatEventType.Celebration => ("FIRANDE", Color.FromRgb(139, 92, 246)),
        _ => ("HÄNDELSE", Color.FromRgb(150, 150, 170))
    };

    /// <summary>Twitch lets the streamer colour an announcement; PRIMARY means no choice was made.</summary>
    private static Color AnnouncementAccent(string? color) => color?.ToUpperInvariant() switch
    {
        "BLUE" => Color.FromRgb(59, 130, 246),
        "GREEN" => Color.FromRgb(31, 157, 85),
        "ORANGE" => Color.FromRgb(217, 122, 43),
        "PURPLE" => Color.FromRgb(122, 75, 208),
        _ => Color.FromRgb(251, 191, 36)
    };

    /// <summary>
    /// The one line that says this is an answer rather than a fresh thought. Small, grey and cut off
    /// at one row: it is context for the sentence below it and must never outweigh what was written.
    /// </summary>
    private TextBlock CreateReplyLine(ChatReply reply) => new()
    {
        Text = $"↩ {reply.ParentDisplayName}: {reply.ParentText}",
        Foreground = new SolidColorBrush(Color.FromRgb(183, 180, 194)),
        FontFamily = new FontFamily(_settings.FontFamily),
        FontSize = Math.Max(11, _settings.FontSize * 0.56),
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
        Margin = new Thickness(0, 0, 0, 2)
    };

    private TextBlock CreateMessageBody(ChatMessage message) =>
        CreateBody(message.Text, message.Emotes, _settings.GiantEmotes ? message.GigantifiedEmoteIndex : -1);

    private TextBlock CreateBody(string text, IReadOnlyList<EmoteSpan> emotes, int giantIndex = -1)
    {
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
        if (!_settings.ShowEmotes)
        {
            body.Text = text;
            return body;
        }

        if (emotes.Count == 0)
        {
            AddTextAndEmojiInlines(body, text);
            return body;
        }

        // Cap emote height to the fixed block line height so images never clip.
        double emoteSize = Math.Round(_settings.FontSize * Math.Min(1.35, _settings.LineSpacing * 0.95));
        int cursor = 0;
        for (int i = 0; i < emotes.Count; i++)
        {
            EmoteSpan emote = emotes[i];
            if (emote.Start < cursor || emote.Start + emote.Length > text.Length) continue;
            if (emote.Start > cursor) AddTextAndEmojiInlines(body, text[cursor..emote.Start]);
            // A gigantified emote is the one image allowed to break out of the line box; the card
            // lets its lines grow for it, see ApplyMessageTypography.
            double size = i == giantIndex ? emoteSize * GiantEmoteScale : emoteSize;
            body.Inlines.Add(CreateEmoteInline(emote, text.Substring(emote.Start, emote.Length), size, i == giantIndex));
            cursor = emote.Start + emote.Length;
        }
        if (cursor < text.Length) AddTextAndEmojiInlines(body, text[cursor..]);
        return body;
    }

    private void AddTextAndEmojiInlines(TextBlock body, string text)
    {
        double size = Math.Round(_settings.FontSize * Math.Min(1.35, _settings.LineSpacing * 0.95));
        foreach ((string part, string? imageCode) in UnicodeEmoji.Split(text))
        {
            if (imageCode is null)
            {
                body.Inlines.Add(new Run(part));
                continue;
            }

            var image = new Image
            {
                Source = GetImage($"https://cdn.jsdelivr.net/gh/jdecked/twemoji@17.0.3/assets/72x72/{imageCode}.png", 72),
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                ToolTip = part
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            body.Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
        }
    }

    private Inline CreateEmoteInline(EmoteSpan emote, string emoteName, double size, bool giant = false)
    {
        // Emote images are served from Twitch's open CDN – no authentication needed.
        var image = new Image
        {
            Width = size,
            Height = size,
            ToolTip = emoteName,
            Stretch = Stretch.Uniform
        };
        try
        {
            // WPF renders Twitch's static PNG reliably. The "default" format may
            // select an animated format whose first frame can be decoded incorrectly.
            // A gigantified emote is drawn three times as large, and only the 3.0 variant has the
            // pixels for it – 2.0 stretched to that size is visibly soft.
            string url = $"https://static-cdn.jtvnw.net/emoticons/v2/{Uri.EscapeDataString(emote.EmoteId)}/static/dark/{(giant ? "3.0" : "2.0")}";
            BitmapImage staticImage = GetImage(url, giant ? 128 : 64);
            image.Source = staticImage;
            EnableAnimation(image, emote.EmoteId, staticImage);
        }
        catch (UriFormatException)
        {
            return new Run(emoteName);
        }
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center };
    }

    private void EnableAnimation(Image image, string emoteId, ImageSource staticImage)
    {
        bool removed = false;

        void StopAnimation(object sender, RoutedEventArgs args)
        {
            removed = true;
            image.ClearValue(AnimationBehavior.SourceStreamProperty);
            image.Unloaded -= StopAnimation;
            AnimationBehavior.RemoveErrorHandler(image, FallBackToStatic);
        }

        void FallBackToStatic(object sender, AnimationErrorEventArgs args)
        {
            args.Handled = true;
            _animatedEmotes.MarkUnavailable(emoteId);
            image.ClearValue(AnimationBehavior.SourceStreamProperty);
            image.Source = staticImage;
        }

        image.Unloaded += StopAnimation;
        AnimationBehavior.AddErrorHandler(image, FallBackToStatic);
        _ = LoadAnimationAsync();

        async Task LoadAnimationAsync()
        {
            try
            {
                byte[]? animationBytes = await _animatedEmotes.GetAnimationAsync(emoteId);
                if (removed || animationBytes is null) return;

                var stream = new MemoryStream(animationBytes, writable: false);
                image.Source = null;
                AnimationBehavior.SetCacheFramesInMemory(image, false);
                AnimationBehavior.SetRepeatBehavior(image, System.Windows.Media.Animation.RepeatBehavior.Forever);
                AnimationBehavior.SetSourceStream(image, stream);
            }
            catch (Exception)
            {
                // Rendering an emote must never be able to take down the overlay.
                if (!removed)
                {
                    image.ClearValue(AnimationBehavior.SourceStreamProperty);
                    image.Source = staticImage;
                }
            }
        }
    }

    private void RemoveOldestMessage()
    {
        if (MessagePanel.Children.Count > 0)
            MessagePanel.Children.RemoveAt(0);
    }

    private void ClearMessages()
    {
        while (MessagePanel.Children.Count > 0)
            MessagePanel.Children.RemoveAt(MessagePanel.Children.Count - 1);
    }

    private static System.Windows.Media.Effects.DropShadowEffect CreateTextOutline()
    {
        var effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black,
            ShadowDepth = 0,
            BlurRadius = 4,
            Opacity = 0.95
        };
        effect.Freeze();
        return effect;
    }

    private FrameworkElement CreateBadge(ChatBadge badge)
    {
        if (_badges.TryGet(badge.SetId, badge.Version, out BadgeInfo? info))
        {
            try
            {
                return new Image { Source = GetImage(info!.ImageUrl, 44), Width = 22, Height = 22, Margin = new Thickness(0, 0, 5, 0), ToolTip = info.Title };
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

    private BitmapImage GetImage(string url, int decodePixelWidth)
    {
        string key = decodePixelWidth + ":" + url;
        if (_imageCache.TryGetValue(key, out BitmapImage? cached)) return cached;
        if (_imageCache.Count >= 512) _imageCache.Clear();

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(url, UriKind.Absolute);
        image.DecodePixelWidth = decodePixelWidth;
        image.EndInit();
        _imageCache[key] = image;
        return image;
    }

    private static Border CreateLabel(string text, Color color) => new()
    {
        Background = new SolidColorBrush(color),
        CornerRadius = new CornerRadius(4),
        Margin = new Thickness(0, 0, 6, 0),
        Padding = new Thickness(5, 2, 5, 2),
        Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.ExtraBold, VerticalAlignment = VerticalAlignment.Center }
    };

    /// <summary>
    /// The body is the last thing in the card – a reply line can sit above the name row, and an
    /// event card may have no body at all, so it is found from the end rather than by index.
    /// </summary>
    private void ApplyMessageTypography(Border card)
    {
        if (card.Child is not StackPanel stack || stack.Children.Count < 2) return;
        if (stack.Children[^1] is not TextBlock body) return;
        body.FontFamily = new FontFamily(_settings.FontFamily);
        body.FontSize = _settings.FontSize;

        // Every line is normally locked to the same height, which is what makes a column of messages
        // scannable. A gigantified emote is three times that, and BlockLineHeight would crop it to
        // the line box instead of showing it – so the one card carrying one is allowed to let its
        // lines grow to whatever is in them.
        bool giant = _settings.GiantEmotes
            && card.Tag is ChatTimelineItem { Message: { } message }
            && message.GigantifiedEmoteIndex >= 0;
        if (giant)
        {
            body.ClearValue(TextBlock.LineHeightProperty);
            body.LineStackingStrategy = LineStackingStrategy.MaxHeight;
            return;
        }
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

    private readonly record struct RenderSettings(
        double MessageBackgroundOpacity,
        double FontSize,
        double LineSpacing,
        string FontFamily,
        bool ShowBadges,
        bool ShowTimestamps,
        bool UseTwitchNameColors,
        bool EmphasizeMentions,
        bool ShowEmotes,
        bool GiantEmotes,
        bool TextOutline,
        string MentionName)
    {
        public static RenderSettings From(AppSettings settings) => new(
            settings.MessageBackgroundOpacity,
            settings.FontSize,
            settings.LineSpacing,
            settings.FontFamily,
            settings.ShowBadges,
            settings.ShowTimestamps,
            settings.UseTwitchNameColors,
            settings.EmphasizeMentions,
            settings.ShowEmotes,
            settings.GiantEmotes,
            settings.TextOutline,
            string.IsNullOrWhiteSpace(settings.UserName) ? settings.Channel : settings.UserName);
    }
}
