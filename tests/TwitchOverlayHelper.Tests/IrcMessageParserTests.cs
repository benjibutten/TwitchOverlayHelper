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

    [Theory]
    [InlineData("#Some_Channel", "some_channel")]
    [InlineData("https://www.twitch.tv/Twitch", "twitch")]
    [InlineData(" twitch ", "twitch")]
    public void NormalizesChannelInput(string input, string expected) =>
        Assert.Equal(expected, TwitchChatClient.NormalizeChannel(input));
}
