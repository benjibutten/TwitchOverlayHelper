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

    // ------------------------------------------------------------- what an action reaches
    //
    // One rule, asked by everything that holds chat: the timeline the browser pages replay from, the
    // file on disk and the overlay's own cards. A line one of them still shows after a ban is a line
    // the moderator believes is gone.

    private static ChatMessage From(string id, string login, string userId) =>
        new(id, login, "hej", null, [], false, false, DateTimeOffset.Now) { UserId = userId, UserLogin = login };

    [Fact]
    public void ClearingTheRoomReachesEveryLine()
    {
        var cleared = new ChatModerationEvent(ChatEventKind.ChatCleared, null, null, null, null, DateTimeOffset.Now);

        Assert.True(cleared.Affects(From("1", "kajsa", "7")));
        Assert.True(cleared.Affects(From("2", "pelle", "8")));
    }

    [Fact]
    public void ADeletionReachesOnlyTheMessageItNames()
    {
        var deleted = new ChatModerationEvent(ChatEventKind.MessageDeleted, "msg-1", null, "kajsa", null, DateTimeOffset.Now);

        Assert.True(deleted.Affects(From("msg-1", "kajsa", "7")));
        // The same chatter's other lines stay: one message was deleted, not the person.
        Assert.False(deleted.Affects(From("msg-2", "kajsa", "7")));
    }

    // The id is what a ban is really about – a display name can be changed, and a login reused.
    [Fact]
    public void APurgeReachesEverythingFromThatChatter()
    {
        var purged = new ChatModerationEvent(ChatEventKind.UserPurged, null, "7", "kajsa", 600, DateTimeOffset.Now);

        Assert.True(purged.Affects(From("1", "kajsa", "7")));
        Assert.True(purged.Affects(From("2", "kajsa", "7")));
        Assert.False(purged.Affects(From("3", "pelle", "8")));
    }

    // IRC names the purged user in lower case; our own echo carries the login as the account writes
    // it. Matching case-sensitively there would leave the banned chatter's lines on screen.
    [Fact]
    public void APurgeReachesTheChatterWhateverTheCaseOfTheirLogin()
    {
        var purged = new ChatModerationEvent(ChatEventKind.UserPurged, null, null, "kajsa", 600, DateTimeOffset.Now);

        Assert.True(purged.Affects(From("1", "Kajsa", "7")));
    }
}
