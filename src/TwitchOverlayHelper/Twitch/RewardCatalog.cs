using System.Collections.Concurrent;
using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Twitch;

/// <summary>
/// Names for the reward ids IRC hands out. IRC only ever carries a redemption's GUID, so without
/// this a redeemed message can say nothing about which reward it was. Filled from Helix when we are
/// allowed to read the channel's rewards, and topped up from every redemption that comes past.
///
/// It is deliberately allowed to be empty: in someone else's channel we will never learn the names,
/// and a message then shows a neutral marker rather than nothing at all.
/// </summary>
public sealed class RewardCatalog
{
    private readonly ConcurrentDictionary<string, CustomReward> _rewards = new(StringComparer.OrdinalIgnoreCase);

    public void Remember(CustomReward reward)
    {
        if (reward.Id.Length > 0) _rewards[reward.Id] = reward;
    }

    public void RememberAll(IEnumerable<CustomReward> rewards)
    {
        foreach (CustomReward reward in rewards) Remember(reward);
    }

    /// <summary>Dropped when the app leaves a channel; another channel's reward names mean nothing here.</summary>
    public void Clear() => _rewards.Clear();

    public CustomReward? Find(string? rewardId) =>
        rewardId is { Length: > 0 } id && _rewards.TryGetValue(id, out CustomReward? reward) ? reward : null;

    /// <summary>
    /// Puts the reward's name and price on a message that redeemed one. Returns the message
    /// unchanged when it redeemed nothing, or when the reward is one we have no name for.
    /// </summary>
    public ChatMessage Enrich(ChatMessage message)
    {
        if (Find(message.RewardId) is not { } reward) return message;
        return message with
        {
            RewardTitle = reward.Title.Length > 0 ? reward.Title : null,
            RewardCost = reward.Cost > 0 ? reward.Cost : null
        };
    }
}
