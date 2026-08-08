using System.Net.Http;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Tests;

/// <summary>
/// What the app is willing to ask Twitch for in a given channel. These are the rules that keep a
/// missing permission from turning into a broken app: every "no" here has to end with fewer cards,
/// never with a chat that stops reading.
/// </summary>
public sealed class EventSubPlanTests
{
    private const string OwnUserId = "42";

    private static TwitchEventSubClient Client(string? loggedInUserId, params string[] scopes)
    {
        var store = new TokenStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        if (loggedInUserId is not null)
            store.Save(new StoredCredentials("refresh", "client", "streamern", loggedInUserId, scopes));

        var http = new HttpClient();
        var session = new TwitchSession(http, store);
        return new TwitchEventSubClient(session, new TwitchApiClient(http, session));
    }

    [Fact]
    public void AsksForNothingWhenNobodyIsLoggedIn()
    {
        EventSubPlan plan = Client(null).Plan(OwnUserId);

        Assert.False(plan.WorthConnecting);
        Assert.False(plan.Redemptions);
        Assert.False(plan.Shoutouts);
    }

    [Fact]
    public void ReadsRedemptionsInYourOwnChannel()
    {
        EventSubPlan plan = Client(OwnUserId, TwitchAuth.RedemptionsScope).Plan(OwnUserId);

        Assert.True(plan.Redemptions);
    }

    // The scope is granted per user, not per channel: Twitch will not let anyone read someone
    // else's redemptions, so asking would only earn a 403.
    [Fact]
    public void NeverAsksForRedemptionsInSomeoneElsesChannel()
    {
        EventSubPlan plan = Client(OwnUserId, TwitchAuth.RedemptionsScope).Plan("999");

        Assert.False(plan.Redemptions);
    }

    // The point of the whole scope check: a login granted before the scope existed must not send a
    // request Twitch answers with 403, and must not stop anything else from working either.
    [Fact]
    public void SkipsRedemptionsWhenTheStoredLoginPredatesTheScope()
    {
        EventSubPlan plan = Client(OwnUserId, "chat:read").Plan(OwnUserId);

        Assert.False(plan.Redemptions);
        Assert.Contains(TwitchAuth.RedemptionsScope, plan.MissingScopes);
    }

    // Twitch will not say up front whether we moderate a channel, so the only way to find out is to
    // ask and read the refusal. That means the plan says yes wherever the scope allows it.
    [Fact]
    public void TriesShoutoutsInAnyChannelOnceTheScopeIsThere()
    {
        EventSubPlan plan = Client(OwnUserId, TwitchAuth.ShoutoutsScope).Plan("999");

        Assert.True(plan.Shoutouts);
        Assert.True(plan.WorthConnecting);
    }

    [Fact]
    public void OpensNoSocketWhenThereIsNothingItCouldCarry()
    {
        EventSubPlan plan = Client(OwnUserId, "chat:read").Plan("999");

        Assert.False(plan.WorthConnecting);
    }

    [Fact]
    public void ReadsPowerUpsInYourOwnChannel()
    {
        EventSubPlan plan = Client(OwnUserId, TwitchAuth.BitsScope).Plan(OwnUserId);

        Assert.True(plan.PowerUps);
        Assert.True(plan.WorthConnecting);
    }

    // Bits are spent in a channel and only its broadcaster may read them, so unlike shoutouts there
    // is nothing to be learned from asking in someone else's chat.
    [Fact]
    public void NeverAsksForPowerUpsInSomeoneElsesChannel()
    {
        EventSubPlan plan = Client(OwnUserId, TwitchAuth.BitsScope).Plan("999");

        Assert.False(plan.PowerUps);
    }

    [Fact]
    public void SkipsPowerUpsWhenTheStoredLoginPredatesTheScope()
    {
        EventSubPlan plan = Client(OwnUserId, "chat:read").Plan(OwnUserId);

        Assert.False(plan.PowerUps);
        Assert.Contains(TwitchAuth.BitsScope, plan.MissingScopes);
    }

    // Someone else's channel cannot carry power-ups at all, so naming the scope there would send the
    // user off to log in again for something that still would not work.
    [Fact]
    public void DoesNotOfferTheBitsScopeInSomeoneElsesChannel()
    {
        EventSubPlan plan = Client(OwnUserId, "chat:read").Plan("999");

        Assert.DoesNotContain(TwitchAuth.BitsScope, plan.MissingScopes);
    }

    [Fact]
    public void ReadsHypeTrainsInYourOwnChannel()
    {
        EventSubPlan plan = Client(OwnUserId, TwitchAuth.HypeTrainScope).Plan(OwnUserId);

        Assert.True(plan.HypeTrain);
        Assert.True(plan.WorthConnecting);
    }

    // A hype train belongs to the channel it runs in, and only that broadcaster may read it – the
    // same shape as power-ups, so there is nothing to learn from asking anywhere else.
    [Fact]
    public void NeverAsksForHypeTrainsInSomeoneElsesChannel()
    {
        EventSubPlan plan = Client(OwnUserId, TwitchAuth.HypeTrainScope).Plan("999");

        Assert.False(plan.HypeTrain);
    }

    [Fact]
    public void SkipsHypeTrainsWhenTheStoredLoginPredatesTheScope()
    {
        EventSubPlan plan = Client(OwnUserId, "chat:read").Plan(OwnUserId);

        Assert.False(plan.HypeTrain);
        Assert.Contains(TwitchAuth.HypeTrainScope, plan.MissingScopes);
    }

    [Fact]
    public void DoesNotOfferTheHypeTrainScopeInSomeoneElsesChannel()
    {
        EventSubPlan plan = Client(OwnUserId, "chat:read").Plan("999");

        Assert.DoesNotContain(TwitchAuth.HypeTrainScope, plan.MissingScopes);
    }
}

public sealed class ScopeMigrationTests
{
    [Fact]
    public void NamesTheScopesAStoredLoginPredates()
    {
        IReadOnlyList<string> missing = TwitchAuth.MissingScopes(["chat:read", "chat:edit"]);

        Assert.Contains(TwitchAuth.RedemptionsScope, missing);
        Assert.Contains(TwitchAuth.ShoutoutsScope, missing);
        Assert.Contains(TwitchAuth.BitsScope, missing);
        Assert.Contains(TwitchAuth.HypeTrainScope, missing);
        Assert.DoesNotContain("chat:read", missing);
    }

    [Fact]
    public void FindsNothingMissingOnAFreshLogin()
    {
        Assert.Empty(TwitchAuth.MissingScopes(TwitchAuth.RequiredScopes));
    }

    [Fact]
    public void TreatsAMissingScopeListAsMissingEverything()
    {
        Assert.Equal(TwitchAuth.RequiredScopes.Length, TwitchAuth.MissingScopes(null).Count);
    }

    // The string goes in front of the user, so it has to say what the feature is rather than
    // repeat a scope name nobody outside Twitch's docs would recognise.
    [Fact]
    public void DescribesScopesAsFeatures()
    {
        Assert.Equal("inlösta belöningar", TwitchAuth.DescribeScope(TwitchAuth.RedemptionsScope));
        Assert.Equal("shoutouts", TwitchAuth.DescribeScope(TwitchAuth.ShoutoutsScope));
        Assert.Equal("power-ups och förstorade emotes", TwitchAuth.DescribeScope(TwitchAuth.BitsScope));
        Assert.Equal("hypetåg", TwitchAuth.DescribeScope(TwitchAuth.HypeTrainScope));
    }
}

/// <summary>
/// The guard that makes the reconnect overlap safe. Handling a reconnect the way Twitch requires
/// means both sockets are live for a moment and every event in that window arrives twice.
/// </summary>
public sealed class RecentMessageIdTests
{
    [Fact]
    public void AcceptsAnIdOnceAndRefusesItAfterwards()
    {
        var seen = new RecentMessageIds();

        Assert.True(seen.IsNew("abc"));
        Assert.False(seen.IsNew("abc"));
        Assert.False(seen.IsNew("abc"));
    }

    [Fact]
    public void KeepsDifferentIdsApart()
    {
        var seen = new RecentMessageIds();

        Assert.True(seen.IsNew("abc"));
        Assert.True(seen.IsNew("def"));
    }

    // A stream runs for hours; the buffer must not grow with it.
    [Fact]
    public void ForgetsTheOldestOnceItIsFull()
    {
        var seen = new RecentMessageIds(limit: 3);

        foreach (string id in new[] { "1", "2", "3" }) Assert.True(seen.IsNew(id));
        Assert.True(seen.IsNew("4"));

        // "1" fell out to make room, so it counts as new again. Harmless: a reconnect overlap is
        // seconds long, and nothing that old can still be in flight.
        Assert.True(seen.IsNew("1"));
        Assert.False(seen.IsNew("4"));
    }

    // Showing something twice is a blemish; dropping it is a lost sub. An untrackable frame goes through.
    [Fact]
    public void LetsThroughFramesThatCarryNoId()
    {
        var seen = new RecentMessageIds();

        Assert.True(seen.IsNew(null));
        Assert.True(seen.IsNew(""));
        Assert.True(seen.IsNew(""));
    }

    [Fact]
    public void StartsOverWhenCleared()
    {
        var seen = new RecentMessageIds();
        seen.IsNew("abc");

        seen.Clear();

        Assert.True(seen.IsNew("abc"));
    }

    // Two sockets are read by two tasks during the overlap, so the same id can be offered twice at once.
    [Fact]
    public async Task CountsAsNewExactlyOnceUnderConcurrentReaders()
    {
        var seen = new RecentMessageIds();
        int accepted = 0;

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            if (seen.IsNew("same-id")) Interlocked.Increment(ref accepted);
        })));

        Assert.Equal(1, accepted);
    }
}

public sealed class RewardCatalogTests
{
    private static ChatMessage Redeemed(string rewardId) =>
        new("m1", "Kajsa", "hej", null, [], false, false, DateTimeOffset.Now) { RewardId = rewardId };

    [Fact]
    public void PutsTheRewardNameAndPriceOnARedeemedMessage()
    {
        var catalog = new RewardCatalog();
        catalog.Remember(new CustomReward("abc", "TTS (INGEN REFUND)", 5000));

        ChatMessage enriched = catalog.Enrich(Redeemed("abc"));

        Assert.Equal("TTS (INGEN REFUND)", enriched.RewardTitle);
        Assert.Equal(5000, enriched.RewardCost);
    }

    // The case that matters in someone else's channel: the id is all IRC gave us and all we will
    // ever have. The message still goes through, it just cannot name the reward.
    [Fact]
    public void LeavesAMessageAloneWhenTheRewardIsUnknown()
    {
        ChatMessage message = new RewardCatalog().Enrich(Redeemed("abc"));

        Assert.Null(message.RewardTitle);
        Assert.Equal("abc", message.RewardId);
    }

    [Fact]
    public void LeavesOrdinaryMessagesUntouched()
    {
        var catalog = new RewardCatalog();
        catalog.Remember(new CustomReward("abc", "TTS", 5000));
        var plain = new ChatMessage("m2", "Pelle", "hej", null, [], false, false, DateTimeOffset.Now);

        Assert.Null(catalog.Enrich(plain).RewardTitle);
    }

    // Reward names belong to one channel; carrying them into the next one would label a redemption
    // with a name from a channel the reader has left.
    [Fact]
    public void ForgetsTheNamesWhenTheChannelChanges()
    {
        var catalog = new RewardCatalog();
        catalog.Remember(new CustomReward("abc", "TTS", 5000));

        catalog.Clear();

        Assert.Null(catalog.Enrich(Redeemed("abc")).RewardTitle);
    }
}
