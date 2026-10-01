using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchOverlayHelper.Settings;

/// <summary>
/// Lyckosnurren: a channel point reward that spins through the winnable pets on the overlay and
/// lands on one, which the viewer then owns for good. The wins themselves live in
/// <c>Spins.SpinWinStore</c>; this is only how the feature is bought and shown.
///
/// <para>One reward rather than a rule list like the pets: a spin is a spin, there is no "spin for
/// ten minutes" to sell alongside it.</para>
/// </summary>
public sealed class SpinSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Twitch reward id of the spin reward. Empty means no reward triggers a spin.</summary>
    public string RewardId { get; set; } = string.Empty;

    /// <summary>The reward's name on stream, matched alongside the id like the pet rules do.</summary>
    public string RewardName { get; set; } = string.Empty;

    /// <inheritdoc cref="PetRewardRule.Managed"/>
    public bool Managed { get; set; }

    /// <summary>The price the reward was created with. Twitch's own copy is the truth; this is a note.</summary>
    public int Cost { get; set; } = 5000;

    /// <summary>Which edge the spin panel slides in from: "left", "right", "top" or "bottom".</summary>
    public string Side { get; set; } = "right";

    /// <summary>How long the reel spins before it lands, seconds.</summary>
    public int SpinSeconds { get; set; } = 8;

    /// <summary>The headline over the reel. The streamer's to word, like everything the viewers see.</summary>
    public string Title { get; set; } = "Lyckosnurren";

    /// <summary>
    /// How long a duplicate win waits for its winner to name a recipient before the points go back.
    /// </summary>
    public int GiftTimeoutMinutes { get; set; } = 10;

    /// <summary>What a viewer types to see their wins.</summary>
    public string ListCommand { get; set; } = "!mina";

    /// <summary>What a winner types, followed by a name, to give a duplicate win away.</summary>
    public string GiveCommand { get; set; } = "!ge";

    /// <summary>
    /// What anyone types to see the whole pool: every art the reel can land on and how rare each is.
    /// The counterpart to <see cref="ListCommand"/> – that one answers "what do I have", this one
    /// "what is there", which is the question somebody has to answer before the price looks worth it.
    /// </summary>
    public string PoolCommand { get; set; } = "!alla";

    /// <summary>
    /// What a winner types to put a duplicate win on the lawn themselves instead of giving it away.
    /// Spends the gift the same way a gift does.
    /// </summary>
    public string ReleaseCommand { get; set; } = "!släpp";

    /// <inheritdoc cref="PetRewardRule.CanRefund"/>
    public bool CanRefund => Managed && RewardId.Length > 0;

    public bool MatchesReward(string? rewardId, string? rewardTitle) =>
        (RewardId.Length > 0 && string.Equals(RewardId, rewardId, StringComparison.OrdinalIgnoreCase))
        || (RewardName.Length > 0 && string.Equals(RewardName, rewardTitle, StringComparison.OrdinalIgnoreCase));

    public void Normalize()
    {
        RewardId = RewardId?.Trim() ?? string.Empty;
        RewardName = RewardName?.Trim() ?? string.Empty;
        // Same reasoning as the pet rules: the flag decides whether viewers get their points back,
        // and a hand-edit claiming a reward with no id is not trusted with that.
        if (RewardId.Length == 0) Managed = false;
        Cost = Math.Clamp(Cost, 1, 10_000_000);
        Side = Side?.Trim().ToLowerInvariant() switch
        {
            "left" or "top" or "bottom" => Side!.Trim().ToLowerInvariant(),
            _ => "right"
        };
        SpinSeconds = Math.Clamp(SpinSeconds, 4, 20);
        Title = (Title ?? string.Empty).Trim();
        if (Title.Length == 0) Title = "Lyckosnurren";
        if (Title.Length > 60) Title = Title[..60];
        GiftTimeoutMinutes = Math.Clamp(GiftTimeoutMinutes, 1, 120);
        ListCommand = EdgeAlertSettings.CleanCommand(string.IsNullOrWhiteSpace(ListCommand) ? "!mina" : ListCommand);
        GiveCommand = EdgeAlertSettings.CleanCommand(string.IsNullOrWhiteSpace(GiveCommand) ? "!ge" : GiveCommand);
        PoolCommand = EdgeAlertSettings.CleanCommand(string.IsNullOrWhiteSpace(PoolCommand) ? "!alla" : PoolCommand);
        // Two commands answering the same word would leave "!x namn" meaning two things; the give
        // command yields, because the list command is the one viewers meet first.
        if (string.Equals(ListCommand, GiveCommand, StringComparison.OrdinalIgnoreCase))
            GiveCommand = string.Equals(ListCommand, "!ge", StringComparison.OrdinalIgnoreCase) ? "!skänk" : "!ge";
        // The pool command yields to both for the same reason, and to a second-choice word that is
        // free rather than one that would only collide again.
        if (string.Equals(PoolCommand, ListCommand, StringComparison.OrdinalIgnoreCase)
            || string.Equals(PoolCommand, GiveCommand, StringComparison.OrdinalIgnoreCase))
            PoolCommand = FreeWord(["!alla", "!vinster", "!lista"], ListCommand, GiveCommand);
        ReleaseCommand = EdgeAlertSettings.CleanCommand(string.IsNullOrWhiteSpace(ReleaseCommand) ? "!släpp" : ReleaseCommand);
        if (string.Equals(ReleaseCommand, ListCommand, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ReleaseCommand, GiveCommand, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ReleaseCommand, PoolCommand, StringComparison.OrdinalIgnoreCase))
            ReleaseCommand = FreeWord(["!släpp", "!visa", "!ut"], ListCommand, GiveCommand, PoolCommand);
    }

    /// <summary>The first of these words nothing else has already claimed. The last is the fallback.</summary>
    private static string FreeWord(IReadOnlyList<string> candidates, params string[] taken)
    {
        foreach (string word in candidates)
            if (!taken.Contains(word, StringComparer.OrdinalIgnoreCase)) return word;
        return candidates[^1];
    }

    /// <inheritdoc cref="AppSettings.Unknown"/>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
