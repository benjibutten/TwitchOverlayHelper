using System.Text;

namespace TwitchOverlayHelper.Speech;

/// <summary>
/// Turns what a viewer typed into something worth sending to a voice model: handles a wall of
/// newlines, a hundred exclamation marks, a pasted paragraph.
/// </summary>
public static class TtsText
{
    /// <summary>
    /// How many times a character may repeat before the rest are dropped. Synthesis is billed per
    /// character, and a longer run sounds no different.
    /// </summary>
    private const int MaxRun = 3;

    /// <summary>
    /// The message as it should be spoken, or an empty string when there is nothing to say.
    ///
    /// <para>Cut at <paramref name="maxCharacters"/>, on the last word boundary before the limit
    /// when there is one.</para>
    /// </summary>
    public static string Clean(string? text, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);
        char previous = '\0';
        int run = 0;
        foreach (char raw in text)
        {
            // Newlines and tabs become spaces rather than disappearing: they separate words, and a
            // message written as a list would otherwise have its lines run together.
            char current = char.IsControl(raw) ? ' ' : raw;
            if (current == ' ')
            {
                // Collapsed here rather than by a second pass, so the run counter never sees a
                // stretch of whitespace as a repeated character.
                if (builder.Length == 0 || builder[^1] == ' ') continue;
                builder.Append(' ');
                previous = ' ';
                run = 1;
                continue;
            }

            run = current == previous ? run + 1 : 1;
            previous = current;
            if (run > MaxRun) continue;
            builder.Append(current);
        }

        string cleaned = builder.ToString().Trim();
        int limit = Math.Max(1, maxCharacters);
        if (cleaned.Length <= limit) return cleaned;

        string cut = cleaned[..limit];
        int lastSpace = cut.LastIndexOf(' ');
        // Only when the boundary is far enough in to leave a sentence behind; a message whose first
        // word is longer than the whole limit is cut where the limit is.
        return (lastSpace > limit / 2 ? cut[..lastSpace] : cut).TrimEnd();
    }
}
