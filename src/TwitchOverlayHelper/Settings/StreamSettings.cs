using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchOverlayHelper.Settings;

/// <summary>
/// The chat as the viewers see it: a transparent browser source laid over the stream.
///
/// <para>Its own settings rather than the dock's: the dock is the streamer's own surface and may
/// show everything the app knows, while this page is on the broadcast – bigger, shorter, and
/// carrying only what a viewer has any business seeing.</para>
/// </summary>
public sealed class StreamSettings
{
    public double FontSize { get; set; } = 26;
    public string FontFamily { get; set; } = "Verdana";
    public double LineHeight { get; set; } = 1.35;
    public double MessageGap { get; set; } = 8;

    /// <summary>How many lines the column holds. Small on purpose: a viewer reads the newest few.</summary>
    public int MaxMessages { get; set; } = 12;

    /// <summary>
    /// Seconds a line stays before it fades out on its own. 0 leaves it until a newer line pushes it
    /// off.
    /// </summary>
    public int FadeAfterSeconds { get; set; }

    /// <summary>Newest line at the top instead of the bottom, for a box anchored to the top edge.</summary>
    public bool NewestOnTop { get; set; }

    /// <summary>Slides new lines in. Off is one less thing moving in the corner of the picture.</summary>
    public bool Animate { get; set; } = true;

    /// <summary>The plate behind each line. Enough of one to keep text readable over a bright scene.</summary>
    public double MessageBackgroundOpacity { get; set; } = 0.35;

    /// <summary>A dark rim around the letters. The one thing that survives any background.</summary>
    public bool TextOutline { get; set; } = true;

    public bool NameOnOwnLine { get; set; }
    public bool ShowBadges { get; set; } = true;
    public bool UseTwitchNameColors { get; set; } = true;
    public bool ShowEmotes { get; set; } = true;
    public bool GiantEmotes { get; set; } = true;
    /// <summary>
    /// Draws the GIFs tier 2 and tier 3 subscribers can send. Off leaves the line as Twitch worded
    /// it – "[Yes GIF by Someone]".
    /// </summary>
    public bool ShowGifs { get; set; } = true;
    /// <summary>Off by default: the viewers can see the clock on their own stream.</summary>
    public bool ShowTimestamps { get; set; }
    /// <summary>The quiet "answering X" line above a reply, so an answer is not read as a new thought.</summary>
    public bool ShowReplies { get; set; } = true;
    /// <summary>Collapses addresses to a small chip, so nobody can put a readable link on your stream.</summary>
    public bool CollapseLinks { get; set; } = true;
    /// <summary>Rewrites shouted lines to sentence case. Off by default – on stream chat is quoted as said.</summary>
    public bool CalmShouting { get; set; }

    /// <summary>
    /// Hides "!command" lines outright rather than dimming them the way the dock does: on the
    /// broadcast a command is noise with a bot answer coming right behind it.
    /// </summary>
    public bool HideCommands { get; set; } = true;

    /// <summary>
    /// Accounts whose lines never reach the stream: names separated by comma, space or newline.
    /// Chat bots by default, because their answers are the other half of the hidden command traffic.
    ///
    /// <para>Kept as the text that was typed rather than as a parsed list; the page does the
    /// splitting.</para>
    /// </summary>
    public string IgnoredAccounts { get; set; } = "nightbot, streamelements, streamlabs, moobot, fossabot, sery_bot";

    /// <summary>
    /// Which event cards the stream overlay draws. A list of its own: what the viewers are shown is
    /// a different decision from what the streamer wants in their own column.
    ///
    /// <para><see cref="ChatEventVisibility.HypeTrain"/> is never read by this view and has no
    /// switch in the settings window: Twitch already shows a hype train on every player. The field
    /// is only here because the three views share one shape.</para>
    /// </summary>
    public ChatEventVisibility Events { get; set; } = new();

    public void Normalize()
    {
        Events ??= new ChatEventVisibility();
        FontSize = Math.Clamp(FiniteOrDefault(FontSize, 26), 14, 64);
        LineHeight = Math.Clamp(FiniteOrDefault(LineHeight, 1.35), 1.1, 2.2);
        MessageGap = Math.Clamp(FiniteOrDefault(MessageGap, 8), 0, 40);
        MessageBackgroundOpacity = Math.Clamp(FiniteOrDefault(MessageBackgroundOpacity, 0.35), 0, 0.9);
        MaxMessages = Math.Clamp(MaxMessages, 1, 60);
        FadeAfterSeconds = Math.Clamp(FadeAfterSeconds, 0, 600);
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Verdana" : FontFamily.Trim();
        IgnoredAccounts ??= string.Empty;
    }

    private static double FiniteOrDefault(double value, double fallback) => double.IsFinite(value) ? value : fallback;

    /// <inheritdoc cref="AppSettings.Unknown"/>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
