using TwitchOverlayHelper.Pets;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Tests;

/// <summary>
/// Stands in for Helix. Records every verdict, so a test can say which redemptions were paid back
/// and which were booked as delivered without a network anywhere near it.
/// </summary>
internal sealed class FakeRedemptionGateway : IRedemptionGateway
{
    public List<(string RewardId, string RedemptionId, RedemptionStatus Status)> Answers { get; } = [];

    public List<QueuedRedemption> Queue { get; } = [];

    /// <summary>Thrown from every answer while it is set – for the "Twitch was unhappy" paths.</summary>
    public Func<Exception?>? FailNext { get; set; }

    /// <summary>The same for reading a queue, which fails on its own and has its own consequence.</summary>
    public Func<Exception?>? FailNextRead { get; set; }

    public Task AnswerAsync(string rewardId, string redemptionId, RedemptionStatus status, CancellationToken token)
    {
        if (FailNext?.Invoke() is { } failure) return Task.FromException(failure);
        Answers.Add((rewardId, redemptionId, status));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QueuedRedemption>> GetUnfulfilledAsync(string rewardId, CancellationToken token)
    {
        if (FailNextRead?.Invoke() is { } failure) return Task.FromException<IReadOnlyList<QueuedRedemption>>(failure);
        return Task.FromResult<IReadOnlyList<QueuedRedemption>>(Queue.Where(item => item.RewardId == rewardId).ToArray());
    }

    public IEnumerable<string> Refunded => Answers.Where(a => a.Status == RedemptionStatus.Canceled).Select(a => a.RedemptionId);

    public IEnumerable<string> Fulfilled => Answers.Where(a => a.Status == RedemptionStatus.Fulfilled).Select(a => a.RedemptionId);
}

public sealed class RedemptionLedgerTests
{
    // Short enough that the tests do not sit and wait, long enough that a scheduling hiccup on a
    // loaded machine cannot make an entry look overdue before its test says so.
    private static readonly RedemptionLedgerTimings Fast = new(
        AckGrace: TimeSpan.FromMilliseconds(60),
        OverlayGrace: TimeSpan.FromMilliseconds(60),
        Interval: TimeSpan.FromHours(1),
        ReceiptWindow: TimeSpan.FromSeconds(5));

    private static (RedemptionLedger Ledger, FakeRedemptionGateway Gateway, PetRegistry Registry, List<string> Despawned) Build()
    {
        var gateway = new FakeRedemptionGateway();
        var registry = new PetRegistry();
        List<string> despawned = [];
        // The interval is an hour: every test drives TickAsync itself, so nothing happens behind
        // the assertions.
        return (new RedemptionLedger(gateway, registry, despawned.Add, Fast), gateway, registry, despawned);
    }

    private static void Spawn(PetRegistry registry, string petId, TimeSpan lifetime) =>
        registry.Spawn(petId, "Kajsa", null, "robo", lifetime, maxPets: 6);

    [Fact]
    public async Task APetThatLivesItsFullTimeIsBookedAsDelivered()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, PetRegistry registry, _) = Build();
        Spawn(registry, "viewer-7", TimeSpan.FromMinutes(5));
        ledger.Track("r1", "reward-1", "viewer-7", "Kajsa", 500, DateTimeOffset.UtcNow.AddMilliseconds(20));
        ledger.MarkShown("viewer-7");

        await Task.Delay(40);
        await ledger.TickAsync();

        Assert.Equal(["r1"], gateway.Fulfilled);
        Assert.Empty(gateway.Refunded);
        Assert.Equal(0, ledger.PendingCount);
    }

    // The case the whole design exists for: the browser source is connected but never actually drew
    // anything, so the frame went out and nobody saw a pet.
    [Fact]
    public async Task ARedemptionNobodyReportedDrawingIsPaidBack()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, PetRegistry registry, List<string> despawned) = Build();
        Spawn(registry, "viewer-7", TimeSpan.FromMinutes(5));
        ledger.Track("r1", "reward-1", "viewer-7", "Kajsa", 500, DateTimeOffset.UtcNow.AddMinutes(5));

        await Task.Delay(80);
        await ledger.TickAsync();

        Assert.Equal(["r1"], gateway.Refunded);
        // The pet goes with the refund; nothing paid back is left walking about.
        Assert.Empty(registry.Snapshot());
        Assert.Equal(["viewer-7"], despawned);
    }

    // A reload in OBS drops the socket for a moment and puts the pet straight back. That must not
    // cost anybody their points, which is what the grace period is for.
    [Fact]
    public async Task AnOverlayThatComesStraightBackCostsNobodyTheirPoints()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, PetRegistry registry, _) = Build();
        Spawn(registry, "viewer-7", TimeSpan.FromMinutes(5));
        ledger.Track("r1", "reward-1", "viewer-7", "Kajsa", 500, DateTimeOffset.UtcNow.AddMinutes(5));
        ledger.MarkShown("viewer-7");

        ledger.OverlayCountChanged(0);
        ledger.OverlayCountChanged(1); // back before the grace period is out
        await Task.Delay(80);
        await ledger.TickAsync();

        Assert.Empty(gateway.Answers);
        Assert.Equal(1, ledger.PendingCount);
    }

    [Fact]
    public async Task AnOverlayThatStaysGonePaysBackEveryPetItWasShowing()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, PetRegistry registry, _) = Build();
        Spawn(registry, "viewer-7", TimeSpan.FromMinutes(5));
        Spawn(registry, "viewer-8", TimeSpan.FromMinutes(5));
        ledger.Track("r1", "reward-1", "viewer-7", "Kajsa", 500, DateTimeOffset.UtcNow.AddMinutes(5));
        ledger.Track("r2", "reward-1", "viewer-8", "Pelle", 500, DateTimeOffset.UtcNow.AddMinutes(5));
        ledger.MarkShown("viewer-7");
        ledger.MarkShown("viewer-8");

        ledger.OverlayCountChanged(0);
        await Task.Delay(80);
        await ledger.TickAsync();

        Assert.Equal(["r1", "r2"], gateway.Refunded.Order());
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public void ARefundFromTwitchsOwnQueueTakesThePetDownToo()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, PetRegistry registry, List<string> despawned) = Build();
        Spawn(registry, "viewer-7", TimeSpan.FromMinutes(5));
        ledger.Track("r1", "reward-1", "viewer-7", "Kajsa", 500, DateTimeOffset.UtcNow.AddMinutes(5));

        ledger.HandleExternalUpdate("r1", "CANCELED");

        // Twitch already did the refunding; asking it again would be answering a redemption that is
        // no longer in the queue.
        Assert.Empty(gateway.Answers);
        Assert.Empty(registry.Snapshot());
        Assert.Equal(["viewer-7"], despawned);
        Assert.Equal(0, ledger.PendingCount);
    }

    // Our own verdicts come back through the same event. Acting on them again would take a pet down
    // that was fulfilled precisely because it lived its whole life.
    [Fact]
    public void OurOwnAnswerComingBackChangesNothing()
    {
        (RedemptionLedger ledger, _, PetRegistry registry, List<string> despawned) = Build();
        Spawn(registry, "viewer-7", TimeSpan.FromMinutes(5));
        ledger.Track("r1", "reward-1", "viewer-7", "Kajsa", 500, DateTimeOffset.UtcNow.AddMinutes(5));

        ledger.HandleExternalUpdate("r1", "FULFILLED");

        Assert.Single(registry.Snapshot());
        Assert.Empty(despawned);
    }

    [Fact]
    public async Task WhatAPreviousRunLeftUnfulfilledIsPaidBack()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, _, _) = Build();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        gateway.Queue.Add(new QueuedRedemption("old", "reward-1", "Kajsa", 500, started.AddMinutes(-3)));
        // Arrived while the app was starting: EventSub is about to give this one a pet, so the
        // sweep has to leave it alone.
        gateway.Queue.Add(new QueuedRedemption("new", "reward-1", "Pelle", 500, started.AddSeconds(2)));

        await ledger.SweepAsync(["reward-1"], started);

        Assert.Equal(["old"], gateway.Refunded);
    }

    /// <summary>
    /// The sweep must not undo the very thing it backs up. A reconnect mid-stream runs it again with
    /// a fresh cutoff, and a reading still waiting for the streamer's yes was redeemed before that
    /// moment – so it would be paid back while it was sitting on screen being decided.
    /// </summary>
    [Fact]
    public async Task TheSweepLeavesRedemptionsAnotherQueueIsHolding()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, _, _) = Build();
        DateTimeOffset reconnected = DateTimeOffset.UtcNow;
        gateway.Queue.Add(new QueuedRedemption("waiting", "reward-1", "Kajsa", 500, reconnected.AddMinutes(-2)));
        gateway.Queue.Add(new QueuedRedemption("orphan", "reward-1", "Pelle", 500, reconnected.AddMinutes(-2)));
        ledger.ClaimedElsewhere = id => id == "waiting";

        await ledger.SweepAsync(["reward-1"], reconnected);

        Assert.Equal(["orphan"], gateway.Refunded);
    }

    /// <summary>
    /// A verdict that is already decided still has to reach Twitch, and a dropped connection or a
    /// token being refreshed must not turn a refusal into a viewer who paid and got nothing back.
    /// </summary>
    [Fact]
    public async Task AnImmediateVerdictIsTriedAgainWhenTwitchIsUnhappy()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, _, _) = Build();
        gateway.FailNext = () => new TwitchApiException("Twitch hade en dålig minut.");

        await ledger.AnswerNow("r1", "reward-1", "Kajsa", 500, RedemptionStatus.Canceled, "nekad", "tts");

        // Held rather than dropped: the entry went back with its verdict attached.
        Assert.Empty(gateway.Refunded);
        Assert.Equal(1, ledger.PendingCount);

        gateway.FailNext = null;
        await ledger.TickAsync();

        Assert.Equal(["r1"], gateway.Refunded);
        Assert.Equal(0, ledger.PendingCount);
    }

    /// <summary>
    /// A reading answered through the ledger is reported as a reading, so the app can put the
    /// sentence next to the feature it belongs to rather than under the pets.
    /// </summary>
    [Fact]
    public async Task AVerdictSaysWhatTheRedemptionPaidFor()
    {
        (RedemptionLedger ledger, _, _, _) = Build();
        RedemptionNotice? notice = null;
        ledger.Answered += given => notice = given;

        await ledger.AnswerNow("r1", "reward-1", "Kajsa", 500, RedemptionStatus.Canceled, "nekad", "tts");

        Assert.Equal("tts", notice?.Subject);
    }

    [Fact]
    public async Task ARefundTwitchRefusedRightNowIsTriedAgainRatherThanLost()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, PetRegistry registry, _) = Build();
        Spawn(registry, "viewer-7", TimeSpan.FromMinutes(5));
        ledger.Track("r1", "reward-1", "viewer-7", "Kajsa", 500, DateTimeOffset.UtcNow.AddMinutes(5));
        gateway.FailNext = () => new TwitchApiException("Twitch svarade med ett fel.");

        await Task.Delay(80);
        await ledger.TickAsync();
        Assert.Empty(gateway.Answers);
        Assert.Equal(1, ledger.PendingCount);

        gateway.FailNext = null;
        await ledger.TickAsync();

        Assert.Equal(["r1"], gateway.Refunded);
    }

    // A reward the app did not create answers 403 forever, so retrying is pointless – and holding on
    // to the entry would mean trying again every two seconds for the rest of the stream.
    [Fact]
    public async Task ARewardTwitchSaysIsNotOursIsLetGoInsteadOfRetried()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, PetRegistry registry, _) = Build();
        Spawn(registry, "viewer-7", TimeSpan.FromMinutes(5));
        ledger.Track("r1", "reward-1", "viewer-7", "Kajsa", 500, DateTimeOffset.UtcNow.AddMinutes(5));
        gateway.FailNext = () => new TwitchNotPermittedException("Du saknar behörighet.");

        await Task.Delay(80);
        await ledger.TickAsync();
        gateway.FailNext = null;
        await ledger.TickAsync();

        Assert.Empty(gateway.Answers);
        Assert.Equal(0, ledger.PendingCount);
    }

    // A pet pushed off a full lawn stopped being delivered the moment it left the screen. Before
    // this it sat out the rest of its time and was booked as a full life.
    [Fact]
    public async Task APetEvictedToMakeRoomIsPaidBackRatherThanBookedAsDelivered()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, PetRegistry registry, _) = Build();
        Spawn(registry, "viewer-7", TimeSpan.FromMinutes(5));
        ledger.Track("r1", "reward-1", "viewer-7", "Kajsa", 500, DateTimeOffset.UtcNow.AddMinutes(5));
        ledger.MarkShown("viewer-7");

        // What PetService reports when a spawn evicted this one; the registry has already dropped it.
        registry.Remove("viewer-7");
        ledger.PetEvicted("viewer-7");
        await Task.Delay(20);

        Assert.Equal(["r1"], gateway.Refunded);
        Assert.Equal(0, ledger.PendingCount);
    }

    [Fact]
    public async Task EvictingSomebodyElsesPetLeavesThisOneAlone()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, PetRegistry registry, _) = Build();
        Spawn(registry, "viewer-7", TimeSpan.FromMinutes(5));
        ledger.Track("r1", "reward-1", "viewer-7", "Kajsa", 500, DateTimeOffset.UtcNow.AddMinutes(5));
        ledger.MarkShown("viewer-7");

        ledger.PetEvicted("nagon-annan");
        await Task.Delay(20);

        Assert.Empty(gateway.Answers);
        Assert.Equal(1, ledger.PendingCount);
    }

    // A token being refreshed under the ledger throws from a layer below the Twitch API types. It
    // used to travel past the retry list and take the entry with it, which is a viewer quietly out
    // of pocket for a pet nobody saw.
    [Fact]
    public async Task AnAuthErrorMidRoundPutsTheEntryBackInsteadOfLosingIt()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, PetRegistry registry, _) = Build();
        Spawn(registry, "viewer-7", TimeSpan.FromMinutes(5));
        ledger.Track("r1", "reward-1", "viewer-7", "Kajsa", 500, DateTimeOffset.UtcNow.AddMinutes(5));
        gateway.FailNext = () => new TwitchAuthException("Twitch kunde inte förnya inloggningen.");

        await Task.Delay(80);
        await ledger.TickAsync();
        Assert.Empty(gateway.Answers);
        Assert.Equal(1, ledger.PendingCount);

        gateway.FailNext = null;
        await ledger.TickAsync();

        Assert.Equal(["r1"], gateway.Refunded);
    }

    // The verdict has to survive the wait. An entry put back and then judged afresh would have a
    // failed refund come round again as a fulfilment, which is the one mistake here that silently
    // keeps the viewer's points.
    [Fact]
    public async Task ARefundThatFailedDoesNotComeBackAsAFulfilment()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, _, _) = Build();
        gateway.FailNext = () => new TwitchApiException("Twitch svarade med ett fel.");

        // An immediate refund: its entry carries no future expiry, so a second judging would call
        // it expired and book it as delivered.
        await ledger.RefundNow("r1", "reward-1", "Kajsa", 500, "pet-overlayen var inte igång");
        gateway.FailNext = null;
        await ledger.TickAsync();

        Assert.Equal(["r1"], gateway.Refunded);
        Assert.Empty(gateway.Fulfilled);
    }

    // EventSub can deliver a redemption made a moment before the subscription was confirmed, so the
    // cutoff alone cannot tell it from one nobody was listening for.
    [Fact]
    public async Task TheSweepLeavesAlonePetsItIsAlreadyWatching()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, PetRegistry registry, _) = Build();
        DateTimeOffset listeningSince = DateTimeOffset.UtcNow;
        Spawn(registry, "viewer-7", TimeSpan.FromMinutes(5));
        ledger.Track("live", "reward-1", "viewer-7", "Kajsa", 500, DateTimeOffset.UtcNow.AddMinutes(5));
        gateway.Queue.Add(new QueuedRedemption("live", "reward-1", "Kajsa", 500, listeningSince.AddSeconds(-1)));
        gateway.Queue.Add(new QueuedRedemption("gammal", "reward-1", "Pelle", 500, listeningSince.AddMinutes(-9)));

        await ledger.SweepAsync(["reward-1"], listeningSince);

        Assert.Equal(["gammal"], gateway.Refunded);
        Assert.Single(registry.Snapshot());
    }

    // The spawn frame leaves before the entry is written, so on a machine running both the app and
    // the lawn the receipt can beat the bookkeeping. It used to land on nothing, and the pet that
    // everybody watched was paid back when the grace period ran out.
    [Fact]
    public async Task AReceiptThatArrivesBeforeTheEntryStillCounts()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, PetRegistry registry, _) = Build();
        Spawn(registry, "viewer-7", TimeSpan.FromMinutes(5));

        ledger.MarkShown("viewer-7");
        ledger.Track("r1", "reward-1", "viewer-7", "Kajsa", 500, DateTimeOffset.UtcNow.AddMinutes(5));

        await Task.Delay(80);
        await ledger.TickAsync();

        Assert.Empty(gateway.Answers);
        Assert.Equal(1, ledger.PendingCount);
    }

    // Pet ids are viewer ids and come round again. A receipt from the same viewer's last redemption
    // must not vouch for this one, or a lawn that has since gone dark would keep being paid for.
    [Fact]
    public async Task AStaleReceiptDoesNotVouchForALaterRedemption()
    {
        var gateway = new FakeRedemptionGateway();
        var registry = new PetRegistry();
        // A receipt window short enough that the one taken below is plainly out of date by the time
        // the entry is booked.
        var ledger = new RedemptionLedger(gateway, registry, _ => { },
            Fast with { ReceiptWindow = TimeSpan.FromMilliseconds(20) });
        Spawn(registry, "viewer-7", TimeSpan.FromMinutes(5));

        ledger.MarkShown("viewer-7");
        await Task.Delay(50);
        ledger.Track("r1", "reward-1", "viewer-7", "Kajsa", 500, DateTimeOffset.UtcNow.AddMinutes(5));

        await Task.Delay(80);
        await ledger.TickAsync();

        Assert.Equal(["r1"], gateway.Refunded);
    }

    // Turning pets off hides the whole lawn while the creatures on it go on living out their time.
    // Nobody can see what they paid for, so nobody keeps paying for it.
    [Fact]
    public async Task SwitchingPetsOffPaysBackEverythingStillWaiting()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, PetRegistry registry, List<string> despawned) = Build();
        Spawn(registry, "viewer-7", TimeSpan.FromMinutes(5));
        Spawn(registry, "viewer-8", TimeSpan.FromMinutes(5));
        ledger.Track("r1", "reward-1", "viewer-7", "Kajsa", 500, DateTimeOffset.UtcNow.AddMinutes(5));
        ledger.Track("r2", "reward-1", "viewer-8", "Pelle", 500, DateTimeOffset.UtcNow.AddMinutes(5));
        ledger.MarkShown("viewer-7");
        ledger.MarkShown("viewer-8");

        ledger.RefundAll("pets stängdes av");
        await Task.Delay(20);

        Assert.Equal(["r1", "r2"], gateway.Refunded.Order());
        Assert.Equal(0, ledger.PendingCount);
        Assert.Equal(["viewer-7", "viewer-8"], despawned.Order());
    }

    // A sweep that could not read a queue must say so, or its caller marks the job done and those
    // redemptions sit in Twitch's queue for the rest of the stream.
    [Fact]
    public async Task ASweepThatCouldNotReadAQueueReportsItselfUnfinished()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, _, _) = Build();
        gateway.FailNextRead = () => new TwitchApiException("Twitch svarade med ett fel.");

        Assert.False(await ledger.SweepAsync(["reward-1"], DateTimeOffset.UtcNow));

        gateway.FailNextRead = null;
        Assert.True(await ledger.SweepAsync(["reward-1"], DateTimeOffset.UtcNow));
    }

    // The same goes for a token being refreshed underneath it: that throws from below the Twitch
    // API types, and a sweep counting it as done is the same silent loss.
    [Fact]
    public async Task ASweepSurvivesAnAuthErrorAndReportsItselfUnfinished()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, _, _) = Build();
        gateway.FailNextRead = () => new TwitchAuthException("Twitch kunde inte förnya inloggningen.");

        Assert.False(await ledger.SweepAsync(["reward-1"], DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AnImmediateRefundNeedsNoPetAndTracksNothing()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, _, List<string> despawned) = Build();

        ledger.RefundNow("r1", "reward-1", "Kajsa", 500, "pet-overlayen var inte igång");

        Assert.Equal(["r1"], gateway.Refunded);
        Assert.Empty(despawned);
        Assert.Equal(0, ledger.PendingCount);
    }

    // Closing the app must not fire off a round of refunds on the way out: the redemptions stay in
    // Twitch's queue, where the streamer can work them and the next start sweeps the rest.
    [Fact]
    public async Task ResetLetsEverythingGoWithoutAnsweringTwitch()
    {
        (RedemptionLedger ledger, FakeRedemptionGateway gateway, PetRegistry registry, _) = Build();
        Spawn(registry, "viewer-7", TimeSpan.FromMinutes(5));
        ledger.Track("r1", "reward-1", "viewer-7", "Kajsa", 500, DateTimeOffset.UtcNow.AddMinutes(5));

        ledger.Reset();
        await Task.Delay(80);
        await ledger.TickAsync();

        Assert.Empty(gateway.Answers);
        Assert.Equal(0, ledger.PendingCount);
    }
}
