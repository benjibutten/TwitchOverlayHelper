namespace TwitchOverlayHelper.Pets;

/// <summary>
/// How rare a winnable pet is in the lucky spin, as a small set of named tiers rather than a free
/// number. Names because they are written by hand into pet.json and shown in a dropdown; a weight
/// typo of 1000 instead of 100 would silently drown every other pet, while an unknown name here
/// just becomes "vanlig".
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
    /// The tier's share of the draw, relative to the others in the same spin. A legendary next to a
    /// common wins roughly one time in thirty-four – rare enough to be an event, common enough that
    /// a channel actually sees one.
    /// </summary>
    public static int Weight(string rarity) => Normalize(rarity) switch
    {
        Uncommon => 40,
        Rare => 12,
        Legendary => 3,
        _ => 100
    };

    /// <summary>The tier a hand-written value means, with everything unrecognised read as common.</summary>
    public static string Normalize(string? rarity)
    {
        string trimmed = (rarity ?? string.Empty).Trim();
        foreach (string tier in All)
            if (string.Equals(trimmed, tier, StringComparison.OrdinalIgnoreCase)) return tier;
        return Common;
    }
}
