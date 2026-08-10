using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Pets;

/// <summary>
/// The ledger's two calls, wired to the real Helix client. The broadcaster is read through a
/// callback rather than handed over once: the app can be pointed at another channel while it runs,
/// and answering a redemption against the channel we have left would be rejected at best.
/// </summary>
public sealed class TwitchRedemptionGateway(TwitchApiClient api, Func<string> broadcasterId) : IRedemptionGateway
{
    public Task AnswerAsync(string rewardId, string redemptionId, RedemptionStatus status, CancellationToken token)
    {
        string broadcaster = broadcasterId();
        if (broadcaster.Length == 0) throw new TwitchApiException("Kanalen är inte känd ännu.");
        return api.UpdateRedemptionStatusAsync(broadcaster, rewardId, redemptionId, status, token);
    }

    public Task<IReadOnlyList<QueuedRedemption>> GetUnfulfilledAsync(string rewardId, CancellationToken token)
    {
        string broadcaster = broadcasterId();
        if (broadcaster.Length == 0) throw new TwitchApiException("Kanalen är inte känd ännu.");
        return api.GetUnfulfilledRedemptionsAsync(broadcaster, rewardId, token);
    }
}
