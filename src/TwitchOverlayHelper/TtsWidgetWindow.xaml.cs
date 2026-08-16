using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TwitchOverlayHelper.Interop;
using TwitchOverlayHelper.Settings;

namespace TwitchOverlayHelper;

/// <summary>
/// Appearance of the card the viewers see while a paid message is read out loud. A window of its own
/// next to the dock's and the stream chat's, for the same reason those two are apart: it is a
/// surface with its own audience and its own questions. Nothing here changes what the streamer sees,
/// and nothing here decides whether a reading happens at all – that stays on the Uppläsning tab,
/// where the money is.
/// </summary>
public partial class TtsWidgetWindow : Window
{
    /// <summary>The colours the card can be given. A handful of clear names beats a colour picker.</summary>
    private static readonly (string Name, string Hex)[] Colors =
    [
        ("Lila", "#A970FF"), ("Turkos", "#5FD6C8"), ("Grön", "#22C55E"), ("Gul", "#FACC15"),
        ("Orange", "#F59E0B"), ("Röd", "#EF4444"), ("Rosa", "#DB2777"), ("Blå", "#3B82F6"), ("Vit", "#FFFFFF")
    ];

    private readonly AppSettings _settings;
    private readonly Action _onChanged;
    private readonly Func<string> _onPreview;
    private bool _loading = true;

    /// <param name="onPreview">
    /// Shows the card in OBS with an invented reading, and answers with what to tell the user – how
    /// many pages drew it, or that none are connected. The window has no way of knowing that itself:
    /// it can see the settings, but only the app can see who is listening.
    /// </param>
    public TtsWidgetWindow(AppSettings settings, Action onChanged, Func<string> onPreview)
    {
        InitializeComponent();
        DarkTitleBar.Enable(this);
        _settings = settings;
        _onChanged = onChanged;
        _onPreview = onPreview;
        PopulateColors();
        Populate();
        _loading = false;
    }

    private void PopulateColors()
    {
        foreach ((string name, string hex) in Colors)
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
            ColorBox.Items.Add(new ComboBoxItem { Content = row, Tag = hex });
        }
    }

    private void Populate()
    {
        TtsWidgetSettings widget = _settings.Tts.Widget;
        EnabledCheck.IsChecked = widget.Enabled;
        SelectPosition(widget.Position);
        OffsetXSlider.Value = widget.OffsetX;
        OffsetYSlider.Value = widget.OffsetY;
        WidthSlider.Value = widget.Width;
        FontBox.Text = widget.FontFamily;
        FontSizeSlider.Value = widget.FontSize;
        PlateSlider.Value = widget.BackgroundOpacity;
        RadiusSlider.Value = widget.CornerRadius;
        LingerSlider.Value = widget.LingerMilliseconds;
        OutlineCheck.IsChecked = widget.TextOutline;
        WaveCheck.IsChecked = widget.ShowWave;
        LabelBox.Text = widget.Label;
        NameCheck.IsChecked = widget.ShowName;
        TextCheck.IsChecked = widget.ShowText;
        CostCheck.IsChecked = widget.ShowCost;
        SelectColor(widget.AccentColor);
        SelectAnimation(widget.Animation);
        UpdateValueLabels();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        TtsWidgetSettings widget = _settings.Tts.Widget;
        widget.Enabled = EnabledCheck.IsChecked == true;
        widget.Position = SelectedPosition();
        widget.OffsetX = OffsetXSlider.Value;
        widget.OffsetY = OffsetYSlider.Value;
        widget.Width = WidthSlider.Value;
        widget.FontFamily = FontBox.Text;
        widget.FontSize = FontSizeSlider.Value;
        widget.BackgroundOpacity = PlateSlider.Value;
        widget.CornerRadius = RadiusSlider.Value;
        widget.LingerMilliseconds = (int)Math.Round(LingerSlider.Value);
        widget.TextOutline = OutlineCheck.IsChecked == true;
        widget.ShowWave = WaveCheck.IsChecked == true;
        widget.Label = LabelBox.Text;
        widget.ShowName = NameCheck.IsChecked == true;
        widget.ShowText = TextCheck.IsChecked == true;
        widget.ShowCost = CostCheck.IsChecked == true;
        widget.AccentColor = (ColorBox.SelectedItem as ComboBoxItem)?.Tag as string ?? widget.AccentColor;
        widget.Animation = (AnimationBox.SelectedItem as ComboBoxItem)?.Tag switch
        {
            "Fade" => TtsWidgetAnimation.Fade,
            "Pop" => TtsWidgetAnimation.Pop,
            "None" => TtsWidgetAnimation.None,
            _ => TtsWidgetAnimation.Slide
        };
        widget.Normalize();

        UpdateValueLabels();
        _onChanged();
    }

    private void UpdateValueLabels()
    {
        TtsWidgetSettings widget = _settings.Tts.Widget;
        OffsetXValue.Text = $"{widget.OffsetX:0} px";
        OffsetYValue.Text = $"{widget.OffsetY:0} px";
        WidthValue.Text = $"{widget.Width:0} px";
        FontSizeValue.Text = $"{widget.FontSize:0} px";
        PlateValue.Text = widget.BackgroundOpacity > 0.02 ? $"{widget.BackgroundOpacity:P0}" : "ingen";
        RadiusValue.Text = widget.CornerRadius > 0 ? $"{widget.CornerRadius:0} px" : "raka";
        LingerValue.Text = widget.LingerMilliseconds > 0 ? $"{widget.LingerMilliseconds / 1000.0:0.0} s" : "försvinner direkt";

        // The card is drawn by the reading page, and that page only ever gets a clip when the sound
        // is meant for OBS. Said here rather than left to be discovered on a live stream, where the
        // symptom – a card that never appears – says nothing about the reason.
        EnabledHint.Text = EnabledCheck.IsChecked != true
            ? "Av: browserkällan ritar ingenting och kan ligga kvar på 1×1 px."
            : _settings.Tts.UsesBrowser
                ? "Gör browserkällan för uppläsning lika stor som scenen i OBS – annars ritas rutan utanför bilden."
                : "Ljudet är satt till den här datorns högtalare, så uppläsningen går aldrig via browserkällan och rutan visas aldrig. Välj “Browser Source” eller “Båda” under Var ljudet hamnar.";
    }

    private void SelectPosition(TtsWidgetPosition position)
    {
        RadioButton button = position switch
        {
            TtsWidgetPosition.TopLeft => PosTopLeft,
            TtsWidgetPosition.TopCenter => PosTopCenter,
            TtsWidgetPosition.TopRight => PosTopRight,
            TtsWidgetPosition.MiddleLeft => PosMiddleLeft,
            TtsWidgetPosition.MiddleCenter => PosMiddleCenter,
            TtsWidgetPosition.MiddleRight => PosMiddleRight,
            TtsWidgetPosition.BottomLeft => PosBottomLeft,
            TtsWidgetPosition.BottomRight => PosBottomRight,
            _ => PosBottomCenter
        };
        button.IsChecked = true;
    }

    private TtsWidgetPosition SelectedPosition()
    {
        if (PosTopLeft.IsChecked == true) return TtsWidgetPosition.TopLeft;
        if (PosTopCenter.IsChecked == true) return TtsWidgetPosition.TopCenter;
        if (PosTopRight.IsChecked == true) return TtsWidgetPosition.TopRight;
        if (PosMiddleLeft.IsChecked == true) return TtsWidgetPosition.MiddleLeft;
        if (PosMiddleCenter.IsChecked == true) return TtsWidgetPosition.MiddleCenter;
        if (PosMiddleRight.IsChecked == true) return TtsWidgetPosition.MiddleRight;
        if (PosBottomLeft.IsChecked == true) return TtsWidgetPosition.BottomLeft;
        if (PosBottomRight.IsChecked == true) return TtsWidgetPosition.BottomRight;
        return TtsWidgetPosition.BottomCenter;
    }

    private void SelectColor(string hex)
    {
        foreach (object item in ColorBox.Items)
            if (item is ComboBoxItem option && string.Equals(option.Tag as string, hex, StringComparison.OrdinalIgnoreCase))
            {
                ColorBox.SelectedItem = option;
                return;
            }
        ColorBox.SelectedIndex = 0;
    }

    private void SelectAnimation(TtsWidgetAnimation animation)
    {
        string tag = animation.ToString();
        foreach (object item in AnimationBox.Items)
            if (item is ComboBoxItem option && string.Equals(option.Tag as string, tag, StringComparison.Ordinal))
            {
                AnimationBox.SelectedItem = option;
                return;
            }
        AnimationBox.SelectedIndex = 0;
    }

    private void Preview_Click(object sender, RoutedEventArgs e) => PreviewStatus.Text = _onPreview();

    /// <summary>
    /// Back to the defaults – including switched off, which is the default. Deliberately: this button
    /// is what somebody reaches for when the card has ended up somewhere they cannot see it, and
    /// leaving it on would leave them looking for it again.
    /// </summary>
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _settings.Tts.Widget = new TtsWidgetSettings();
        _loading = true;
        Populate();
        _loading = false;
        _onChanged();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
