using TwitchOverlayHelper.History;
using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Tests;

public sealed class ChatHistoryMergeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 21, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    private static ChatTimelineItem Message(string id, string text, int minutesAgo) =>
        ChatTimelineItem.Of(new ChatMessage(id, "Namn", text, null, [], false, false, Now.AddMinutes(-minutesAgo)));

    private static ChatTimelineItem Event(string id, int minutesAgo) =>
        ChatTimelineItem.Of(new ChatEvent(ChatEventType.Raid, id, "Pelle", Now.AddMinutes(-minutesAgo)));

    private static IReadOnlyList<ChatTimelineItem> Combine(
        IReadOnlyList<ChatTimelineItem> mine, IReadOnlyList<ChatTimelineItem> fetched, int limit = 200) =>
        ChatHistoryMerge.Combine(mine, fetched, limit, MaxAge, Now);

    [Fact]
    public void TheSameLineFromBothSourcesAppearsOnce()
    {
        IReadOnlyList<ChatTimelineItem> merged = Combine([Message("a", "hej", 5)], [Message("a", "hej", 5)]);

        Assert.Equal("a", Assert.Single(merged).Message?.Id);
    }

    /// <summary>
    /// Identity is asked for in two places now – here, and before the overlay is redrawn, to tell the
    /// lines still waiting to be drawn from the ones the redraw already covers. A message and an event
    /// that happen to share an id are two different lines, and if the answer let them collide the
    /// second place would quietly drop a sub notice because a message had the same id.
    /// </summary>
    [Fact]
    public void AMessageAndAnEventAreNeverTheSameLine()
    {
        Assert.NotEqual(ChatHistoryMerge.IdOf(Message("x", "hej", 5)), ChatHistoryMerge.IdOf(Event("x", 5)));
        Assert.Equal(ChatHistoryMerge.IdOf(Message("x", "hej", 5)), ChatHistoryMerge.IdOf(Message("x", "hej igen", 1)));
    }

    [Fact]
    public void OurOwnCopyWinsOverTheFetchedOne()
    {
        // Ours has been through reward and power-up enrichment; the raw feed's has not.
        ChatTimelineItem mine = ChatTimelineItem.Of(
            new ChatMessage("a", "Namn", "hej", null, [], false, false, Now.AddMinutes(-5)) { RewardTitle = "Spawna pet" });

        IReadOnlyList<ChatTimelineItem> merged = Combine([mine], [Message("a", "hej", 5)]);

        Assert.Equal("Spawna pet", Assert.Single(merged).Message?.RewardTitle);
    }

    [Fact]
    public void EverythingEndsUpInTimeOrder()
    {
        IReadOnlyList<ChatTimelineItem> merged = Combine(
            [Message("live-1", "nyss", 1)],
            [Message("gammal-1", "innan", 30), Event("raid", 20), Message("gammal-2", "innan också", 10)]);

        Assert.Equal(["gammal-1", "raid", "gammal-2", "live-1"], merged.Select(Id));
    }

    [Fact]
    public void FetchedLinesLandAboveLinesThatArrivedLive()
    {
        // The case this exists for: the app connected, a few lines came in, and only then did the
        // older ones arrive. Appending them would put yesterday underneath today.
        IReadOnlyList<ChatTimelineItem> merged = Combine(
            [Message("live", "precis nu", 0)],
            [Message("äldre", "för en stund sen", 15)]);

        Assert.Equal(["äldre", "live"], merged.Select(Id));
    }

    [Fact]
    public void LinesOlderThanTheWindowAreLeftOut()
    {
        IReadOnlyList<ChatTimelineItem> merged = Combine(
            [], [Message("igår", "gammalt", (int)MaxAge.TotalMinutes + 1), Message("färsk", "nytt", 5)]);

        Assert.Equal("färsk", Assert.Single(merged).Message?.Id);
    }

    [Fact]
    public void OnlyTheNewestLinesSurviveTheLimit()
    {
        List<ChatTimelineItem> fetched = Enumerable.Range(0, 10)
            .Select(i => Message($"m{i}", $"rad {i}", 100 - i))
            .ToList();

        IReadOnlyList<ChatTimelineItem> merged = Combine([], fetched, limit: 3);

        Assert.Equal(["m7", "m8", "m9"], merged.Select(Id));
    }

    [Fact]
    public void MessagesAndEventsWithTheSameIdAreNotConfusedForEachOther()
    {
        IReadOnlyList<ChatTimelineItem> merged = Combine([Message("delad", "text", 5)], [Event("delad", 6)]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void NothingFetchedLeavesOurOwnHistoryAlone()
    {
        IReadOnlyList<ChatTimelineItem> merged = Combine([Message("a", "hej", 5), Message("b", "då", 4)], []);

        Assert.Equal(["a", "b"], merged.Select(Id));
    }

    private static string Id(ChatTimelineItem item) => item.Message?.Id ?? item.Event!.Id;
}
