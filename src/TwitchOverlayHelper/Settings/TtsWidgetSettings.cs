using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>Springs up to size. The loudest of the three.</summary>
    Pop
}

/// <summary>
/// The card the viewers see while a paid message is being read out loud.
///
/// <para>Off by default: without the card the reading page is a 1×1 source that only carries
/// sound, and showing it means the streamer has to resize that source in OBS.</para>
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
    /// short message is not stretched to fill it.
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
    /// Whether the message is written out as well as read out. The one switch here that puts a
    /// stranger's text on the broadcast; off leaves a card that only says who is speaking.
    /// </summary>
    public bool ShowText { get; set; } = true;

    /// <summary>Whether the card says what the reading cost. Off: the price is rarely the point.</summary>
    public bool ShowCost { get; set; }

    /// <summary>
    /// The little row of bars that moves while the voice is talking. Decorative, not an equaliser:
    /// the audio does not drive it.
    /// </summary>
    public bool ShowWave { get; set; } = true;

    /// <summary>A dark rim around the letters, for the same reason the stream chat has one.</summary>
    public bool TextOutline { get; set; } = true;

    public TtsWidgetAnimation Animation { get; set; } = TtsWidgetAnimation.Slide;

    /// <summary>
    /// How long the card stays after the last word, in milliseconds. Covers the gap between two
    /// readings in a queue, so the card does not blink out and straight back in.
    /// </summary>
    public int LingerMilliseconds { get; set; } = 900;

    public void Normalize()
    {
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Verdana" : FontFamily.Trim();
        AccentColor = IsHexColor(AccentColor) ? AccentColor : "#A970FF";
        // Cut rather than rejected: the caption only has the width of the card.
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

    /// <inheritdoc cref="AppSettings.Unknown"/>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
