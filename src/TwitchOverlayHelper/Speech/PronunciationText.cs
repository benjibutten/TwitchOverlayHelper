using System.Text.RegularExpressions;

namespace TwitchOverlayHelper.Speech;

/// <summary>
/// Turns whatever the language model answered into the single short line the voice model should
/// read. The prompt asks for exactly that, but a stray label, arrow or pair of quotes would
/// otherwise be spoken out loud – so the answer is trimmed here rather than trusted.
/// </summary>
internal static partial class PronunciationText
{
    /// <summary>A spoken name is a handful of words; anything longer is the model explaining itself.</summary>
    private const int MaxLength = 120;

    public static string Clean(string? answer, string fallback)
    {
        string line = FirstMeaningfulLine(answer);
        line = AfterLastArrow(line);
        line = StripWrappers(line);
        line = StripLabel(line);
        line = Whitespace().Replace(line, " ").Trim();
        if (line.Length > MaxLength) line = line[..MaxLength].TrimEnd();
        return line.Length > 0 ? line : fallback.Trim();
    }

    private static string FirstMeaningfulLine(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return string.Empty;
        foreach (string candidate in answer.Split('\n'))
        {
            string line = candidate.Trim().Trim('-', '*', '•', ' ');
            if (line.Length > 0) return line;
        }
        return string.Empty;
    }

    /// <summary>"xSwEx → Ex swee" keeps only the spoken half.</summary>
    private static string AfterLastArrow(string line)
    {
        int arrow = line.LastIndexOf('→');
        if (arrow >= 0) return line[(arrow + 1)..];
        int ascii = line.LastIndexOf("->", StringComparison.Ordinal);
        return ascii >= 0 ? line[(ascii + 2)..] : line;
    }

    private static string StripWrappers(string line)
    {
        string trimmed = line.Trim();
        while (trimmed.Length > 1 && IsWrapper(trimmed[0]) && IsWrapper(trimmed[^1]))
            trimmed = trimmed[1..^1].Trim();
        return trimmed.Trim('`', '*', '_');
    }

    private static bool IsWrapper(char character) => character is '"' or '\'' or '`' or '«' or '»' or '“' or '”' or '*' or '_';

    private static string StripLabel(string line)
    {
        Match match = Label().Match(line);
        return match.Success ? line[match.Length..].Trim() : line;
    }

    [GeneratedRegex(@"^(uttal|svar|taltext|användarnamn|namn|output|resultat)\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Label();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
