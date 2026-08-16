namespace TwitchOverlayHelper.Settings;

/// <summary>Where in the picture the card sits. Nine anchors, the way an OBS source is placed.</summary>
public enum TtsWidgetPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    MiddleCenter,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

/// <summary>How the card arrives and leaves.</summary>
public enum TtsWidgetAnimation
{
    /// <summary>It is simply there, and then it is not. The cheapest thing to encode.</summary>
    None,

    Fade,

    /// <summary>Slides in from the edge it is anchored to.</summary>
    Slide,

    /// <summary>Springs up to size. The loudest of the three, for a card that is the point.</summary>
    Pop
}

/// <summary>
/// The card the viewers see while a paid message is being read out loud.
///
/// <para>Its own settings rather than a corner of <see cref="StreamSettings"/>: that one is a column
/// of chat that is always there and read in passing, this one appears for the twenty seconds a
/// reading lasts and is the thing on screen while it does. What they do share is the medium – both
/// are drawn over video nobody knows the colour of – so the plate behind the text and the rim around
/// the letters exist here for the same reason they exist there.</para>
///
/// <para>Off by default, and deliberately: the reading page has always been a 1×1 source that only
/// carries sound, and an upgrade must not put a card on somebody's stream because they installed a
/// new version. Turning it on is also the moment the source has to be resized in OBS, which is
/// something only the streamer can do.</para>
/// </summary>
public sealed class TtsWidgetSettings
{
    /// <summary>
    /// Whether the reading page draws anything at all. Also decides what the app is willing to send
    /// it: with the card off, the viewer's name and words never leave the app for that page – see
    /// <see cref="Web.BrowserTtsOutput"/>.
    /// </summary>
    public bool Enabled { get; set; }

    public TtsWidgetPosition Position { get; set; } = TtsWidgetPosition.BottomCenter;

    /// <summary>How far in from the left or right edge, in pixels. Ignored by the centred anchors.</summary>
    public double OffsetX { get; set; } = 64;

    /// <summary>How far in from the top or bottom edge, in pixels. Ignored by the middle row.</summary>
    public double OffsetY { get; set; } = 64;

    /// <summary>
    /// The widest the card may grow, in pixels of the browser source. A limit rather than a size: a
    /// three-word message should not be stretched across the screen to fill a box.
    /// </summary>
    public double Width { get; set; } = 720;

    public double FontSize { get; set; } = 26;

    public string FontFamily { get; set; } = "Verdana";

    /// <summary>Hex colour, "#RRGGBB". The name, the caption and the bars are drawn in it.</summary>
    public string AccentColor { get; set; } = "#A970FF";

    /// <summary>The plate behind the text. Zero leaves only the letters and their rim.</summary>
    public double BackgroundOpacity { get; set; } = 0.72;

    public double CornerRadius { get; set; } = 16;

    /// <summary>
    /// The small line above the message, so the card says what it is rather than looking like chat.
    /// Empty leaves it out.
    /// </summary>
    public string Label { get; set; } = "LÄSER UPP";

    public bool ShowName { get; set; } = true;

    /// <summary>
    /// Whether the message is written out as well as read out. On by default – a viewer who paid for
    /// their words wants them seen, and a synthetic voice is not always caught first time – but it is
    /// the one switch here that puts a stranger's text on the broadcast, so it can be turned off and
    /// leave a card that only says who is speaking.
    /// </summary>
    public bool ShowText { get; set; } = true;

    /// <summary>Whether the card says what the reading cost. Off: the price is rarely the point.</summary>
    public bool ShowCost { get; set; }

    /// <summary>
    /// The little row of bars that moves while the voice is talking. Not a real equaliser – the audio
    /// is nowhere near this element – but it is what makes the card read as "this is being said now"
    /// rather than as another notification that has got stuck.
    /// </summary>
    public bool ShowWave { get; set; } = true;

    /// <summary>A dark rim around the letters, for the same reason the stream chat has one.</summary>
    public bool TextOutline { get; set; } = true;

    public TtsWidgetAnimation Animation { get; set; } = TtsWidgetAnimation.Slide;

    /// <summary>
    /// How long the card stays after the last word, in milliseconds. A short pause reads as the card
    /// finishing rather than being cut off, and it covers the gap between two readings in a queue –
    /// without it the card would blink out and straight back in.
    /// </summary>
    public int LingerMilliseconds { get; set; } = 900;

    public void Normalize()
    {
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Verdana" : FontFamily.Trim();
        AccentColor = IsHexColor(AccentColor) ? AccentColor : "#A970FF";
        // Trimmed rather than rejected: a caption is a handful of words, and the space it has is the
        // width of the card.
        Label = (Label ?? string.Empty).Trim();
        if (Label.Length > 40) Label = Label[..40];
        OffsetX = Clamp(OffsetX, 0, 600, 64);
        OffsetY = Clamp(OffsetY, 0, 600, 64);
        Width = Clamp(Width, 240, 1800, 720);
        FontSize = Clamp(FontSize, 12, 72, 26);
        BackgroundOpacity = Clamp(BackgroundOpacity, 0, 1, 0.72);
        CornerRadius = Clamp(CornerRadius, 0, 48, 16);
        LingerMilliseconds = Math.Clamp(LingerMilliseconds, 0, 5000);
        if (!Enum.IsDefined(Position)) Position = TtsWidgetPosition.BottomCenter;
        if (!Enum.IsDefined(Animation)) Animation = TtsWidgetAnimation.Slide;
    }

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static bool IsHexColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
}
