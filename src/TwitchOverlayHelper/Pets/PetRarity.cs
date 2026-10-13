namespace TwitchOverlayHelper.Pets;

/// <summary>
/// How rare a winnable pet is in the lucky spin, as a small set of named tiers. Names rather than
/// numeric weights because they are written by hand into pet.json: a mistyped weight would skew
/// the whole draw, while an unknown name just becomes "vanlig".
/// </summary>
public static class PetRarity
{
    public const string Common = "vanlig";
    public const string Uncommon = "ovanlig";
    public const string Rare = "sällsynt";
    public const string Legendary = "legendarisk";

    /// <summary>Every tier, common first – the order the settings dropdown shows them in.</summary>
    public static readonly IReadOnlyList<string> All = [Common, Uncommon, Rare, Legendary];

    /// <summary>
    /// The tier's share of the draw, relative to the others in the same spin: one legendary next to
    /// one common wins roughly one time in thirty-four.
    /// </summary>
    public static int Weight(string rarity) => Normalize(rarity) switch
    {
        Uncommon => 40,
        Rare => 12,
        Legendary => 3,
        _ => 100
    };

    /// <summary>The canonical tier name for a value, ignoring case; anything unrecognised is common.</summary>
    public static string Normalize(string? rarity)
    {
        string trimmed = (rarity ?? string.Empty).Trim();
        foreach (string tier in All)
            if (string.Equals(trimmed, tier, StringComparison.OrdinalIgnoreCase)) return tier;
        return Common;
    }
}
