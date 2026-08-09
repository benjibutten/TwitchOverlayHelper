using System.Net.Http;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Nicknames;
using TwitchOverlayHelper.Pets;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Twitch;
using TwitchOverlayHelper.Web;

namespace TwitchOverlayHelper.Tests;

/// <summary>
/// The timeline the hub keeps: what survives a channel being set, and what counts as a change worth
/// writing to disk. Both are about the restart – the saved history is put back before anything is
/// connected, and it has to still be there once the stream starts.
/// </summary>
public sealed class ChatHubHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 21, 0, 0, TimeSpan.Zero);

    /// <summary>Shared so the pets that ship with the app are written out once, not once per test.</summary>
    private static readonly string PetsFolder = Path.Combine(Path.GetTempPath(), "toh-tests-pets");

    private static ChatHub Hub(string channel)
    {
        var settings = new AppSettings { Channel = channel };
        settings.Normalize();
        return new ChatHub(settings, new TwitchBadgeCatalog(), new TwitchSession(new HttpClient()),
            new PetRegistry(), new PetCatalog(PetsFolder), new NicknameBook());
    }

    private static ChatMessage Message(string id, string text) =>
        new(id, "Kajsa", text, "#A970FF", [], false, false, Now) { UserId = "7", UserLogin = "kajsa" };

    [Fact]
    public void ConnectingToTheChannelTheHistoryCameFromKeepsIt()
    {
        ChatHub hub = Hub("kanalen");
        hub.ReplaceHistory([ChatTimelineItem.Of(Message("a", "hej"))]);

        // What the app does on the first connect after a restart: same room, same lines.
        hub.SetChannel("kanalen");

        ChatTimelineItem kept = Assert.Single(hub.SnapshotHistory());
        Assert.Equal("a", kept.Message?.Id);
    }

    [Fact]
    public void ConnectingToAnotherChannelDropsTheRestoredHistory()
    {
        ChatHub hub = Hub("kanalen");
        hub.ReplaceHistory([ChatTimelineItem.Of(Message("a", "hej"))]);

        hub.SetChannel("annan");

        Assert.Empty(hub.SnapshotHistory());
    }

    [Fact]
    public void SampleLinesAreNeverHandedOutForSaving()
    {
        ChatHub hub = Hub("kanalen");
        hub.ShowSamples();

        Assert.Empty(hub.SnapshotHistory());
    }

    [Fact]
    public void RestoredLinesSurviveTheFirstRealMessage()
    {
        ChatHub hub = Hub("kanalen");
        hub.ReplaceHistory([ChatTimelineItem.Of(Message("a", "hej"))]);

        hub.PublishMessage(Message("b", "och hej igen"));

        Assert.Equal(["a", "b"], hub.SnapshotHistory().Select(item => item.Message?.Id));
    }

    [Fact]
    public void SampleLinesGoWhenTheFirstRealMessageArrives()
    {
        ChatHub hub = Hub("kanalen");
        hub.ShowSamples();

        hub.PublishMessage(Message("b", "hej"));

        ChatTimelineItem only = Assert.Single(hub.SnapshotHistory());
        Assert.Equal("b", only.Message?.Id);
    }

    [Fact]
    public void MarkingAMessageCountsAsAChangeWorthSaving()
    {
        ChatHub hub = Hub("kanalen");
        hub.PublishMessage(Message("a", "hej"));
        long before = hub.HistoryVersion;

        // A Gigantify marker on a line already in the history. Nothing else will arrive in a quiet
        // chat, so this has to be what tells the saver the file is out of date.
        hub.PublishMessageUpdate(Message("a", "hej") with { GigantifiedEmoteId = "25" });

        Assert.NotEqual(before, hub.HistoryVersion);
        Assert.Equal("25", Assert.Single(hub.SnapshotHistory()).Message?.GigantifiedEmoteId);
    }

    [Fact]
    public void AMarkerForALineThatIsGoneChangesNothing()
    {
        ChatHub hub = Hub("kanalen");
        hub.PublishMessage(Message("a", "hej"));
        long before = hub.HistoryVersion;

        hub.PublishMessageUpdate(Message("borta", "hej") with { GigantifiedEmoteId = "25" });

        Assert.Equal(before, hub.HistoryVersion);
    }
}
