using TwitchOverlayHelper.History;
using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Tests;

public sealed class ChatHistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 21, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"chat-history-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private ChatHistoryStore Store() => new(_path);

    private static ChatMessage Message(string id, string text, DateTimeOffset at) =>
        new(id, "Namn", text, "#A970FF", [new ChatBadge("moderator", "1")], false, false, at,
            [new EmoteSpan("25", 0, 5)])
        {
            UserId = "12345",
            UserLogin = "namn",
            Bits = 100
        };

    [Fact]
    public void MessagesSurviveTheRoundTrip()
    {
        ChatMessage original = Message("a", "hej på dig", Now.AddMinutes(-5));
        Store().Save("kanalen", [ChatTimelineItem.Of(original)], Now);

        ChatTimelineItem restored = Assert.Single(Store().Load("kanalen", Now));

        Assert.NotNull(restored.Message);
        ChatMessage message = restored.Message;
        Assert.Equal(original.Id, message.Id);
        Assert.Equal(original.Text, message.Text);
        Assert.Equal(original.DisplayName, message.DisplayName);
        Assert.Equal(original.NameColor, message.NameColor);
        Assert.Equal(original.SentAt, message.SentAt);
        Assert.Equal(original.UserId, message.UserId);
        Assert.Equal(original.UserLogin, message.UserLogin);
        Assert.Equal(original.Bits, message.Bits);
        // The badges are what the "!psst" check reads, so losing them here would switch the edge
        // glow off for every restored moderator without anything looking broken.
        Assert.Equal("moderator", Assert.Single(message.Badges).SetId);
        Assert.Equal("25", Assert.Single(message.Emotes).EmoteId);
    }

    [Fact]
    public void EventsSurviveTheRoundTripToo()
    {
        var original = new ChatEvent(ChatEventType.Subscription, "sub-1", "Kajsa", Now.AddMinutes(-2))
        {
            UserLogin = "kajsa",
            Months = 8,
            Tier = "1000",
            Message = "tack!"
        };
        Store().Save("kanalen", [ChatTimelineItem.Of(original)], Now);

        ChatTimelineItem restored = Assert.Single(Store().Load("kanalen", Now));

        Assert.NotNull(restored.Event);
        ChatEvent chatEvent = restored.Event;
        Assert.Equal(ChatEventType.Subscription, chatEvent.Type);
        Assert.Equal("Kajsa", chatEvent.DisplayName);
        Assert.Equal(8, chatEvent.Months);
        Assert.Equal("tack!", chatEvent.Message);
    }

    [Fact]
    public void MessagesAndEventsKeepTheirOrder()
    {
        Store().Save("kanalen",
        [
            ChatTimelineItem.Of(Message("a", "först", Now.AddMinutes(-3))),
            ChatTimelineItem.Of(new ChatEvent(ChatEventType.Raid, "raid", "Pelle", Now.AddMinutes(-2))),
            ChatTimelineItem.Of(Message("b", "sist", Now.AddMinutes(-1)))
        ], Now);

        IReadOnlyList<ChatTimelineItem> restored = Store().Load("kanalen", Now);

        Assert.Equal(3, restored.Count);
        Assert.Equal("a", restored[0].Message?.Id);
        Assert.Equal("raid", restored[1].Event?.Id);
        Assert.Equal("b", restored[2].Message?.Id);
    }

    [Fact]
    public void LinesOlderThanTheWindowAreLeftBehind()
    {
        Store().Save("kanalen",
        [
            ChatTimelineItem.Of(Message("gammal", "igår", Now - ChatHistoryStore.MaxAge.Add(TimeSpan.FromMinutes(1)))),
            ChatTimelineItem.Of(Message("färsk", "nyss", Now.AddMinutes(-1)))
        ], Now);

        ChatTimelineItem restored = Assert.Single(Store().Load("kanalen", Now));

        Assert.Equal("färsk", restored.Message?.Id);
    }

    [Fact]
    public void AnotherChannelsChatIsNeverRestored()
    {
        Store().Save("kanalen", [ChatTimelineItem.Of(Message("a", "hej", Now))], Now);

        Assert.Empty(Store().Load("en-annan-kanal", Now));
    }

    [Fact]
    public void TheChannelIsMatchedWithoutCase()
    {
        Store().Save("Kanalen", [ChatTimelineItem.Of(Message("a", "hej", Now))], Now);

        Assert.Single(Store().Load("kanalen", Now));
    }

    [Fact]
    public void NothingSavedMeansNothingRestored()
    {
        Assert.Empty(Store().Load("kanalen", Now));
    }

    [Fact]
    public void AnEmptyChannelRestoresNothing()
    {
        Store().Save("kanalen", [ChatTimelineItem.Of(Message("a", "hej", Now))], Now);

        Assert.Empty(Store().Load("   ", Now));
    }

    [Fact]
    public void AHalfWrittenFileIsIgnoredRatherThanThrown()
    {
        File.WriteAllText(_path, "{ det här är inte json");

        Assert.Empty(Store().Load("kanalen", Now));
    }

    [Fact]
    public void OnlyTheNewestLinesAreKept()
    {
        List<ChatTimelineItem> many = Enumerable.Range(0, ChatHistoryStore.MaxItems + 50)
            .Select(i => ChatTimelineItem.Of(Message($"m{i}", $"rad {i}", Now.AddSeconds(-1))))
            .ToList();

        Store().Save("kanalen", many, Now);
        IReadOnlyList<ChatTimelineItem> restored = Store().Load("kanalen", Now);

        Assert.Equal(ChatHistoryStore.MaxItems, restored.Count);
        Assert.Equal("m50", restored[0].Message?.Id);
    }

    [Fact]
    public void ClearingLeavesNothingToRestore()
    {
        ChatHistoryStore store = Store();
        store.Save("kanalen", [ChatTimelineItem.Of(Message("a", "hej", Now))], Now);

        store.Clear();

        Assert.Empty(store.Load("kanalen", Now));
    }

    [Fact]
    public void AWriteThatWorkedSaysSo()
    {
        Assert.True(Store().Save("kanalen", [ChatTimelineItem.Of(Message("a", "hej", Now))], Now));
    }

    /// <summary>
    /// A write that could not happen has to say so, or the caller marks the history as saved and
    /// never comes back to it – the file then stays a version behind until the next line arrives.
    /// </summary>
    [Fact]
    public void AWriteThatFailedSaysSo()
    {
        // A folder where the file should be: everything about the write is right except that the
        // path cannot hold a file, which is what a locked or unwritable target looks like from here.
        string blocked = Path.Combine(Path.GetTempPath(), $"chat-history-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(blocked);
        try
        {
            Assert.False(new ChatHistoryStore(blocked).Save("kanalen", [ChatTimelineItem.Of(Message("a", "hej", Now))], Now));
        }
        finally
        {
            Directory.Delete(blocked, true);
        }
    }
}
