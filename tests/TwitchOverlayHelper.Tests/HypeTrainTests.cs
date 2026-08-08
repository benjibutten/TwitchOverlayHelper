using System.Globalization;
using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Tests;

/// <summary>
/// A hype train is the one thing in the chat views that is a state rather than a line, and the two
/// rules that follow from that are here: an update never walks the train backwards, and a strip
/// never outlives the train it describes.
/// </summary>
public sealed class HypeTrainStateTests
{
    private static HypeTrainState Train(
        HypeTrainPhase phase, int level = 1, int total = 100, string id = "t1", DateTimeOffset? at = null) =>
        new(id, phase, level, total, 500, total, at ?? DateTimeOffset.Now);

    [Fact]
    public void ShowsTheFirstTrainItIsGiven()
    {
        Assert.True(Train(HypeTrainPhase.Begin).Supersedes(null));
    }

    [Fact]
    public void FollowsATrainUpThroughItsLevels()
    {
        HypeTrainState first = Train(HypeTrainPhase.Progress, level: 1, total: 100);

        Assert.True(Train(HypeTrainPhase.Progress, level: 1, total: 260).Supersedes(first));
        Assert.True(Train(HypeTrainPhase.Progress, level: 2, total: 600).Supersedes(first));
    }

    // Twitch says outright that a progress can arrive before the begin that started it. Letting the
    // late one through would put the bar back where it was a minute ago.
    [Fact]
    public void IgnoresAnUpdateThatWouldWalkTheTrainBackwards()
    {
        HypeTrainState current = Train(HypeTrainPhase.Progress, level: 3, total: 1400);

        Assert.False(Train(HypeTrainPhase.Begin, level: 1, total: 100).Supersedes(current));
        Assert.False(Train(HypeTrainPhase.Progress, level: 2, total: 900).Supersedes(current));
    }

    [Fact]
    public void LetsTheEndThroughHoweverFarBehindItLooks()
    {
        HypeTrainState current = Train(HypeTrainPhase.Progress, level: 3, total: 1400);

        // The end payload has no goal and no progress, so by the numbers alone it reads as a step back.
        Assert.True(new HypeTrainState("t1", HypeTrainPhase.Ended, 3, 0, 0, 1400, DateTimeOffset.Now).Supersedes(current));
    }

    // A train ends once. A progress notification still in flight must not claim it is running again.
    [Fact]
    public void StaysEndedOnceItHasEnded()
    {
        HypeTrainState ended = Train(HypeTrainPhase.Ended, level: 3, total: 1400);

        Assert.False(Train(HypeTrainPhase.Progress, level: 3, total: 1500).Supersedes(ended));
    }

    [Fact]
    public void ReplacesTheOldTrainWhenANewOneStarts()
    {
        HypeTrainState previous = Train(HypeTrainPhase.Ended, level: 5, total: 3000, id: "t1");

        Assert.True(Train(HypeTrainPhase.Begin, level: 1, total: 100, id: "t2").Supersedes(previous));
    }

    [Fact]
    public void KeepsAFinishedTrainOnScreenJustLongEnoughToRead()
    {
        var at = DateTimeOffset.Now;
        HypeTrainState ended = Train(HypeTrainPhase.Ended, at: at);

        Assert.True(ended.IsWorthShowing(at.AddSeconds(3)));
        Assert.False(ended.IsWorthShowing(at + HypeTrainState.EndedLinger + TimeSpan.FromSeconds(1)));
    }

    // What keeps a dropped connection from leaving a frozen bar up for the rest of the stream:
    // Twitch would have sent a level-up or an end before this deadline if we were still hearing it.
    [Fact]
    public void RetiresARunningTrainOnceItsOwnDeadlineHasPassed()
    {
        var now = DateTimeOffset.Now;
        HypeTrainState running = Train(HypeTrainPhase.Progress) with { ExpiresAt = now.AddMinutes(2) };

        Assert.True(running.IsWorthShowing(now));
        Assert.False(running.IsWorthShowing(now.AddMinutes(3)));
    }

    [Fact]
    public void ShowsATrainWithNoDeadlineAtAll()
    {
        Assert.True(Train(HypeTrainPhase.Progress).IsWorthShowing(DateTimeOffset.Now));
    }

    // The overlay gets two cards from a whole train. A card per contribution would bury the chat.
    [Fact]
    public void MakesACardOutOfTheStartAndTheEndAndNothingBetween()
    {
        Assert.Equal(ChatEventType.HypeTrainBegin, Train(HypeTrainPhase.Begin).ToChatEvent()?.Type);
        Assert.Equal(ChatEventType.HypeTrainEnd, Train(HypeTrainPhase.Ended).ToChatEvent()?.Type);
        Assert.Null(Train(HypeTrainPhase.Progress).ToChatEvent());
    }

    // Both cards come from one train, so their ids have to differ or the second would look like a
    // repeat of the first.
    [Fact]
    public void GivesTheStartAndEndCardsIdsOfTheirOwn()
    {
        Assert.NotEqual(Train(HypeTrainPhase.Begin).ToChatEvent()!.Id, Train(HypeTrainPhase.Ended).ToChatEvent()!.Id);
    }

    // The overlay deduplicates its cards on this id, which only works if the id names the moment and
    // nothing else: the numbers on a train change between notifications, and the moment does not.
    [Fact]
    public void NamesTheMomentAndNotTheNumbersInACardId()
    {
        Assert.Equal(
            Train(HypeTrainPhase.Ended, level: 3, total: 1400).ToChatEvent()!.Id,
            Train(HypeTrainPhase.Ended, level: 4, total: 2600).ToChatEvent()!.Id);
    }
}

/// <summary>The wording both views share, worded once so neither can drift away from the other.</summary>
public sealed class HypeTrainTextTests
{
    /// <summary>
    /// Swedish groups thousands with a non-breaking space, so a total can never be broken across
    /// two lines. Taken from the culture rather than typed into the expected strings, because the
    /// two kinds of space look identical in a source file and one of them would go unnoticed.
    /// </summary>
    private static readonly string Nbsp = CultureInfo.GetCultureInfo("sv-SE").NumberFormat.NumberGroupSeparator;

    private static HypeTrainState Running(int level, int progress, int goal) =>
        new("t1", HypeTrainPhase.Progress, level, progress, goal, 1400, DateTimeOffset.Now);

    [Fact]
    public void NamesTheLevelOnTheStrip()
    {
        Assert.Equal("Hypetåg – nivå 3", ChatEventText.DescribeHypeTrain(Running(3, 200, 800)));
    }

    [Fact]
    public void SaysHowFarIntoTheLevelTheTrainHasCome()
    {
        Assert.Equal($"1{Nbsp}200 / 2{Nbsp}500 poäng", ChatEventText.DescribeHypeProgress(Running(3, 1200, 2500)));
    }

    // The whole reason the plan insists on version 2: v1 cannot tell these apart at all.
    [Fact]
    public void TellsTheSpecialTrainsApart()
    {
        Assert.StartsWith("Gyllene Kappa-tåg", ChatEventText.DescribeHypeTrain(Running(1, 0, 500) with { Kind = "golden_kappa" }));
        Assert.StartsWith("Skattkammartåg", ChatEventText.DescribeHypeTrain(Running(1, 0, 500) with { Kind = "treasure" }));
        Assert.StartsWith("Hypetåg (delat)", ChatEventText.DescribeHypeTrain(Running(1, 0, 500) with { IsShared = true }));
    }

    [Fact]
    public void SaysWhereAFinishedTrainGotTo()
    {
        var ended = new HypeTrainState("t1", HypeTrainPhase.Ended, 4, 0, 0, 4250, DateTimeOffset.Now);

        Assert.Equal($"Hypetåg slutade på nivå 4 med 4{Nbsp}250 poäng", ChatEventText.DescribeHypeTrain(ended));
    }

    // The overlay card is a single line, so the biggest contributor rides along in it.
    [Fact]
    public void NamesTheBiggestContributorOnTheEndCard()
    {
        var ended = new HypeTrainState("t1", HypeTrainPhase.Ended, 4, 0, 0, 4250, DateTimeOffset.Now)
        {
            TopContributions = [new HypeTrainContribution("Kajsa", "bits", 1200)]
        };

        // "Toppbidrag" rather than "störst": Twitch ranks these per contribution method, so calling
        // the first one the biggest would claim a ranking the payload does not carry.
        Assert.Equal($"Hypetåg slutade på nivå 4 med 4{Nbsp}250 poäng – toppbidrag: Kajsa (1{Nbsp}200 bits)",
            ChatEventText.Describe(ended.ToChatEvent()!));
    }

    [Fact]
    public void SaysTheStartIsUnderway()
    {
        var begun = new HypeTrainState("t1", HypeTrainPhase.Begin, 1, 137, 500, 137, DateTimeOffset.Now);

        Assert.Equal("Hypetåg igång – nivå 1", ChatEventText.Describe(begun.ToChatEvent()!));
    }

    // Twitch writes a subscription as its tier price – 500, 1000 or 2500 – which is an encoding and
    // not a number anyone should be shown.
    [Fact]
    public void SpellsOutASubscriptionTierInsteadOfItsPrice()
    {
        Assert.Equal("Pelle (prenumeration (nivå 1))", ChatEventText.DescribeContribution(new HypeTrainContribution("Pelle", "subscription", 500)));
        Assert.Equal("Pelle (prenumeration (nivå 2))", ChatEventText.DescribeContribution(new HypeTrainContribution("Pelle", "subscription", 1000)));
        Assert.Equal("Pelle (prenumeration (nivå 3))", ChatEventText.DescribeContribution(new HypeTrainContribution("Pelle", "subscription", 2500)));
    }

    [Fact]
    public void CountsBitsAsThemselves()
    {
        Assert.Equal($"Kajsa (1{Nbsp}200 bits)", ChatEventText.DescribeContribution(new HypeTrainContribution("Kajsa", "bits", 1200)));
    }

    // "other" covers whatever Twitch adds next; it must read as something rather than as nothing.
    [Fact]
    public void HasSomethingToSayAboutAContributionItDoesNotKnow()
    {
        Assert.Equal("Kajsa (bidrag)", ChatEventText.DescribeContribution(new HypeTrainContribution("Kajsa", "other", 40)));
    }
}
