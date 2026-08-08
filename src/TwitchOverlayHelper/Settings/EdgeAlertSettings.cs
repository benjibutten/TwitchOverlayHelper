using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Settings;

/// <summary>
/// The edge glow: a soft band of light that fades in along the screen's edges and out again, to
/// catch the streamer's eye without putting anything readable on top of the game. Two things light
/// it, each with a colour of its own – a moderator writing the call command, and a first-time
/// chatter – so the colour alone says which of the two just happened.
/// </summary>
public sealed class EdgeAlertSettings
{
    /// <summary>Lit when the streamer or a moderator writes <see cref="ModCommand"/> in chat.</summary>
    public EdgeAlertStyle ModAlert { get; set; } = new() { Color = "#F59E0B" };

    /// <summary>Lit when someone writes in the channel for the first time ever.</summary>
    public EdgeAlertStyle NewChatterAlert { get; set; } = new() { Color = "#5FD6C8" };

    /// <summary>
    /// The chat command that lights the mod glow. Only the broadcaster and moderators can trigger
    /// it – a viewer writing the same word does nothing.
    /// </summary>
    public string ModCommand { get; set; } = "!psst";

    /// <summary>How far in from the edge the light reaches, in pixels. Shared by both alerts.</summary>
    public double EdgeWidth { get; set; } = 160;

    public bool TriggersModAlert(ChatMessage message)
    {
        if (!ModAlert.Enabled) return false;
        if (!message.IsBroadcaster && !message.IsModerator) return false;
        string command = CleanCommand(ModCommand);
        string text = message.Text.Trim();
        // "!psst kolla chatten" counts too – the words after the command are for the humans.
        return text.Equals(command, StringComparison.OrdinalIgnoreCase)
            || text.StartsWith(command + " ", StringComparison.OrdinalIgnoreCase);
    }

    public bool TriggersNewChatterAlert(ChatMessage message) =>
        NewChatterAlert.Enabled && message.IsFirstMessage;

    /// <summary>
    /// The command as chat will have to write it: one word, always starting with "!". Whatever was
    /// typed into the settings box – spaces, a missing "!", extra words – comes out usable.
    /// </summary>
    public static string CleanCommand(string? value)
    {
        string first = (value ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        if (first.Length == 0 || first == "!") return "!psst";
        return first.StartsWith('!') ? first : "!" + first;
    }

    public void Normalize()
    {
        ModAlert ??= new EdgeAlertStyle { Color = "#F59E0B" };
        NewChatterAlert ??= new EdgeAlertStyle { Color = "#5FD6C8" };
        ModAlert.Normalize("#F59E0B");
        NewChatterAlert.Normalize("#5FD6C8");
        ModCommand = CleanCommand(ModCommand);
        EdgeWidth = double.IsFinite(EdgeWidth) ? Math.Clamp(EdgeWidth, 60, 320) : 160;
    }
}

/// <summary>How one of the edge glows looks: its colour, how strong it gets, and how long it stays.</summary>
public sealed class EdgeAlertStyle
{
    public bool Enabled { get; set; } = true;

    /// <summary>Hex colour, "#RRGGBB".</summary>
    public string Color { get; set; } = "#F59E0B";

    /// <summary>Peak opacity of the glow, 0.15–1.</summary>
    public double Intensity { get; set; } = 0.7;

    /// <summary>Seconds from fade-in to fully gone.</summary>
    public double DurationSeconds { get; set; } = 6;

    public void Normalize(string fallbackColor)
    {
        Color = IsHexColor(Color) ? Color : fallbackColor;
        Intensity = double.IsFinite(Intensity) ? Math.Clamp(Intensity, 0.15, 1) : 0.7;
        DurationSeconds = double.IsFinite(DurationSeconds) ? Math.Clamp(DurationSeconds, 2, 20) : 6;
    }

    private static bool IsHexColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
}
