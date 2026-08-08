using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Web;

namespace TwitchOverlayHelper.Tests;

public sealed class ChatEventVisibilityTests
{
    private static readonly ChatEventType[] AllTypes = Enum.GetValues<ChatEventType>();

    [Fact]
    public void ShowsEverythingUntilSomethingIsSwitchedOff()
    {
        var visibility = new ChatEventVisibility();

        Assert.All(AllTypes, type => Assert.True(visibility.Allows(type)));
    }

    /// <summary>
    /// Every type has to answer to a switch somebody can find. A new msg-id that fell through the
    /// groups would land in a card nothing could turn off, so the mapping is checked as a whole
    /// rather than group by group.
    /// </summary>
    [Fact]
    public void EveryTypeIsCoveredByTheSwitchForItsGroup()
    {
        foreach (ChatEventType type in AllTypes)
        {
            string group = ChatEventVisibility.Group(type);
            var visibility = new ChatEventVisibility();
            Switch(visibility, group, false);

            Assert.False(visibility.Allows(type), $"{type} is not switched off by its group {group}");
        }
    }

    /// <summary>Switching one group off must not quietly take another one with it.</summary>
    [Fact]
    public void SwitchingOneGroupOffLeavesTheOthersAlone()
    {
        var visibility = new ChatEventVisibility { Subs = false };

        Assert.False(visibility.Allows(ChatEventType.Subscription));
        Assert.False(visibility.Allows(ChatEventType.CommunityGift));
        Assert.True(visibility.Allows(ChatEventType.Raid));
        Assert.True(visibility.Allows(ChatEventType.RewardRedemption));
    }

    /// <summary>
    /// The dock filters on the group name it is handed with each event. If the mapper ever sent
    /// something else, every card of that kind would fall through to the "other" switch instead.
    /// </summary>
    [Fact]
    public void TheDockIsToldTheSameGroupTheOverlayFiltersOn()
    {
        foreach (ChatEventType type in AllTypes)
        {
            DockEvent wire = DockMapper.ToDock(new ChatEvent(type, "e1", "Kajsa", DateTimeOffset.Now));

            Assert.Equal(ChatEventVisibility.Group(type), wire.Group);
        }
    }

    /// <summary>Reading a settings file written before these switches existed must not hide anything.</summary>
    [Fact]
    public void SettingsMissingTheListFallBackToShowingEverything()
    {
        var settings = new AppSettings { Events = null!, Dock = new DockSettings { Events = null! } };

        settings.Normalize();

        Assert.All(AllTypes, type => Assert.True(settings.Events.Allows(type)));
        Assert.All(AllTypes, type => Assert.True(settings.Dock.Events.Allows(type)));
    }

    /// <summary>
    /// The dock looks its switches up by the group name, so the names on the wire have to be the
    /// same ones the mapper sends. Nothing here would fail to compile if they drifted apart.
    /// </summary>
    [Fact]
    public void TheSwitchesTravelUnderTheNamesTheGroupsAreCalled()
    {
        var settings = new DockSettings { Events = new ChatEventVisibility { Raids = false } };

        string json = DockJson.Serialize(settings);

        foreach (ChatEventType type in AllTypes)
            Assert.Contains($"\"{ChatEventVisibility.Group(type)}\":", json, StringComparison.Ordinal);
        Assert.Contains("\"raids\":false", json, StringComparison.Ordinal);
    }

    private static void Switch(ChatEventVisibility visibility, string group, bool on)
    {
        switch (group)
        {
            case "subs": visibility.Subs = on; break;
            case "raids": visibility.Raids = on; break;
            case "announcements": visibility.Announcements = on; break;
            case "bits": visibility.Bits = on; break;
            case "milestones": visibility.Milestones = on; break;
            case "rewards": visibility.Rewards = on; break;
            case "shoutouts": visibility.Shoutouts = on; break;
            case "hypeTrain": visibility.HypeTrain = on; break;
            case "other": visibility.Other = on; break;
            default: Assert.Fail($"Unknown group {group}"); break;
        }
    }
}
