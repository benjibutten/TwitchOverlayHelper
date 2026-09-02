using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Tests;

public sealed class IrcMessageParserTests
{
    [Fact]
    public void ParsesMessageBadgesAndEscapedDisplayName()
    {
        const string raw = "@badges=broadcaster/1,subscriber/12;color=#9146FF;display-name=Benji\\sBoy;first-msg=1;id=abc;room-id=42;tmi-sent-ts=1700000000000 :benji!benji@benji.tmi.twitch.tv PRIVMSG #demo :Hej allihop!";

        bool parsed = IrcMessageParser.TryParseChatMessage(raw, out var message);

        Assert.True(parsed);
        Assert.Equal("Benji Boy", message!.DisplayName);
        Assert.Equal("Hej allihop!", message.Text);
        Assert.True(message.IsFirstMessage);
        Assert.Collection(message.Badges,
            badge => Assert.Equal("broadcaster", badge.SetId),
            badge => Assert.Equal("subscriber", badge.SetId));
        Assert.Equal("42", IrcMessageParser.TryGetRoomId(raw));
    }

    /// <summary>
    /// USERSTATE is how we learn what our own messages look like, since Twitch never sends them back
    /// to us. It has to survive being sent without an id, which is the case on JOIN.
    /// </summary>
    [Fact]
    public void ParsesUserState()
    {
        const string raw = "@badge-info=;badges=broadcaster/1;color=#9146FF;display-name=Benji\\sBoy;emote-sets=0;id=m9;mod=0;subscriber=0;user-type= :tmi.twitch.tv USERSTATE #demo";

        Assert.True(IrcMessageParser.TryParseUserState(raw, out UserState? state));
        Assert.Equal("m9", state!.MessageId);
        Assert.Equal("Benji Boy", state.DisplayName);
        Assert.Equal("#9146FF", state.Color);
        Assert.Equal("broadcaster", Assert.Single(state.Badges).SetId);

        const string onJoin = "@badge-info=;badges=;color=;display-name=Benji;emote-sets=0;mod=0;user-type= :tmi.twitch.tv USERSTATE #demo";

        Assert.True(IrcMessageParser.TryParseUserState(onJoin, out UserState? joined));
        Assert.Null(joined!.MessageId);
        Assert.Null(joined.Color);
        Assert.Empty(joined.Badges);
    }

    [Fact]
    public void AChatMessageIsNotAUserState()
    {
        const string raw = "@display-name=Kajsa;id=m1 :kajsa!kajsa@kajsa.tmi.twitch.tv PRIVMSG #demo :hej";

        Assert.False(IrcMessageParser.TryParseUserState(raw, out UserState? state));
        Assert.Null(state);
    }

    /// <summary>
    /// The mod tag, which is the only thing that actually says whether the sender can moderate. The
    /// badges here are a real lead moderator's: Twitch never sent the moderator badge to go with them.
    /// </summary>
    [Fact]
    public void ReadsTheModTagRatherThanGuessingFromBadges()
    {
        const string raw = "@badges=lead_moderator/1,founder/0,bits-leader/2;display-name=Benji;id=m1;mod=1;user-type=mod :benji!benji@benji.tmi.twitch.tv PRIVMSG #demo :!psst";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out var message));
        Assert.True(message!.HasModTag);
        Assert.True(message.IsModerator);
        Assert.False(message.IsBroadcaster);
    }

    [Fact]
    public void LeavesTheModTagOffForOrdinaryChatters()
    {
        const string raw = "@badges=subscriber/12;display-name=Kajsa;id=m2;mod=0;user-type= :kajsa!kajsa@kajsa.tmi.twitch.tv PRIVMSG #demo :!psst";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out var message));
        Assert.False(message!.HasModTag);
        Assert.False(message.IsModerator);
    }

    [Fact]
    public void ParsesChannelPointRewardId()
    {
        const string raw = "@custom-reward-id=abc-123-def;display-name=Kajsa;id=m1;user-id=7 :kajsa!kajsa@kajsa.tmi.twitch.tv PRIVMSG #demo :en pet tack!";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out var message));
        Assert.Equal("abc-123-def", message!.RewardId);
    }

    [Fact]
    public void LeavesRewardIdNullForOrdinaryMessages()
    {
        const string raw = "@display-name=Kajsa;id=m2 :kajsa!kajsa@kajsa.tmi.twitch.tv PRIVMSG #demo :hej";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out var message));
        Assert.Null(message!.RewardId);
    }

    [Theory]
    [InlineData("#Some_Channel", "some_channel")]
    [InlineData("https://www.twitch.tv/Twitch", "twitch")]
    [InlineData(" twitch ", "twitch")]
    public void NormalizesChannelInput(string input, string expected) =>
        Assert.Equal(expected, TwitchChatClient.NormalizeChannel(input));

    [Fact]
    public void ParsesEmotesSortedByPosition()
    {
        const string text = "Kappa hej Kappa PogChamp";
        var emotes = IrcMessageParser.ParseEmotes("305954156:16-23/25:0-4,10-14", text);

        Assert.Collection(emotes,
            emote => { Assert.Equal("25", emote.EmoteId); Assert.Equal(0, emote.Start); Assert.Equal(5, emote.Length); },
            emote => { Assert.Equal("25", emote.EmoteId); Assert.Equal(10, emote.Start); Assert.Equal(5, emote.Length); },
            emote => { Assert.Equal("305954156", emote.EmoteId); Assert.Equal(16, emote.Start); Assert.Equal(8, emote.Length); });
        Assert.Equal("Kappa", text.Substring(emotes[0].Start, emotes[0].Length));
        Assert.Equal("PogChamp", text.Substring(emotes[2].Start, emotes[2].Length));
    }

    [Fact]
    public void MapsEmoteIndicesThroughSurrogatePairs()
    {
        // Twitch counts indices in code points; the emoji occupies two UTF-16 chars.
        const string text = "\U0001F600 Kappa";
        var emotes = IrcMessageParser.ParseEmotes("25:2-6", text);

        var emote = Assert.Single(emotes);
        Assert.Equal("Kappa", text.Substring(emote.Start, emote.Length));
    }

    [Fact]
    public void IgnoresMalformedEmoteRanges()
    {
        const string text = "kort";
        Assert.Empty(IrcMessageParser.ParseEmotes("25:0-99/:1-2/x", text));
        Assert.Empty(IrcMessageParser.ParseEmotes(null, text));
    }

    /// <summary>
    /// The GIFs tier 2 and tier 3 subscribers send. Twitch writes the picture's description into the
    /// message itself and names the range covering it, so the line still reads without us – but the
    /// range is what lets the views put the picture back where the sender meant it to go.
    /// </summary>
    [Fact]
    public void ParsesGifTag()
    {
        const string url = "https://media4.giphy.com/media/joSNxeswxuc74Juo8X/giphy.gif?cid=095d7a5d&ep=v1_gifs_trending&rid=giphy.gif&ct=g";
        string raw = "@display-name=Fan;gifs=0-33|joSNxeswxuc74Juo8X|" + url
                     + ";id=g1 :fan!fan@fan.tmi.twitch.tv PRIVMSG #demo :[Y A Y Yes GIF by Djemilah Birnie]";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out var message));
        var gif = Assert.Single(message!.Gifs);
        Assert.Equal("joSNxeswxuc74Juo8X", gif.GifId);
        Assert.Equal(url, gif.Url);
        Assert.Equal("[Y A Y Yes GIF by Djemilah Birnie]", message.Text.Substring(gif.Start, gif.Length));
    }

    /// <summary>
    /// A comma separates two GIFs, and is also a character a query string may legally carry. The
    /// entry only starts where "digits-digits|" does, or one address would be torn in half.
    /// </summary>
    [Fact]
    public void KeepsACommaInsideAGifAddress()
    {
        const string url = "https://media.giphy.com/media/abc/giphy.gif?ep=v1,gifs&ct=g";
        string raw = "@gifs=0-6|abc|" + url + " :fan!fan@fan.tmi.twitch.tv PRIVMSG #demo :[a GIF]";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out var message));
        Assert.Equal(url, Assert.Single(message!.Gifs).Url);
    }

    [Fact]
    public void ParsesSeveralGifsSortedByPosition()
    {
        const string text = "[b GIF] mitt emellan [a GIF]";
        var gifs = IrcMessageParser.ParseGifs(
            "21-27|a|https://media.giphy.com/a.gif,0-6|b|https://media.giphy.com/b.gif", text);

        Assert.Collection(gifs,
            gif => { Assert.Equal("b", gif.GifId); Assert.Equal("[b GIF]", text.Substring(gif.Start, gif.Length)); },
            gif => { Assert.Equal("a", gif.GifId); Assert.Equal("[a GIF]", text.Substring(gif.Start, gif.Length)); });
    }

    [Fact]
    public void MapsGifIndicesThroughSurrogatePairs()
    {
        const string text = "\U0001F600 [a GIF]";
        var gif = Assert.Single(IrcMessageParser.ParseGifs("2-8|a|https://media.giphy.com/a.gif", text));
        Assert.Equal("[a GIF]", text.Substring(gif.Start, gif.Length));
    }

    /// <summary>
    /// The address comes off the wire and is handed straight to a browser source and to WPF, so
    /// anything that is not an ordinary https URL is dropped rather than rendered.
    /// </summary>
    [Fact]
    public void IgnoresMalformedGifEntries()
    {
        const string text = "[a GIF]";
        Assert.Empty(IrcMessageParser.ParseGifs("0-6|a|javascript:alert(1)", text));
        Assert.Empty(IrcMessageParser.ParseGifs("0-6|a|file://C:/secret.gif", text));
        Assert.Empty(IrcMessageParser.ParseGifs("0-6|a", text));
        Assert.Empty(IrcMessageParser.ParseGifs("0-99|a|https://media.giphy.com/a.gif", text));
        Assert.Empty(IrcMessageParser.ParseGifs("x-y|a|https://media.giphy.com/a.gif", text));
        Assert.Empty(IrcMessageParser.ParseGifs(null, text));
    }

    /// <summary>
    /// The end of a range is read one past itself, so an end at the top of the int range used to
    /// wrap round to a negative index and throw – on the socket read loop, which took the whole
    /// connection down with it. Anything a stranger can put in a tag has to come back empty instead.
    /// </summary>
    [Theory]
    [InlineData("2147483647")]
    [InlineData("2147483646")]
    public void IgnoresRangesAtTheTopOfTheIntRange(string end)
    {
        const string text = "[a GIF]";
        Assert.Empty(IrcMessageParser.ParseGifs($"0-{end}|a|https://media.giphy.com/a.gif", text));
        Assert.Empty(IrcMessageParser.ParseEmotes($"25:0-{end}", text));
        Assert.Empty(IrcMessageParser.ParseGifs($"{end}-{end}|a|https://media.giphy.com/a.gif", text));
        Assert.Empty(IrcMessageParser.ParseEmotes($"25:{end}-{end}", text));
    }

    /// <summary>
    /// A GIF sent as a reply. The positions are counted against the text as Twitch sent it, so they
    /// have to move along with the "@name " the parser cuts off the front.
    /// </summary>
    [Fact]
    public void MovesGifPositionsWhenTheReplyMentionIsCut()
    {
        const string raw = "@gifs=7-13|a|https://media.giphy.com/a.gif;reply-parent-msg-id=p1;reply-parent-user-login=benji;"
                           + "reply-parent-display-name=Benji;reply-parent-user-id=1;reply-parent-msg-body=hej"
                           + " :fan!fan@fan.tmi.twitch.tv PRIVMSG #demo :@Benji [a GIF]";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out var message));
        Assert.Equal("[a GIF]", message!.Text);
        var gif = Assert.Single(message.Gifs);
        Assert.Equal("[a GIF]", message.Text.Substring(gif.Start, gif.Length));
    }

    [Fact]
    public void ParsesReplyTagsAndCutsTheRepeatedMention()
    {
        const string raw = "@display-name=lov3t;id=m9;reply-parent-display-name=adaaam1891;reply-parent-msg-body=han\\sska\\sbyta\\sname;"
                           + "reply-parent-msg-id=p1;reply-parent-user-id=99;reply-parent-user-login=adaaam1891"
                           + " :lov3t!lov3t@lov3t.tmi.twitch.tv PRIVMSG #demo :@adaaam1891 ah okej, tack för svar!";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out var message));
        Assert.Equal("ah okej, tack för svar!", message!.Text);
        Assert.NotNull(message.Reply);
        Assert.Equal("p1", message.Reply!.ParentMessageId);
        Assert.Equal("99", message.Reply.ParentUserId);
        Assert.Equal("adaaam1891", message.Reply.ParentLogin);
        Assert.Equal("adaaam1891", message.Reply.ParentDisplayName);
        Assert.Equal("han ska byta name", message.Reply.ParentText);
    }

    [Fact]
    public void MovesEmotesAlongWithTheCutReplyMention()
    {
        // The emote range is counted against the text Twitch sent, mention and all.
        const string raw = "@display-name=Benji;emotes=25:7-11;id=m10;reply-parent-display-name=Kajsa;reply-parent-msg-body=hej;"
                           + "reply-parent-msg-id=p2;reply-parent-user-login=kajsa"
                           + " :benji!benji@benji.tmi.twitch.tv PRIVMSG #demo :@Kajsa Kappa hej";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out var message));
        Assert.Equal("Kappa hej", message!.Text);
        var emote = Assert.Single(message.Emotes);
        Assert.Equal("Kappa", message.Text.Substring(emote.Start, emote.Length));
    }

    [Fact]
    public void KeepsAPlainMentionThatIsNotAReply()
    {
        const string raw = "@display-name=mickemal;id=m11 :mickemal!mickemal@mickemal.tmi.twitch.tv PRIVMSG #demo :@aerplejn_ Alo";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out var message));
        Assert.Null(message!.Reply);
        Assert.Equal("@aerplejn_ Alo", message.Text);
    }

    [Fact]
    public void CutsTheReplyMentionWhenTheSenderUsedTheLogin()
    {
        // Twitch writes whichever of the two the sending client used, so both have to be recognised.
        const string raw = "@display-name=Benji;id=m12;reply-parent-display-name=Kajsa_92;reply-parent-msg-body=hej;"
                           + "reply-parent-msg-id=p3;reply-parent-user-login=kajsa_92"
                           + " :benji!benji@benji.tmi.twitch.tv PRIVMSG #demo :@kajsa_92 !pet katt";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out var message));
        Assert.Equal("!pet katt", message!.Text);
    }

    [Fact]
    public void ParsesEmotesFromChatMessageLine()
    {
        const string raw = "@badges=;color=;display-name=Benji;emotes=25:0-4;id=abc;tmi-sent-ts=1700000000000 :benji!benji@benji.tmi.twitch.tv PRIVMSG #demo :Kappa hej";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out var message));
        var emote = Assert.Single(message!.Emotes);
        Assert.Equal("25", emote.EmoteId);
        Assert.Equal("Kappa", message.Text.Substring(emote.Start, emote.Length));
    }

    /// <summary>
    /// A refused message is answered with a NOTICE and nothing else – no USERSTATE ever follows – so
    /// this is the only sign that a line the reader typed did not reach the chat.
    /// </summary>
    [Fact]
    public void ReadsARefusedMessageOutOfItsNotice()
    {
        const string raw = "@msg-id=msg_slowmode :tmi.twitch.tv NOTICE #demo :This room is in slow mode. You may send another message in 3 seconds.";

        Assert.True(IrcMessageParser.TryParseSendRefusal(raw, out string? reason));
        // Twitch's own wording, whole: the seconds are the part worth reading.
        Assert.Equal("This room is in slow mode. You may send another message in 3 seconds.", reason);
    }

    /// <summary>
    /// The same NOTICE command carries things about the room that say nothing about our message.
    /// Treating one of those as a refusal would report a message that went through as rejected.
    /// </summary>
    [Fact]
    public void ANoticeAboutTheRoomIsNotARefusal()
    {
        Assert.False(IrcMessageParser.TryParseSendRefusal(
            "@msg-id=slow_on :tmi.twitch.tv NOTICE #demo :This room is now in slow mode.", out _));
        Assert.False(IrcMessageParser.TryParseSendRefusal(
            "@msg-id=emote_only_off :tmi.twitch.tv NOTICE #demo :This room is no longer in emote-only mode.", out _));
        // Nothing to tell the two apart by, so it cannot be claimed as one.
        Assert.False(IrcMessageParser.TryParseSendRefusal(":tmi.twitch.tv NOTICE #demo :Något hände.", out _));
    }

    /// <summary>A line beginning with a slash is a command, and an unknown one says nothing at all.</summary>
    [Fact]
    public void AnUnrecognisedCommandCountsAsNothingSaid()
    {
        Assert.True(IrcMessageParser.TryParseSendRefusal(
            "@msg-id=unrecognized_cmd :tmi.twitch.tv NOTICE #demo :Unrecognized command: /nonsense", out string? reason));
        Assert.Equal("Unrecognized command: /nonsense", reason);
    }
}
