using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Tests;

/// <summary>
/// Badges have two lifetimes in one catalogue. Getting them mixed up is what once put the previous
/// streamer's subscriber icon next to a stranger's name in the next channel.
/// </summary>
public sealed class BadgeCatalogTests
{
    private static string Response(string setId, string version, string url, string title = "Badge") => $$"""
        {"data":[{"set_id":"{{setId}}","versions":[{"id":"{{version}}","image_url_2x":"{{url}}","title":"{{title}}"}]}]}
        """;

    private static TwitchBadgeCatalog Catalog()
    {
        var catalog = new TwitchBadgeCatalog();
        catalog.Add(Response("subscriber", "0", "https://cdn/global-sub.png"), channelOwned: false);
        catalog.Add(Response("moderator", "1", "https://cdn/global-mod.png"), channelOwned: false);
        catalog.Add(Response("subscriber", "6", "https://cdn/streamer-a-6.png", "6 månader"), channelOwned: true);
        return catalog;
    }

    [Fact]
    public void ReadsTheImageAndTitleOffAHelixResponse()
    {
        Assert.True(Catalog().TryGet("subscriber", "6", out BadgeInfo? badge));

        Assert.Equal("https://cdn/streamer-a-6.png", badge!.ImageUrl);
        Assert.Equal("6 månader", badge.Title);
    }

    // The bug, in one test: leaving a channel has to take its subscriber tiers with it, whether or
    // not a replacement set can be fetched afterwards.
    [Fact]
    public void ForgetsTheChannelsOwnBadgesWhenTheChannelChanges()
    {
        TwitchBadgeCatalog catalog = Catalog();

        catalog.ForgetChannel();

        Assert.False(catalog.TryGet("subscriber", "6", out _));
    }

    // A global badge looks the same in every chat on Twitch, so dropping it too would cost the
    // reader mod and staff icons for no reason at all.
    [Fact]
    public void KeepsTheGlobalBadgesAcrossAChannelChange()
    {
        TwitchBadgeCatalog catalog = Catalog();

        catalog.ForgetChannel();

        Assert.True(catalog.TryGet("moderator", "1", out BadgeInfo? badge));
        Assert.Equal("https://cdn/global-mod.png", badge!.ImageUrl);
    }

    // Twitch lets a streamer override a set that also exists globally; theirs is the one viewers see.
    [Fact]
    public void PrefersTheChannelsOwnVersionOverTheGlobalOne()
    {
        var catalog = new TwitchBadgeCatalog();
        catalog.Add(Response("subscriber", "0", "https://cdn/global-sub.png"), channelOwned: false);
        catalog.Add(Response("subscriber", "0", "https://cdn/streamer-a-0.png"), channelOwned: true);

        Assert.True(catalog.TryGet("subscriber", "0", out BadgeInfo? badge));
        Assert.Equal("https://cdn/streamer-a-0.png", badge!.ImageUrl);
    }

    // And once the channel is gone, the global one has to come back through rather than the lookup
    // finding nothing – the reader should see the generic sub badge, not a bare word.
    [Fact]
    public void FallsBackToTheGlobalVersionOnceTheChannelIsForgotten()
    {
        var catalog = new TwitchBadgeCatalog();
        catalog.Add(Response("subscriber", "0", "https://cdn/global-sub.png"), channelOwned: false);
        catalog.Add(Response("subscriber", "0", "https://cdn/streamer-a-0.png"), channelOwned: true);

        catalog.ForgetChannel();

        Assert.True(catalog.TryGet("subscriber", "0", out BadgeInfo? badge));
        Assert.Equal("https://cdn/global-sub.png", badge!.ImageUrl);
    }

    [Fact]
    public void FindsNothingForABadgeItHasNeverSeen()
    {
        Assert.False(Catalog().TryGet("vip", "1", out _));
    }
}
