namespace TwitchOverlayHelper.Settings;

/// <summary>
/// Appearance of the OBS browser dock. Deliberately separate from the overlay's settings: the dock
/// is read at rest in a narrow column, the overlay is glanced at over a game.
/// </summary>
public sealed class DockSettings
{
    public double FontSize { get; set; } = 20;
    public double LineHeight { get; set; } = 1.6;
    public double LetterSpacing { get; set; } = 0.02;
    public double WordSpacing { get; set; } = 0.16;
    public double MessageGap { get; set; } = 10;
    public string FontFamily { get; set; } = "Verdana";
    public string Theme { get; set; } = "cream";
    public int MaxMessages { get; set; } = 120;

    public bool ZebraRows { get; set; } = true;
    public bool NameOnOwnLine { get; set; } = true;
    public bool ShowBadges { get; set; } = true;
    public bool ShowTimestamps { get; set; }
    public bool UseTwitchNameColors { get; set; } = true;
    public bool ShowEmotes { get; set; } = true;
    /// <summary>
    /// Draws an emote a Gigantify power-up enlarged at its full size, on a row of its own. On by
    /// default: someone spent bits to make it big, and shrinking it back would throw that away.
    /// Off leaves the emote at reading size with a "⚡ förstorad" marker, for a calm column.
    /// </summary>
    public bool GiantEmotes { get; set; } = true;

    /// <summary>Collapses URLs to a short chip so link noise does not break the reading rhythm.</summary>
    public bool CollapseLinks { get; set; } = true;
    /// <summary>Rewrites shouted messages to sentence case; all-caps is markedly harder to decode.</summary>
    public bool CalmShouting { get; set; } = true;
    /// <summary>Dims "!command" lines and known bot chatter without hiding them.</summary>
    public bool DimCommands { get; set; } = true;

    /// <summary>Upper bound on how fast new messages are revealed, in messages per second. 0 disables the limit.</summary>
    public double MessagesPerSecond { get; set; } = 2.5;
    /// <summary>Keeps messages that mention the streamer in a pinned strip so questions do not scroll away.</summary>
    public bool PinMentions { get; set; } = true;
    public int PinnedMentionSeconds { get; set; } = 90;

    public void Normalize()
    {
        FontSize = Math.Clamp(FiniteOrDefault(FontSize, 20), 12, 48);
        LineHeight = Math.Clamp(FiniteOrDefault(LineHeight, 1.6), 1.2, 2.4);
        LetterSpacing = Math.Clamp(FiniteOrDefault(LetterSpacing, 0.02), 0, 0.25);
        WordSpacing = Math.Clamp(FiniteOrDefault(WordSpacing, 0.16), 0, 1);
        MessageGap = Math.Clamp(FiniteOrDefault(MessageGap, 10), 0, 40);
        MessagesPerSecond = Math.Clamp(FiniteOrDefault(MessagesPerSecond, 2.5), 0, 30);
        MaxMessages = Math.Clamp(MaxMessages, 20, 500);
        PinnedMentionSeconds = Math.Clamp(PinnedMentionSeconds, 10, 600);
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Verdana" : FontFamily.Trim();
        Theme = Theme is "cream" or "dark" or "light" or "peach" or "mint" ? Theme : "cream";
    }

    private static double FiniteOrDefault(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}
