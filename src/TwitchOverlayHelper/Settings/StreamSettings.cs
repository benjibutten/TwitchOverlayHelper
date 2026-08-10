namespace TwitchOverlayHelper.Settings;

/// <summary>
/// The chat as the viewers see it: a transparent browser source laid over the stream.
///
/// <para>Its own settings rather than a second reading of the dock's, because the two are read by
/// different people at different distances. The dock is the streamer's own surface – it may show
/// everything the app knows, and it is read at arm's length. This page is on the broadcast: it is
/// glanced at in a corner of a video, so it is bigger, shorter and carries only what a viewer has
/// any business seeing. What is deliberately absent is listed in the page itself.</para>
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
    /// off, which is what a chat box in a fixed corner usually wants; a number suits an overlay lying
    /// over the game, where an old line during a quiet stretch is just something in the way.
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
    /// <summary>Off by default: the viewers can see the clock on their own stream.</summary>
    public bool ShowTimestamps { get; set; }
    /// <summary>The quiet "answering X" line above a reply, so an answer is not read as a new thought.</summary>
    public bool ShowReplies { get; set; } = true;
    /// <summary>Collapses addresses to a small chip, so nobody can put a readable link on your stream.</summary>
    public bool CollapseLinks { get; set; } = true;
    /// <summary>Rewrites shouted lines to sentence case. Off by default – on stream chat is quoted as said.</summary>
    public bool CalmShouting { get; set; }

    /// <summary>
    /// Hides "!command" lines outright rather than dimming them the way the dock does. The dock dims
    /// because the streamer may still want to see who asked for what; on the broadcast a command is
    /// noise with a bot answer coming right behind it.
    /// </summary>
    public bool HideCommands { get; set; } = true;

    /// <summary>
    /// Accounts whose lines never reach the stream, written the way a person would: names separated
    /// by comma, space or newline. Chat bots by default, because their answers are the other half of
    /// the command traffic hidden above.
    ///
    /// <para>Kept as the text that was typed rather than as a parsed list. The page is the only thing
    /// that ever looks a name up, so it does the splitting, and the settings file holds exactly what
    /// the user wrote instead of that plus a copy of it they cannot edit.</para>
    /// </summary>
    public string IgnoredAccounts { get; set; } = "nightbot, streamelements, streamlabs, moobot, fossabot, sery_bot";

    /// <summary>
    /// Which event cards the stream overlay draws. A third list rather than a shared one: subs and
    /// raids on the broadcast are a thank-you to the viewers, and that is a different decision from
    /// what the streamer wants in their own column.
    ///
    /// <para><see cref="ChatEventVisibility.HypeTrain"/> is the one member this view never reads, and
    /// there is deliberately no switch for it in the settings window. Twitch already puts a hype
    /// train in front of the viewers itself, on every player watching – drawing a second one over the
    /// video would be this app telling them something they can already see. The field is only here
    /// because the three views share one shape.</para>
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
}
