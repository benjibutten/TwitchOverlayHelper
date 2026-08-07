using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Tests;

/// <summary>
/// Power-ups, which reach the app two different ways. A message effect rides on the PRIVMSG itself
/// and works for anyone; a gigantified emote only exists on EventSub and has to be paired with the
/// chat line it belongs to afterwards.
/// </summary>
public sealed class MessageEffectParsingTests
{
    private static ChatMessage Parse(string raw)
    {
        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out ChatMessage? message));
        return message!;
    }

    // Undocumented but sent in practice. Reading it here is what makes message effects show up for a
    // logged-out viewer in someone else's channel, where EventSub can tell us nothing at all.
    [Fact]
    public void ReadsTheMessageEffectStraightOffTheChatLine()
    {
        const string raw = "@animation-id=simmer;display-name=Kajsa;id=m1;user-id=12 " +
                           ":kajsa!kajsa@kajsa.tmi.twitch.tv PRIVMSG #demo :grattis!";

        Assert.Equal("simmer", Parse(raw).MessageEffectId);
    }

    [Fact]
    public void LeavesAnOrdinaryLineWithoutAnEffect()
    {
        const string raw = "@display-name=Kajsa;id=m1;user-id=12 :kajsa!kajsa@kajsa.tmi.twitch.tv PRIVMSG #demo :hej";

        Assert.Null(Parse(raw).MessageEffectId);
    }
}

/// <summary>
/// Which emote a Gigantify power-up enlarged. Twitch does not say – that is open issue
/// twitchdev/issues#1047 – so this is the one piece of the feature that rests on a convention.
/// </summary>
public sealed class GigantifiedEmoteIndexTests
{
    private static ChatMessage WithEmotes(string? gigantified, params EmoteSpan[] emotes) =>
        new("m1", "Kajsa", "Kappa PogChamp Kappa", null, [], false, false, DateTimeOffset.Now, emotes)
        {
            GigantifiedEmoteId = gigantified
        };

    [Fact]
    public void SaysNothingIsEnlargedOnAnOrdinaryMessage()
    {
        Assert.Equal(-1, WithEmotes(null, new EmoteSpan("25", 0, 5)).GigantifiedEmoteIndex);
    }

    // The convention: without a named emote, the last one in the message is the big one.
    [Fact]
    public void FallsBackToTheLastEmoteWhenTwitchDidNotNameOne()
    {
        ChatMessage message = WithEmotes("", new EmoteSpan("25", 0, 5), new EmoteSpan("88", 6, 9));

        Assert.Equal(1, message.GigantifiedEmoteIndex);
    }

    // channel.bits.use does carry the emote id, so where we have it we do better than guess.
    [Fact]
    public void PicksTheNamedEmoteRatherThanTheLastOne()
    {
        ChatMessage message = WithEmotes("88", new EmoteSpan("25", 0, 5), new EmoteSpan("88", 6, 9), new EmoteSpan("25", 16, 5));

        Assert.Equal(1, message.GigantifiedEmoteIndex);
    }

    // The same emote written twice: the later one is the one that was made big.
    [Fact]
    public void TakesTheLastSpanWhenTheNamedEmoteAppearsMoreThanOnce()
    {
        ChatMessage message = WithEmotes("25", new EmoteSpan("25", 0, 5), new EmoteSpan("88", 6, 9), new EmoteSpan("25", 16, 5));

        Assert.Equal(2, message.GigantifiedEmoteIndex);
    }

    // A named emote that is not in the message any more leaves the convention, not an empty answer:
    // something in this line was gigantified and showing nothing would be the worse of the two.
    [Fact]
    public void KeepsToTheConventionWhenTheNamedEmoteIsNotInTheMessage()
    {
        ChatMessage message = WithEmotes("999", new EmoteSpan("25", 0, 5), new EmoteSpan("88", 6, 9));

        Assert.Equal(1, message.GigantifiedEmoteIndex);
    }

    [Fact]
    public void HasNothingToEnlargeInAMessageWithoutEmotes()
    {
        Assert.Equal(-1, WithEmotes("25").GigantifiedEmoteIndex);
    }
}

/// <summary>
/// Pairing the power-up with its message. The two arrive on different connections with nothing
/// linking them, and either one can be first – so both orders have to end with the same marked line.
/// </summary>
public sealed class PowerUpTrackerTests
{
    private static ChatMessage Message(string id, string userId, string text, string emoteId = "25") =>
        new(id, "Kajsa", text, null, [], false, false, DateTimeOffset.Now, [new EmoteSpan(emoteId, 0, 5)])
        {
            UserId = userId
        };

    private static GigantifiedEmote PowerUp(string userId, string emoteId, string text) =>
        new(userId, emoteId, text, DateTimeOffset.Now);

    [Fact]
    public void MarksTheMessageWhenThePowerUpGotHereFirst()
    {
        var tracker = new PowerUpTracker();
        Assert.Null(tracker.Match(PowerUp("12", "25", "Kappa hej")));

        ChatMessage enriched = tracker.Enrich(Message("m1", "12", "Kappa hej"));

        Assert.Equal("25", enriched.GigantifiedEmoteId);
        Assert.Equal(0, enriched.GigantifiedEmoteIndex);
    }

    // The other order, which is the one that costs something: the line has already gone out to the
    // views, so the marked copy has to be sent after it.
    [Fact]
    public void HandsBackTheMarkedMessageWhenTheChatLineGotHereFirst()
    {
        var tracker = new PowerUpTracker();
        tracker.Enrich(Message("m1", "12", "Kappa hej"));

        ChatMessage? updated = tracker.Match(PowerUp("12", "25", "Kappa hej"));

        Assert.NotNull(updated);
        Assert.Equal("m1", updated!.Id);
        Assert.Equal("25", updated.GigantifiedEmoteId);
    }

    [Fact]
    public void NeverPutsOneViewersPowerUpOnAnothersMessage()
    {
        var tracker = new PowerUpTracker();
        tracker.Match(PowerUp("12", "25", "Kappa hej"));

        Assert.Null(tracker.Enrich(Message("m1", "999", "Kappa hej")).GigantifiedEmoteId);
    }

    // The text is what keeps a power-up from landing on whatever the viewer happens to type next.
    [Fact]
    public void WaitsForTheLineTheWordsActuallyMatch()
    {
        var tracker = new PowerUpTracker();
        tracker.Match(PowerUp("12", "25", "Kappa hej"));

        Assert.Null(tracker.Enrich(Message("m1", "12", "Kappa något helt annat")).GigantifiedEmoteId);
        Assert.Equal("25", tracker.Enrich(Message("m2", "12", "Kappa hej")).GigantifiedEmoteId);
    }

    [Fact]
    public void UsesAPowerUpOnceAndOnlyOnce()
    {
        var tracker = new PowerUpTracker();
        tracker.Match(PowerUp("12", "25", "Kappa hej"));

        Assert.Equal("25", tracker.Enrich(Message("m1", "12", "Kappa hej")).GigantifiedEmoteId);
        Assert.Null(tracker.Enrich(Message("m2", "12", "Kappa hej")).GigantifiedEmoteId);
    }

    // Only an emote can be gigantified, so a line without one can neither claim a waiting power-up
    // nor be claimed by a later one.
    [Fact]
    public void LeavesALineWithoutEmotesOutOfIt()
    {
        var tracker = new PowerUpTracker();
        var plain = new ChatMessage("m1", "Kajsa", "hej", null, [], false, false, DateTimeOffset.Now) { UserId = "12" };

        Assert.Null(tracker.Enrich(plain).GigantifiedEmoteId);
        Assert.Null(tracker.Match(PowerUp("12", "25", "hej")));
    }

    // Twitch marks the message field optional on channel.bits.use. Without the words, the emote is
    // the only thing left holding the match together – so it has to actually be checked, or the
    // viewer's next line would take the marker whatever it said.
    [Fact]
    public void StillNeedsTheRightEmoteWhenThePowerUpCameWithoutItsText()
    {
        var tracker = new PowerUpTracker();
        tracker.Match(PowerUp("12", "25", ""));

        Assert.Null(tracker.Enrich(Message("m1", "12", "PogChamp hej", emoteId: "88")).GigantifiedEmoteId);
        Assert.Equal("25", tracker.Enrich(Message("m2", "12", "Kappa hej")).GigantifiedEmoteId);
    }

    [Fact]
    public void NeverMarksALineThatDoesNotContainTheEnlargedEmote()
    {
        var tracker = new PowerUpTracker();
        tracker.Enrich(Message("m1", "12", "PogChamp hej", emoteId: "88"));

        Assert.Null(tracker.Match(PowerUp("12", "25", "PogChamp hej")));
    }

    [Fact]
    public void DropsBothHalvesWhenTheChannelChanges()
    {
        var tracker = new PowerUpTracker();
        tracker.Match(PowerUp("12", "25", "Kappa hej"));
        tracker.Enrich(Message("m1", "12", "Kappa annat"));

        tracker.Clear();

        Assert.Null(tracker.Enrich(Message("m2", "12", "Kappa hej")).GigantifiedEmoteId);
        Assert.Null(tracker.Match(PowerUp("12", "25", "Kappa annat")));
    }
}
