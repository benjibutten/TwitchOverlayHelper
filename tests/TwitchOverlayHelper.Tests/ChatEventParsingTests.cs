using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Tests;

public sealed class ChatEventParsingTests
{
    private static ChatEvent Parse(string raw)
    {
        Assert.True(IrcMessageParser.TryParseUserNotice(raw, out ChatEvent? chatEvent));
        return chatEvent!;
    }

    [Fact]
    public void ParsesANewSub()
    {
        const string raw = "@badges=staff/1;display-name=Benji;id=e1;login=benji;msg-id=sub;msg-param-cumulative-months=1;" +
                           "msg-param-sub-plan=Prime;system-msg=Benji\\ssubscribed\\swith\\sPrime.;tmi-sent-ts=1700000000000;user-id=12 " +
                           ":tmi.twitch.tv USERNOTICE #demo";

        ChatEvent result = Parse(raw);

        Assert.Equal(ChatEventType.Subscription, result.Type);
        Assert.Equal("Benji", result.DisplayName);
        Assert.Equal("benji", result.UserLogin);
        Assert.Equal("12", result.UserId);
        Assert.Equal("Prime", result.Tier);
        Assert.Equal("Benji prenumererar nu (Prime)", ChatEventText.Describe(result));
    }

    [Fact]
    public void ParsesAResubWithItsMonthsAndAttachedMessage()
    {
        const string raw = "@display-name=Kajsa;id=e2;login=kajsa;msg-id=resub;msg-param-cumulative-months=12;" +
                           "msg-param-months=0;msg-param-should-share-streak=1;msg-param-streak-months=5;msg-param-sub-plan=1000;" +
                           "system-msg=Kajsa\\ssubscribed\\sat\\sTier\\s1.;tmi-sent-ts=1700000000000 " +
                           ":tmi.twitch.tv USERNOTICE #demo :tack för allt!";

        ChatEvent result = Parse(raw);

        Assert.Equal(ChatEventType.Subscription, result.Type);
        Assert.Equal(12, result.Months);
        Assert.Equal(5, result.StreakMonths);
        Assert.Equal("tack för allt!", result.Message);
        Assert.Equal("Kajsa har prenumererat i 12 månader (nivå 1) – 5 i rad", ChatEventText.Describe(result));
    }

    // Twitch sends the streak tags whether or not the viewer shared the streak; the sharing flag is
    // what makes them true, and repeating an unshared number would claim something it does not say.
    [Fact]
    public void IgnoresTheStreakWhenTheViewerDidNotShareIt()
    {
        const string raw = "@display-name=Kajsa;id=e3;login=kajsa;msg-id=resub;msg-param-cumulative-months=12;" +
                           "msg-param-should-share-streak=0;msg-param-streak-months=5;msg-param-sub-plan=1000 " +
                           ":tmi.twitch.tv USERNOTICE #demo";

        Assert.Null(Parse(raw).StreakMonths);
    }

    [Fact]
    public void ParsesAGiftSubWithItsRecipient()
    {
        const string raw = "@display-name=Pelle;id=e4;login=pelle;msg-id=subgift;msg-param-months=3;" +
                           "msg-param-recipient-display-name=NyTittare;msg-param-recipient-user-name=nytittare;" +
                           "msg-param-sub-plan=2000;tmi-sent-ts=1700000000000 :tmi.twitch.tv USERNOTICE #demo";

        ChatEvent result = Parse(raw);

        Assert.Equal(ChatEventType.SubGift, result.Type);
        Assert.Equal("NyTittare", result.RecipientDisplayName);
        Assert.Equal("Pelle gav en prenumeration till NyTittare (nivå 2)", ChatEventText.Describe(result));
    }

    [Fact]
    public void ParsesACommunityGiftAsOneEventWithACount()
    {
        const string raw = "@display-name=Pelle;id=e5;login=pelle;msg-id=submysterygift;msg-param-mass-gift-count=20;" +
                           "msg-param-sub-plan=1000;tmi-sent-ts=1700000000000 :tmi.twitch.tv USERNOTICE #demo";

        ChatEvent result = Parse(raw);

        Assert.Equal(ChatEventType.CommunityGift, result.Type);
        Assert.Equal(20, result.GiftCount);
        Assert.Equal("Pelle gav bort 20 prenumerationer (nivå 1)", ChatEventText.Describe(result));
    }

    [Fact]
    public void ParsesARaidWithItsViewerCount()
    {
        const string raw = "@display-name=Streamern;id=e6;login=streamern;msg-id=raid;msg-param-displayName=Streamern;" +
                           "msg-param-login=streamern;msg-param-viewerCount=42;tmi-sent-ts=1700000000000 " +
                           ":tmi.twitch.tv USERNOTICE #demo";

        ChatEvent result = Parse(raw);

        Assert.Equal(ChatEventType.Raid, result.Type);
        Assert.Equal(42, result.ViewerCount);
        Assert.Equal("Streamern raidar med 42 tittare", ChatEventText.Describe(result));
    }

    // Announcements are sent in practice but are missing from Twitch's IRC documentation, so this
    // test is what pins the shape the dock relies on.
    [Fact]
    public void ParsesAnAnnouncementWithItsColourAndText()
    {
        const string raw = "@display-name=Streamern;id=e7;login=streamern;msg-id=announcement;msg-param-color=ORANGE;" +
                           "tmi-sent-ts=1700000000000 :tmi.twitch.tv USERNOTICE #demo :Vi kör 20 minuter till!";

        ChatEvent result = Parse(raw);

        Assert.Equal(ChatEventType.Announcement, result.Type);
        Assert.Equal("ORANGE", result.AnnouncementColor);
        Assert.Equal("Vi kör 20 minuter till!", result.Message);
        Assert.Equal("Meddelande från Streamern", ChatEventText.Describe(result));
    }

    [Fact]
    public void ParsesABitsBadgeTier()
    {
        const string raw = "@display-name=Lisa;id=e8;login=lisa;msg-id=bitsbadgetier;msg-param-threshold=1000;" +
                           "tmi-sent-ts=1700000000000 :tmi.twitch.tv USERNOTICE #demo";

        ChatEvent result = Parse(raw);

        Assert.Equal(ChatEventType.BitsBadge, result.Type);
        Assert.Equal(1000, result.Bits);
    }

    [Fact]
    public void ParsesAWatchStreakMilestone()
    {
        const string raw = "@display-name=Lisa;id=e9;login=lisa;msg-id=viewermilestone;msg-param-category=watch-streak;" +
                           "msg-param-value=8;tmi-sent-ts=1700000000000 :tmi.twitch.tv USERNOTICE #demo";

        ChatEvent result = Parse(raw);

        Assert.Equal(ChatEventType.WatchStreak, result.Type);
        Assert.Equal("Lisa har sett 8 sändningar i rad", ChatEventText.Describe(result));
    }

    [Fact]
    public void ParsesAFirstTimeChatterRitual()
    {
        const string raw = "@display-name=NyTittare;id=e10;login=nytittare;msg-id=ritual;msg-param-ritual-name=new_chatter;" +
                           "tmi-sent-ts=1700000000000 :tmi.twitch.tv USERNOTICE #demo :hej allihop";

        ChatEvent result = Parse(raw);

        Assert.Equal(ChatEventType.NewChatter, result.Type);
        Assert.Equal("hej allihop", result.Message);
    }

    // The whole point of keeping system-msg: a msg-id nobody has seen before still becomes a
    // readable line rather than vanishing from the chat views.
    [Fact]
    public void FallsBackToTwitchsOwnWordingForAnUnknownMsgId()
    {
        const string raw = "@display-name=Benji;id=e11;login=benji;msg-id=nagot-helt-nytt;" +
                           "system-msg=Benji\\sdid\\ssomething\\snew!;tmi-sent-ts=1700000000000 " +
                           ":tmi.twitch.tv USERNOTICE #demo";

        ChatEvent result = Parse(raw);

        Assert.Equal(ChatEventType.Other, result.Type);
        Assert.Equal("Benji did something new!", result.SystemMessage);
        Assert.Equal("Benji did something new!", ChatEventText.Describe(result));
    }

    [Fact]
    public void StillSaysSomethingWhenAnUnknownNoticeCarriesNoSystemMessage()
    {
        const string raw = "@display-name=Benji;id=e12;login=benji;msg-id=nagot-helt-nytt;tmi-sent-ts=1700000000000 " +
                           ":tmi.twitch.tv USERNOTICE #demo";

        Assert.Equal("Benji gjorde något i chatten", ChatEventText.Describe(Parse(raw)));
    }

    [Fact]
    public void ReadsEmotesInTheAttachedMessage()
    {
        const string raw = "@display-name=Kajsa;emotes=25:5-9;id=e13;login=kajsa;msg-id=resub;msg-param-cumulative-months=2;" +
                           "tmi-sent-ts=1700000000000 :tmi.twitch.tv USERNOTICE #demo :tack Kappa";

        ChatEvent result = Parse(raw);

        EmoteSpan emote = Assert.Single(result.Emotes);
        Assert.Equal("25", emote.EmoteId);
        Assert.Equal("Kappa", result.Message!.Substring(emote.Start, emote.Length));
    }

    [Fact]
    public void IgnoresOrdinaryChatLinesAndModerationLines()
    {
        Assert.False(IrcMessageParser.TryParseUserNotice(
            "@display-name=Benji;id=abc :benji!benji@benji.tmi.twitch.tv PRIVMSG #demo :Hej", out _));
        Assert.False(IrcMessageParser.TryParseUserNotice(
            "@target-user-id=99 :tmi.twitch.tv CLEARCHAT #demo :spammer", out _));
    }

    // A cheer never comes as a notice: it is an ordinary message that happens to carry bits.
    [Fact]
    public void ReadsBitsFromAnOrdinaryChatMessage()
    {
        const string raw = "@bits=500;display-name=Pelle;id=abc;user-id=7 :pelle!pelle@pelle.tmi.twitch.tv PRIVMSG #demo :Cheer500 bra jobbat";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out ChatMessage? message));
        Assert.Equal(500, message!.Bits);
        Assert.Equal("Cheer500 bra jobbat", message.Text);
    }

    [Fact]
    public void LeavesBitsUnsetOnAMessageWithoutThem()
    {
        const string raw = "@display-name=Pelle;id=abc;user-id=7 :pelle!pelle@pelle.tmi.twitch.tv PRIVMSG #demo :bra jobbat";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out ChatMessage? message));
        Assert.Null(message!.Bits);
    }
}
