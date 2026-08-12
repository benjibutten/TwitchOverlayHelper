using TwitchOverlayHelper.Overlay;

namespace TwitchOverlayHelper.Tests;

/// <summary>
/// The busy-chat cases. There is only one glow to hand out, so what matters is who gets it when
/// several things ask at once – and that the edges always go dark again.
/// </summary>
public sealed class EdgeAlertSchedulerTests
{
    private const double Duration = 6;
    private static readonly DateTimeOffset Start = new(2026, 8, 8, 20, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => Start + TimeSpan.FromSeconds(seconds);

    [Fact]
    public void TheFirstAlertAlwaysPlays()
    {
        var scheduler = new EdgeAlertScheduler();

        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, Start));
    }

    /// <summary>
    /// The raid case: twenty first-time chatters must not become twenty glows. The ones arriving
    /// while the light is up hold it open, and once it has been stretched as far as it goes the rest
    /// are dropped rather than queued.
    /// </summary>
    [Fact]
    public void ARushOfNewChattersIsOneGlow()
    {
        var scheduler = new EdgeAlertScheduler();
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, Start));

        // Inside the glow: held open, never restarted, up to twice its own length.
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, At(2)));
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, At(4)));
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, At(8)));
        // Twelve seconds is the ceiling; the arrivals after it are dropped, not queued.
        Assert.Null(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, At(11)));
        Assert.Null(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, At(13)));
    }

    /// <summary>Once it has ended, welcomes stay quiet for the cooldown and then work again.</summary>
    [Fact]
    public void WelcomesGoQuietAfterOneHasBeenShown()
    {
        var scheduler = new EdgeAlertScheduler();
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, Start));

        double cooldownEnds = Duration + EdgeAlertScheduler.NewChatterCooldown.TotalSeconds;
        Assert.Null(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, At(cooldownEnds - 1)));
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, At(cooldownEnds + 1)));
    }

    /// <summary>A call is the streamer being asked for; a first-time hello does not get to hide it.</summary>
    [Fact]
    public void AWelcomeCannotInterruptACall()
    {
        var scheduler = new EdgeAlertScheduler();
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.ModCall, Duration, Start));

        Assert.Null(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, At(1)));
        Assert.Null(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, At(5)));
        // The call is over, so the next hello is welcome again.
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, At(7)));
    }

    /// <summary>The other way round: a call takes the light off a welcome the moment it arrives.</summary>
    [Fact]
    public void ACallTakesOverFromAWelcome()
    {
        var scheduler = new EdgeAlertScheduler();
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, Start));

        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.ModCall, Duration, At(1)));
    }

    /// <summary>
    /// Several moderators calling at once is one call, not four – but never silently: the light is
    /// held open for each of them until it has been on twice as long as one call.
    /// </summary>
    [Fact]
    public void SeveralModsCallingAtOnceHoldTheSameGlow()
    {
        var scheduler = new EdgeAlertScheduler();
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.ModCall, Duration, Start));

        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.ModCall, Duration, At(1)));
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.ModCall, Duration, At(3)));
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.ModCall, Duration, At(8)));
        Assert.Null(scheduler.PlayFor(EdgeAlertKind.ModCall, Duration, At(11)));
    }

    /// <summary>
    /// Unlike welcomes, a call has no cooldown. When mods keep needing the streamer they keep being
    /// able to say so – the ceiling only stops one glow from lasting forever.
    /// </summary>
    [Fact]
    public void ACallAfterTheGlowEndedPlaysAgainImmediately()
    {
        var scheduler = new EdgeAlertScheduler();
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.ModCall, Duration, Start));

        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.ModCall, Duration, At(Duration + 0.5)));
    }

    /// <summary>
    /// The ceiling has to reach the light itself, not only the decision to light it. A glow being
    /// held open is given what is left of its twelve seconds – answering with a full six instead
    /// would have the window play on until fourteen, which is the one thing the ceiling is for.
    /// </summary>
    [Fact]
    public void AHeldGlowIsOnlyGivenTheTimeLeftUnderTheCeiling()
    {
        var scheduler = new EdgeAlertScheduler();
        Assert.Equal(TimeSpan.FromSeconds(Duration), scheduler.PlayFor(EdgeAlertKind.ModCall, Duration, Start));

        // Two seconds in a whole alert still fits under the ceiling, so nothing is taken off it.
        Assert.Equal(TimeSpan.FromSeconds(Duration), scheduler.PlayFor(EdgeAlertKind.ModCall, Duration, At(2)));
        // Seven seconds in the ceiling is five seconds away, and five seconds is all this one gets.
        Assert.Equal(TimeSpan.FromSeconds(5), scheduler.PlayFor(EdgeAlertKind.ModCall, Duration, At(7)));
    }

    /// <summary>
    /// A reading waiting for an answer sits between the other two. A stream of first-time hellos
    /// must not bury something the streamer has to decide on – and a viewer's bits are sitting in it
    /// while it waits.
    /// </summary>
    [Fact]
    public void AReadingTakesTheLightFromAWelcome()
    {
        var scheduler = new EdgeAlertScheduler();
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, Start));

        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.TtsRequest, Duration, At(1)));
        // And the welcomes behind it wait rather than taking it back.
        Assert.Null(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, At(2)));
    }

    /// <summary>But a moderator calling for the streamer is still the more urgent of the two.</summary>
    [Fact]
    public void ACallStillOutranksAReading()
    {
        var scheduler = new EdgeAlertScheduler();
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.TtsRequest, Duration, Start));

        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.ModCall, Duration, At(1)));
        Assert.Null(scheduler.PlayFor(EdgeAlertKind.TtsRequest, Duration, At(2)));
    }

    /// <summary>
    /// Several redemptions at once is one glow held open, the same as several mods calling – and
    /// unlike a welcome, a reading has no cooldown: each one has to be answered on its own.
    /// </summary>
    [Fact]
    public void SeveralReadingsHoldTheSameGlowAndNeverGoQuiet()
    {
        var scheduler = new EdgeAlertScheduler();
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.TtsRequest, Duration, Start));

        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.TtsRequest, Duration, At(3)));
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.TtsRequest, Duration, At(8)));
        // Twelve seconds is the ceiling, the same as it is for a call.
        Assert.Null(scheduler.PlayFor(EdgeAlertKind.TtsRequest, Duration, At(11)));
        // The ceiling ends one glow; it does not silence the next redemption.
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.TtsRequest, Duration, At(13)));
    }

    [Fact]
    public void ResetForgetsBothTheGlowAndTheCooldown()
    {
        var scheduler = new EdgeAlertScheduler();
        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, Start));
        Assert.Null(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, At(Duration + 1)));

        scheduler.Reset();

        Assert.NotNull(scheduler.PlayFor(EdgeAlertKind.NewChatter, Duration, At(Duration + 1)));
    }
}
