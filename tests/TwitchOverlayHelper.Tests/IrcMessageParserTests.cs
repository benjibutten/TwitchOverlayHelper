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

    [Fact]
    public void ParsesEmotesFromChatMessageLine()
    {
        const string raw = "@badges=;color=;display-name=Benji;emotes=25:0-4;id=abc;tmi-sent-ts=1700000000000 :benji!benji@benji.tmi.twitch.tv PRIVMSG #demo :Kappa hej";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out var message));
        var emote = Assert.Single(message!.Emotes);
        Assert.Equal("25", emote.EmoteId);
        Assert.Equal("Kappa", message.Text.Substring(emote.Start, emote.Length));
    }
}
