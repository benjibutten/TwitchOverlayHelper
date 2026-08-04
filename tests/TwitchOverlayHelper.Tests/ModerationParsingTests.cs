using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Tests;

public sealed class ModerationParsingTests
{
    private const string Soh = "";

    [Fact]
    public void ReadsUserIdAndLoginNeededForModeration()
    {
        const string raw = "@display-name=Benji;id=abc;user-id=12345;room-id=42 :benji!benji@benji.tmi.twitch.tv PRIVMSG #demo :Hej";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out ChatMessage? message));
        Assert.Equal("12345", message!.UserId);
        Assert.Equal("benji", message.UserLogin);
    }

    [Fact]
    public void FallsBackToLoginWhenDisplayNameIsMissing()
    {
        const string raw = "@id=abc;user-id=1 :someone!someone@someone.tmi.twitch.tv PRIVMSG #demo :Hej";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out ChatMessage? message));
        Assert.Equal("someone", message!.DisplayName);
    }

    [Fact]
    public void UnwrapsActionMessages()
    {
        string raw = $"@display-name=Benji;id=abc;user-id=1 :benji!benji@benji.tmi.twitch.tv PRIVMSG #demo :{Soh}ACTION vinkar{Soh}";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out ChatMessage? message));
        Assert.True(message!.IsAction);
        Assert.Equal("vinkar", message.Text);
    }

    [Fact]
    public void ReportsBroadcasterAndModeratorFromBadges()
    {
        const string raw = "@badges=moderator/1;display-name=Mod;id=abc;user-id=7 :mod!mod@mod.tmi.twitch.tv PRIVMSG #demo :Hej";

        Assert.True(IrcMessageParser.TryParseChatMessage(raw, out ChatMessage? message));
        Assert.True(message!.IsModerator);
        Assert.False(message.IsBroadcaster);
    }

    [Fact]
    public void ParsesTimeoutAsUserPurgeWithDuration()
    {
        const string raw = "@ban-duration=600;target-user-id=99;tmi-sent-ts=1700000000000 :tmi.twitch.tv CLEARCHAT #demo :spammer";

        Assert.True(IrcMessageParser.TryParseModerationEvent(raw, out ChatModerationEvent? moderation));
        Assert.Equal(ChatEventKind.UserPurged, moderation!.Kind);
        Assert.Equal("99", moderation.TargetUserId);
        Assert.Equal("spammer", moderation.TargetLogin);
        Assert.Equal(600, moderation.DurationSeconds);
    }

    [Fact]
    public void ParsesPermanentBanAsPurgeWithoutDuration()
    {
        const string raw = "@target-user-id=99;tmi-sent-ts=1700000000000 :tmi.twitch.tv CLEARCHAT #demo :spammer";

        Assert.True(IrcMessageParser.TryParseModerationEvent(raw, out ChatModerationEvent? moderation));
        Assert.Equal(ChatEventKind.UserPurged, moderation!.Kind);
        Assert.Null(moderation.DurationSeconds);
    }

    [Fact]
    public void ParsesWholeChatClearWhenNoUserIsNamed()
    {
        const string raw = "@room-id=42;tmi-sent-ts=1700000000000 :tmi.twitch.tv CLEARCHAT #demo";

        Assert.True(IrcMessageParser.TryParseModerationEvent(raw, out ChatModerationEvent? moderation));
        Assert.Equal(ChatEventKind.ChatCleared, moderation!.Kind);
    }

    [Fact]
    public void ParsesSingleMessageDeletion()
    {
        const string raw = "@login=spammer;target-msg-id=msg-1;tmi-sent-ts=1700000000000 :tmi.twitch.tv CLEARMSG #demo :fult";

        Assert.True(IrcMessageParser.TryParseModerationEvent(raw, out ChatModerationEvent? moderation));
        Assert.Equal(ChatEventKind.MessageDeleted, moderation!.Kind);
        Assert.Equal("msg-1", moderation.TargetMessageId);
        Assert.Equal("spammer", moderation.TargetLogin);
    }

    [Fact]
    public void IgnoresOrdinaryChatLines()
    {
        const string raw = "@display-name=Benji;id=abc :benji!benji@benji.tmi.twitch.tv PRIVMSG #demo :Hej";

        Assert.False(IrcMessageParser.TryParseModerationEvent(raw, out _));
    }
}
