using System.Net.Http;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Speech;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Tests;

public sealed class TtsTextTests
{
    [Theory]
    [InlineData("  hej   på   dig  ", "hej på dig")]
    [InlineData("rad ett\nrad två", "rad ett rad två")]
    [InlineData("hejjjjjjjjjj", "hejjj")]
    [InlineData("wow!!!!!!!!!!", "wow!!!")]
    [InlineData("", "")]
    [InlineData("   \n\t  ", "")]
    public void TidiesWhatAViewerTyped(string written, string expected) =>
        Assert.Equal(expected, TtsText.Clean(written, 240));

    // ElevenLabs bills by the character, so the limit is the one setting that decides what a single
    // redemption can cost – and a message cut mid-word sounds like the app broke.
    [Fact]
    public void CutsALongMessageAtAWordBoundary()
    {
        string spoken = TtsText.Clean("hej på dig din underbara lilla vän i chatten", 20);

        Assert.Equal("hej på dig din", spoken);
        Assert.True(spoken.Length <= 20);
    }

    /// <summary>A first word longer than the whole limit has no boundary to fall back on.</summary>
    [Fact]
    public void CutsAtTheLimitWhenThereIsNoWordBoundary()
    {
        Assert.Equal("abcdefghij", TtsText.Clean("abcdefghijklmnopqrstuvwxyz", 10));
    }
}

public sealed class TtsSettingsTests
{
    [Fact]
    public void IsPartOfTheSavedSettings()
    {
        var settings = new AppSettings { Tts = null! };

        settings.Normalize();

        Assert.NotNull(settings.Tts);
        Assert.False(settings.Tts.Enabled);
        // The browser source is the only output that reaches the viewers without the streamer
        // capturing their whole desktop.
        Assert.Equal(TtsOutput.Browser, settings.Tts.Output);
    }

    /// <summary>
    /// A hand-edited settings.json could claim a reward is the app's own without an id to answer on.
    /// The flag decides whether viewers get their points back, so it is dropped rather than trusted.
    /// </summary>
    [Fact]
    public void ForgetsTheRefundClaimWithoutARewardId()
    {
        var tts = new TtsSettings { Trigger = TtsTrigger.Reward, RewardManaged = true, RewardId = "  " };

        tts.Normalize();

        Assert.False(tts.RewardManaged);
        Assert.False(tts.CanRefund);
    }

    /// <summary>
    /// Unlike the pets there is no catch-all: a rule that named nothing would turn every redemption
    /// in the channel into something read out loud.
    /// </summary>
    [Fact]
    public void AnEmptyRewardIdClaimsNothing()
    {
        var tts = new TtsSettings { Trigger = TtsTrigger.Reward };

        Assert.False(tts.MatchesReward("abc"));
        Assert.False(tts.MatchesReward(null));
    }

    /// <summary>
    /// A Power-up, on the other hand, may be left unnamed: a channel that sells one custom Power-up
    /// should not have to paste its id, and every one of them is the broadcaster's own.
    /// </summary>
    [Fact]
    public void AnEmptyPowerUpIdClaimsThemAll()
    {
        var tts = new TtsSettings { Trigger = TtsTrigger.PowerUp };

        Assert.True(tts.MatchesPowerUp("anything"));
        // Never both at once: the trigger is a choice between the two.
        Assert.False(tts.MatchesReward("anything"));
    }

    [Fact]
    public void OnlyAnAppMadeRewardCanBeRefunded()
    {
        var tts = new TtsSettings { Trigger = TtsTrigger.Reward, RewardId = "reward-1" };
        tts.Normalize();
        Assert.False(tts.CanRefund);

        tts.RewardManaged = true;
        Assert.True(tts.CanRefund);

        // Bits are spent the moment Twitch sends the notification; there is no endpoint to undo it.
        tts.Trigger = TtsTrigger.PowerUp;
        Assert.False(tts.CanRefund);
    }
}

/// <summary>An ElevenLabs that never answers, so a stop can land while the synthesis is in flight.</summary>
internal sealed class HangingHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }
}

public sealed class TtsServiceTests
{
    private static TtsRequest Request(string id = "r1", bool refundable = true, string text = "hej allihopa") =>
        new(id, TtsSource.Reward, "reward-1", refundable, "user-1", "Kajsa", text, 500, DateTimeOffset.UtcNow);

    private static AppSettings Configured(bool requireApproval = true)
    {
        var settings = new AppSettings();
        settings.Normalize();
        settings.Tts.Enabled = true;
        settings.Tts.VoiceId = "voice-1";
        settings.Tts.RequireApproval = requireApproval;
        return settings;
    }

    private static (TtsService Service, List<string> Played) Service(AppSettings settings)
    {
        var secrets = new SpeechSecretStore(SpeechFixture.TempPath(".bin"));
        secrets.Save(new SpeechSecrets("deepseek", "eleven"));
        var played = new List<string>();
        TtsService service = SpeechFixture.Tts(settings, secrets: secrets, play: (clip, _) =>
        {
            lock (played) played.Add(clip.FilePath);
            return Task.CompletedTask;
        });
        return (service, played);
    }

    /// <summary>Waits for the reading queue to drain; it runs on its own task, off the caller's.</summary>
    private static async Task SettleAsync(TtsService service)
    {
        for (int attempt = 0; attempt < 200 && service.Snapshot().Count > 0; attempt++)
            await Task.Delay(25);
    }

    [Fact]
    public async Task ReadsOutARequestNobodyHasToApprove()
    {
        AppSettings settings = Configured(requireApproval: false);
        (TtsService service, List<string> played) = Service(settings);
        var answers = new List<RedemptionStatus>();
        service.Answered += (_, status, _) => answers.Add(status);

        TtsOutcome outcome = service.Handle(Request());
        await SettleAsync(service);

        Assert.True(outcome.Accepted);
        Assert.Single(played);
        // The points are earned once the words have actually come out of the speakers – never before.
        Assert.Equal([RedemptionStatus.Fulfilled], answers);
    }

    [Fact]
    public async Task HoldsARequestUntilTheStreamerSaysYes()
    {
        AppSettings settings = Configured();
        (TtsService service, List<string> played) = Service(settings);

        service.Handle(Request());

        TtsEntry waiting = Assert.Single(service.Snapshot());
        Assert.Equal("pending", waiting.State);
        Assert.Equal("Kajsa", waiting.Viewer);
        Assert.NotNull(waiting.DeadlineAt);
        Assert.Empty(played);

        Assert.True(service.Approve("r1"));
        await SettleAsync(service);
        Assert.Single(played);
    }

    /// <summary>The refusal is the refund: Twitch puts the points back itself once we cancel.</summary>
    [Fact]
    public void RefusingHandsThePointsBack()
    {
        AppSettings settings = Configured();
        (TtsService service, List<string> played) = Service(settings);
        var answers = new List<RedemptionStatus>();
        service.Answered += (_, status, _) => answers.Add(status);

        service.Handle(Request());
        Assert.True(service.Reject("r1"));

        Assert.Empty(played);
        Assert.Empty(service.Snapshot());
        Assert.Equal([RedemptionStatus.Canceled], answers);
        // Answering the same one twice is what would hand the points back for a reading that was
        // already refused – and the second call has nothing left to act on.
        Assert.False(service.Reject("r1"));
        Assert.Single(answers);
    }

    /// <summary>
    /// Bits cannot be given back by anyone but Twitch, so a refused Power-up has nobody to tell –
    /// but it must still not be read out.
    /// </summary>
    [Fact]
    public void ARefusedPowerUpAnswersNobody()
    {
        AppSettings settings = Configured();
        (TtsService service, List<string> played) = Service(settings);
        var answers = new List<RedemptionStatus>();
        service.Answered += (_, status, _) => answers.Add(status);

        service.Handle(Request() with { Source = TtsSource.PowerUp, Refundable = false, RewardId = "" });
        Assert.True(service.Reject("r1"));

        Assert.Empty(played);
        Assert.Empty(answers);
    }

    /// <summary>
    /// A reading nobody answered during the whole stream is not one the viewer still wants to hear,
    /// and leaving it would mean their points sitting spent on silence.
    /// </summary>
    [Fact]
    public void LetsGoOfARequestNobodyAnswered()
    {
        AppSettings settings = Configured();
        settings.Tts.ApprovalTimeoutSeconds = 30;
        (TtsService service, _) = Service(settings);
        var answers = new List<RedemptionStatus>();
        service.Answered += (_, status, _) => answers.Add(status);

        service.Handle(Request() with { At = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5) });
        service.Sweep();

        Assert.Empty(service.Snapshot());
        Assert.Equal([RedemptionStatus.Canceled], answers);
    }

    /// <summary>
    /// The voice never arrived – no credit, no network, a rejected key. The viewer heard nothing, so
    /// this is a refund rather than a purchase quietly spent on silence.
    /// </summary>
    [Fact]
    public async Task PaysBackAReadingThatCouldNotBePlayed()
    {
        AppSettings settings = Configured(requireApproval: false);
        var secrets = new SpeechSecretStore(SpeechFixture.TempPath(".bin"));
        secrets.Save(new SpeechSecrets("deepseek", "eleven"));
        TtsService service = SpeechFixture.Tts(settings, secrets: secrets,
            play: (_, _) => throw new SpeechException("ingen ljudkälla i OBS"));
        var answers = new List<RedemptionStatus>();
        service.Answered += (_, status, _) => answers.Add(status);

        service.Handle(Request());
        await SettleAsync(service);

        Assert.Equal([RedemptionStatus.Canceled], answers);
    }

    [Fact]
    public void RefusesWhatItCannotRead()
    {
        AppSettings settings = Configured();
        (TtsService service, _) = Service(settings);

        // A redemption with nothing typed in it – a reward that never asked for input.
        TtsOutcome empty = service.Handle(Request(text: "   "));
        Assert.False(empty.Accepted);
        Assert.Contains("ingen text", empty.Reason);

        settings.Tts.Enabled = false;
        TtsOutcome off = service.Handle(Request("r2"));
        Assert.False(off.Accepted);
        Assert.Empty(service.Snapshot());
    }

    /// <summary>
    /// A queue nobody will reach the end of is worse than a refusal the viewer hears about now – and
    /// on the channel points route the refusal hands the points straight back.
    /// </summary>
    [Fact]
    public void RefusesOnceTheQueueIsFull()
    {
        AppSettings settings = Configured();
        settings.Tts.QueueLimit = 2;
        (TtsService service, _) = Service(settings);

        Assert.True(service.Handle(Request("r1")).Accepted);
        Assert.True(service.Handle(Request("r2")).Accepted);

        TtsOutcome full = service.Handle(Request("r3"));
        Assert.False(full.Accepted);
        Assert.Contains("full", full.Reason);
        Assert.Equal(2, service.Snapshot().Count);
    }

    /// <summary>
    /// The same redemption can arrive twice – a reconnect delivers on both sockets for a moment.
    /// Reading it out twice would be bad; answering Twitch about it twice would be worse.
    /// </summary>
    [Fact]
    public void IgnoresTheSameRedemptionArrivingTwice()
    {
        AppSettings settings = Configured();
        (TtsService service, _) = Service(settings);

        service.Handle(Request());
        service.Handle(Request());

        Assert.Single(service.Snapshot());
    }

    /// <summary>
    /// The one mistake here that silently keeps a viewer's money: stopping during the ElevenLabs
    /// call is a reading nobody heard a syllable of, and it used to be booked as delivered because
    /// the same catch covered both the synthesis and the playback.
    /// </summary>
    [Fact]
    public async Task PaysBackAReadingStoppedBeforeItWasHeard()
    {
        AppSettings settings = Configured(requireApproval: false);
        var secrets = new SpeechSecretStore(SpeechFixture.TempPath(".bin"));
        secrets.Save(new SpeechSecrets("deepseek", "eleven"));

        // A handler that never answers stands in for a slow ElevenLabs: the stop lands while the
        // synthesis is still in flight, before any audio could reach an output.
        TtsService service = SpeechFixture.Tts(settings, new HangingHandler(), secrets,
            play: (_, _) => Task.CompletedTask);
        var answers = new List<RedemptionStatus>();
        service.Answered += (_, status, _) => answers.Add(status);

        service.Handle(Request());
        for (int attempt = 0; attempt < 100 && !service.Snapshot().Any(entry => entry.State == "speaking"); attempt++)
            await Task.Delay(25);
        service.Stop();
        await SettleAsync(service);

        Assert.Equal([RedemptionStatus.Canceled], answers);
    }

    /// <summary>
    /// Once it is actually playing the viewers have heard some of it, and cutting it short is the
    /// streamer's decision rather than the viewer's fault to pay for.
    /// </summary>
    [Fact]
    public async Task DoesNotPayBackAReadingStoppedWhileItPlayed()
    {
        AppSettings settings = Configured(requireApproval: false);
        var secrets = new SpeechSecretStore(SpeechFixture.TempPath(".bin"));
        secrets.Save(new SpeechSecrets("deepseek", "eleven"));
        var reached = new TaskCompletionSource();
        TtsService service = SpeechFixture.Tts(settings, secrets: secrets, play: async (_, token) =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        var answers = new List<RedemptionStatus>();
        service.Answered += (_, status, _) => answers.Add(status);

        service.Handle(Request());
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Stop();
        await SettleAsync(service);

        Assert.Equal([RedemptionStatus.Fulfilled], answers);
    }

    /// <summary>
    /// A test started over a paid reading would take that reading off the air – there is one audio
    /// element at the far end – and leave the queue behind it waiting on an acknowledgement that is
    /// never coming. A viewer who paid outranks a button.
    /// </summary>
    [Fact]
    public async Task TheTestButtonWaitsForAPaidReading()
    {
        AppSettings settings = Configured(requireApproval: false);
        var secrets = new SpeechSecretStore(SpeechFixture.TempPath(".bin"));
        secrets.Save(new SpeechSecrets("deepseek", "eleven"));
        var reached = new TaskCompletionSource();
        TtsService service = SpeechFixture.Tts(settings, secrets: secrets, play: async (_, token) =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });

        service.Handle(Request());
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        SpeechException refused = await Assert.ThrowsAsync<SpeechException>(() => service.SpeakTestAsync("test"));
        Assert.Contains("pågår", refused.Message);
    }

    /// <summary>
    /// The stop button reaches whatever is at the speakers, a test included. It has to arrive as the
    /// exception the settings window already handles – a bare cancellation out of here would take an
    /// async void click handler, and the app, with it.
    /// </summary>
    [Fact]
    public async Task StoppingATestIsAnOrdinaryErrorRatherThanACrash()
    {
        AppSettings settings = Configured();
        var secrets = new SpeechSecretStore(SpeechFixture.TempPath(".bin"));
        secrets.Save(new SpeechSecrets("deepseek", "eleven"));
        var reached = new TaskCompletionSource();
        TtsService service = SpeechFixture.Tts(settings, secrets: secrets, play: async (_, token) =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });

        Task test = service.SpeakTestAsync("hej");
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Stop();

        await Assert.ThrowsAsync<SpeechException>(() => test);
        // And the speakers are handed back, so the next test is not refused for ever after.
        Assert.False(service.Stop());
    }

    /// <summary>
    /// Letting go is not the same as settling: the app is closing or has left the channel, and the
    /// redemption is meant to stay unfulfilled for the next connection's sweep to find. Answering it
    /// from under a disappearing window is how a refund gets fired at the wrong channel.
    /// </summary>
    [Fact]
    public void LettingGoOfTheQueueAnswersNobody()
    {
        AppSettings settings = Configured();
        (TtsService service, _) = Service(settings);
        var answers = new List<RedemptionStatus>();
        service.Answered += (_, status, _) => answers.Add(status);

        service.Handle(Request());
        service.Reset();

        Assert.Empty(service.Snapshot());
        Assert.Empty(answers);
    }

    /// <summary>
    /// What the sweep asks before it pays back a redemption from before the app was listening. A
    /// reconnect gives it a fresh cutoff, and everything waiting on the streamer is older than that
    /// – so without this, coming back from a dropped connection would refund the request on screen.
    /// </summary>
    [Fact]
    public void SaysWhichRedemptionsItIsStillHolding()
    {
        AppSettings settings = Configured();
        (TtsService service, _) = Service(settings);

        service.Handle(Request("r1"));

        Assert.True(service.Holds("r1"));
        Assert.False(service.Holds("r2"));

        service.Reject("r1");
        // Once settled it is nobody's to claim, and the sweep may clean it up like any other.
        Assert.False(service.Holds("r1"));
    }

    /// <summary>
    /// SpeakAsync answers rather than throws for everything that was foreseen. Anything unforeseen
    /// used to leave the entry marked as speaking and the speakers claimed for good – the loop refuses
    /// to start a reading while they are held – so every request after it queued and none of them were
    /// ever read.
    /// </summary>
    [Fact]
    public async Task AnUnexpectedFailureDoesNotTakeTheWholeQueueWithIt()
    {
        AppSettings settings = Configured(requireApproval: false);
        var secrets = new SpeechSecretStore(SpeechFixture.TempPath(".bin"));
        secrets.Save(new SpeechSecrets("deepseek", "eleven"));
        var played = new List<string>();
        // Neither a SpeechException nor a network one: nothing on the way up catches this.
        TtsService service = SpeechFixture.Tts(settings, secrets: secrets, play: (clip, _) =>
        {
            lock (played) played.Add(clip.FilePath);
            return played.Count == 1 ? throw new InvalidOperationException("något oväntat") : Task.CompletedTask;
        });
        var answers = new List<RedemptionStatus>();
        service.Answered += (_, status, _) => answers.Add(status);

        service.Handle(Request("r1"));
        await SettleAsync(service);
        service.Handle(Request("r2"));
        await SettleAsync(service);

        Assert.Equal(2, played.Count);
        // The first is a reading nobody heard, so the points go back; the second is read as usual.
        Assert.Equal([RedemptionStatus.Canceled, RedemptionStatus.Fulfilled], answers);
    }

    /// <summary>
    /// The streamer worked the channel's own queue while the request sat here waiting for a yes. The
    /// refund is final – Twitch has taken the redemption out of the queue – so the message must not be
    /// read after all, and above all must not be answered at a redemption that is no longer there.
    /// </summary>
    [Fact]
    public async Task ARedemptionRefundedInTwitchIsNeitherReadNorAnswered()
    {
        AppSettings settings = Configured();
        (TtsService service, List<string> played) = Service(settings);
        var answers = new List<RedemptionStatus>();
        service.Answered += (_, status, _) => answers.Add(status);

        service.Handle(Request("r1"));
        Assert.True(service.HandleExternalUpdate("r1", "CANCELED"));

        Assert.Empty(service.Snapshot());
        Assert.False(service.Holds("r1"));
        Assert.Empty(answers);
        // Nothing left to approve, and nothing read.
        Assert.False(service.Approve("r1"));
        await SettleAsync(service);
        Assert.Empty(played);
        // Somebody else's redemption is not this queue's to claim – the pets' ledger is owed it.
        Assert.False(service.HandleExternalUpdate("r2", "CANCELED"));
    }

    /// <summary>
    /// A refund that lands while the words are coming out. The pets do the same with a creature
    /// refunded in the dashboard: what was paid back stops being delivered.
    /// </summary>
    [Fact]
    public async Task ARefundInTwitchTakesAReadingOffTheSpeakers()
    {
        AppSettings settings = Configured(requireApproval: false);
        var secrets = new SpeechSecretStore(SpeechFixture.TempPath(".bin"));
        secrets.Save(new SpeechSecrets("deepseek", "eleven"));
        var reached = new TaskCompletionSource();
        TtsService service = SpeechFixture.Tts(settings, secrets: secrets, play: async (_, token) =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        var answers = new List<RedemptionStatus>();
        service.Answered += (_, status, _) => answers.Add(status);

        service.Handle(Request("r1"));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.HandleExternalUpdate("r1", "CANCELED"));
        await SettleAsync(service);

        Assert.Empty(service.Snapshot());
        Assert.Empty(answers);
        // And the speakers are handed back rather than held by a reading nobody will answer for.
        Assert.False(service.Stop());
    }

    /// <summary>
    /// Settled in the dashboard some other way. The streamer never said no, so the reading goes ahead
    /// – but the answer at the end is dropped: Twitch has closed the redemption and would refuse it
    /// five times over before the app gave up on it.
    /// </summary>
    [Fact]
    public async Task AReadingClosedInTwitchIsStillReadButNoLongerAnswered()
    {
        AppSettings settings = Configured();
        (TtsService service, List<string> played) = Service(settings);
        var answers = new List<RedemptionStatus>();
        service.Answered += (_, status, _) => answers.Add(status);

        service.Handle(Request("r1"));
        Assert.True(service.HandleExternalUpdate("r1", "FULFILLED"));
        Assert.True(service.Approve("r1"));
        await SettleAsync(service);

        Assert.Single(played);
        Assert.Empty(answers);
    }

    /// <summary>
    /// Long messages are cut before anything is billed for them, and the bar shows what will
    /// actually be read rather than what was typed.
    /// </summary>
    [Fact]
    public void ShowsTheMessageAsItWillBeRead()
    {
        AppSettings settings = Configured();
        settings.Tts.MaxCharacters = 20;
        (TtsService service, _) = Service(settings);

        service.Handle(Request(text: "hej på dig din underbara lilla vän i chatten"));

        Assert.Equal("hej på dig din", Assert.Single(service.Snapshot()).Text);
    }
}
