using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchOverlayHelper.Settings;

/// <summary>
/// The wait screen with the fight: who stands in which corner, where they fight, what the banner
/// says and what the chat may do about it.
///
/// <para>The commands name a corner rather than a fighter: <c>!heja 1</c> is player 1, whoever
/// that is today. A streamer who swaps characters should not have to teach the chat new words.</para>
/// </summary>
public sealed class FightSettings
{
    /// <summary>Fighter id in player 1's corner, on the left.</summary>
    public string Player1 { get; set; } = "silver";

    /// <summary>Fighter id in player 2's corner, on the right.</summary>
    public string Player2 { get; set; } = "ink";

    /// <summary>Arena id, or <c>none</c> for no background at all.</summary>
    public string Arena { get; set; } = "garage";

    /// <summary>The big line in the banner: why the stream is waiting. Empty hides the banner.</summary>
    public string Headline { get; set; } = "Strax tillbaka";

    /// <summary>The smaller line under it. Empty leaves it out.</summary>
    public string Subline { get; set; } = "Under tiden gör de upp om saken själva";

    /// <summary>Banner texts the streamer has saved, to pick again with one click. Newest first.</summary>
    public List<FightText> SavedTexts { get; set; } = [];

    /// <summary>The suggestions that ship with the app. Not stored: they are the app's, not the streamer's.</summary>
    public static IReadOnlyList<FightText> Suggestions { get; } =
    [
        new() { Headline = "Strax tillbaka", Subline = "Under tiden gör de upp om saken själva" },
        new() { Headline = "Streamern är AFK", Subline = "Hämtar kaffe – eller gömmer sig från fajten" },
        new() { Headline = "Karaktären är medvetslös", Subline = "Väntar på hjälp – här är en fajt så länge" },
        new() { Headline = "Tekniskt strul", Subline = "Vi lagar det – de här två väntar inte" },
        new() { Headline = "Snart börjar det", Subline = "Uppvärmningen pågår" }
    ];

    /// <summary>How many texts can be saved. A list longer than this is no longer a quick pick.</summary>
    public const int MaxSavedTexts = 30;

    /// <summary>
    /// Saves the current banner as a text of its own and answers whether anything was added. The
    /// same text twice is one entry, moved to the top, and an empty banner is not worth saving.
    /// </summary>
    public bool SaveCurrentText()
    {
        var text = new FightText { Headline = Headline, Subline = Subline };
        text.Normalize();
        if (text.IsEmpty) return false;
        SavedTexts.RemoveAll(saved => saved.SameAs(text));
        SavedTexts.Insert(0, text);
        if (SavedTexts.Count > MaxSavedTexts) SavedTexts.RemoveRange(MaxSavedTexts, SavedTexts.Count - MaxSavedTexts);
        return true;
    }

    /// <summary>Whether the chat can take sides at all.</summary>
    public bool CommandsEnabled { get; set; } = true;

    /// <summary>Cheering a corner on fills that player's super meter – full, and the next blow is a super.</summary>
    public string CheerCommand { get; set; } = "!heja";

    /// <summary>Patching a corner up gives that player a little health back.</summary>
    public string HealCommand { get; set; } = "!hela";

    /// <summary>How long one viewer waits between two commands, so one person cannot decide a round.</summary>
    public int CooldownSeconds { get; set; } = 20;

    /// <summary>A line on the screen telling the viewers what to type.</summary>
    public bool ShowCommandHint { get; set; } = true;

    public void Normalize()
    {
        Player1 = Clean(Player1, "silver");
        Player2 = Clean(Player2, "ink");
        Arena = Clean(Arena, "garage");
        Headline = Trim(Headline, 80);
        Subline = Trim(Subline, 140);
        // A hand-edited file can hold blanks, duplicates or nulls; what is left is a clean list.
        var kept = new List<FightText>();
        foreach (FightText? text in SavedTexts ?? [])
        {
            if (text is null) continue;
            text.Normalize();
            if (!text.IsEmpty && !kept.Any(other => other.SameAs(text))) kept.Add(text);
        }
        SavedTexts = kept.Take(MaxSavedTexts).ToList();
        CheerCommand = Command(CheerCommand, "!heja");
        HealCommand = Command(HealCommand, "!hela");
        // One word meaning two things would make every heal a cheer; the heal yields.
        if (string.Equals(CheerCommand, HealCommand, StringComparison.OrdinalIgnoreCase))
            HealCommand = string.Equals(CheerCommand, "!hela", StringComparison.OrdinalIgnoreCase) ? "!plåster" : "!hela";
        CooldownSeconds = Math.Clamp(CooldownSeconds, 0, 600);
    }

    /// <summary>
    /// The command as chat will write it, with this feature's own fallback. CleanCommand's is the
    /// mod call "!psst", and a box cleared down to "!" must not turn a cheer into a call for help.
    /// </summary>
    private static string Command(string? value, string fallback)
    {
        string first = (value ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return first.TrimStart('!').Length == 0 ? fallback : EdgeAlertSettings.CleanCommand(first);
    }

    internal static string Trim(string? text, int max)
    {
        string clean = (text ?? string.Empty).Trim();
        return clean.Length > max ? clean[..max] : clean;
    }

    private static string Clean(string? id, string fallback)
    {
        string clean = Fight.FightCatalog.SanitizeId(id ?? string.Empty);
        return clean.Length > 0 ? clean : fallback;
    }

    /// <inheritdoc cref="AppSettings.Unknown"/>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>One banner: the big line and the one under it.</summary>
public sealed class FightText
{
    public string Headline { get; set; } = string.Empty;
    public string Subline { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsEmpty => Headline.Length == 0 && Subline.Length == 0;

    /// <summary>What the list shows: the headline, with the line under it after a dash.</summary>
    [JsonIgnore]
    public string Label => Headline.Length == 0 ? Subline : Subline.Length == 0 ? Headline : $"{Headline} – {Subline}";

    public bool SameAs(FightText other) =>
        string.Equals(Headline, other.Headline, StringComparison.Ordinal)
        && string.Equals(Subline, other.Subline, StringComparison.Ordinal);

    public void Normalize()
    {
        Headline = FightSettings.Trim(Headline, 80);
        Subline = FightSettings.Trim(Subline, 140);
    }

    /// <inheritdoc cref="AppSettings.Unknown"/>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
