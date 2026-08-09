using System.Text.Json;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Tests;

/// <summary>
/// The service answers with raw IRC lines, which is the whole reason it fits: these are the same
/// shapes the live connection sends, so the app's own parser reads them.
/// </summary>
public sealed class RecentMessagesClientTests
{
    private static string Body(params string[] lines) =>
        JsonSerializer.Serialize(new { messages = lines, error = (string?)null });

    [Fact]
    public void ReadsAChatLineWithItsTags()
    {
        string line = "@badge-info=;badges=moderator/1;color=#A970FF;display-name=Benjiboy;" +
            "id=abc-123;tmi-sent-ts=1754769600000;user-id=555 " +
            ":benjiboy!benjiboy@benjiboy.tmi.twitch.tv PRIVMSG #kanalen :hej allihop";

        ChatTimelineItem item = Assert.Single(RecentMessagesClient.Parse(Body(line)));

        Assert.NotNull(item.Message);
        ChatMessage message = item.Message;
        Assert.Equal("abc-123", message.Id);
        Assert.Equal("Benjiboy", message.DisplayName);
        Assert.Equal("hej allihop", message.Text);
        Assert.Equal("#A970FF", message.NameColor);
        Assert.Equal("555", message.UserId);
        // The badge is what decides whether a restored line counts as a moderator's.
        Assert.True(message.IsModerator);
    }

    [Fact]
    public void ReadsAUserNoticeAsAnEventCard()
    {
        string line = "@badges=;display-name=Kajsa;id=sub-1;msg-id=resub;msg-param-cumulative-months=8;" +
            "msg-param-sub-plan=1000;tmi-sent-ts=1754769600000;login=kajsa " +
            ":tmi.twitch.tv USERNOTICE #kanalen :tack för allt";

        ChatTimelineItem item = Assert.Single(RecentMessagesClient.Parse(Body(line)));

        Assert.NotNull(item.Event);
        Assert.Equal(ChatEventType.Subscription, item.Event.Type);
        Assert.Equal("Kajsa", item.Event.DisplayName);
    }

    [Fact]
    public void KeepsTheOrderTheServiceSentThemIn()
    {
        string First = Line("id-1", "först", 1754769600000);
        string Second = Line("id-2", "sen", 1754769601000);

        IReadOnlyList<ChatTimelineItem> items = RecentMessagesClient.Parse(Body(First, Second));

        Assert.Equal(2, items.Count);
        Assert.Equal("id-1", items[0].Message?.Id);
        Assert.Equal("id-2", items[1].Message?.Id);
    }

    [Fact]
    public void LinesTheParserDoesNotRecogniseAreSkippedRatherThanThrown()
    {
        IReadOnlyList<ChatTimelineItem> items = RecentMessagesClient.Parse(
            Body(":tmi.twitch.tv ROOMSTATE #kanalen", "skräp", Line("id-1", "hej", 1754769600000)));

        Assert.Equal("id-1", Assert.Single(items).Message?.Id);
    }

    /// <summary>
    /// The service rebuilds its stored lines without the ":" before the text when the message is a
    /// single word – IRC only requires the colon when the trailing part contains a space. Roughly
    /// one line in eight of a busy channel's history comes back this way, so reading only the
    /// colon form dropped them all without a trace.
    /// </summary>
    [Fact]
    public void ReadsALineWhoseTextHasNoLeadingColon()
    {
        string line = "@badges=;display-name=M1LLER;id=no-colon;tmi-sent-ts=1754769600000;user-id=7 " +
            ":m1ller!m1ller@m1ller.tmi.twitch.tv PRIVMSG #kanalen <3";

        ChatTimelineItem item = Assert.Single(RecentMessagesClient.Parse(Body(line)));

        Assert.Equal("<3", item.Message?.Text);
        Assert.Equal("no-colon", item.Message?.Id);
    }

    [Fact]
    public void ALineWithNoTextAtAllIsSkipped()
    {
        string line = "@badges=;display-name=Namn;id=tom;tmi-sent-ts=1754769600000 " +
            ":namn!namn@namn.tmi.twitch.tv PRIVMSG #kanalen";

        Assert.Empty(RecentMessagesClient.Parse(Body(line)));
    }

    [Fact]
    public void AnErrorAnswerIsAnEmptyChatRatherThanACrash()
    {
        Assert.Empty(RecentMessagesClient.Parse("""{"messages":null,"error":"channel ignored","error_code":"channel_ignored"}"""));
        Assert.Empty(RecentMessagesClient.Parse("inte json alls"));
        Assert.Empty(RecentMessagesClient.Parse("{}"));
    }

    private static string Line(string id, string text, long sentAt) =>
        $"@badges=;display-name=Namn;id={id};tmi-sent-ts={sentAt};user-id=1 " +
        $":namn!namn@namn.tmi.twitch.tv PRIVMSG #kanalen :{text}";
}
