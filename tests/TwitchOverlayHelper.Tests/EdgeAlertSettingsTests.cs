using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Settings;

namespace TwitchOverlayHelper.Tests;

public sealed class EdgeAlertSettingsTests
{
    private static ChatMessage Message(string text, bool moderator = false, bool broadcaster = false, bool firstMessage = false)
    {
        var badges = new List<ChatBadge>();
        if (moderator) badges.Add(new ChatBadge("moderator", "1"));
        if (broadcaster) badges.Add(new ChatBadge("broadcaster", "1"));
        return new ChatMessage("id", "Namn", text, null, badges, firstMessage, false, DateTimeOffset.Now);
    }

    [Theory]
    [InlineData("!psst")]
    [InlineData("!PSST")]
    [InlineData("  !psst  ")]
    [InlineData("!psst kolla chatten")]
    public void ModCommandFromAModeratorLightsTheGlow(string text)
    {
        var settings = new EdgeAlertSettings();

        Assert.True(settings.TriggersModAlert(Message(text, moderator: true)));
    }

    [Fact]
    public void BroadcasterCanCallToo()
    {
        var settings = new EdgeAlertSettings();

        Assert.True(settings.TriggersModAlert(Message("!psst", broadcaster: true)));
    }

    /// <summary>A viewer writing the command must do nothing – the glow is the mods' line to the streamer.</summary>
    [Fact]
    public void ViewersCannotTriggerTheModAlert()
    {
        var settings = new EdgeAlertSettings();

        Assert.False(settings.TriggersModAlert(Message("!psst")));
    }

    [Theory]
    [InlineData("!psst!")]
    [InlineData("!psstx")]
    [InlineData("hej !psst")]
    public void OnlyTheCommandAtTheStartOfTheLineCounts(string text)
    {
        var settings = new EdgeAlertSettings();

        Assert.False(settings.TriggersModAlert(Message(text, moderator: true)));
    }

    [Fact]
    public void SwitchedOffAlertsStayDark()
    {
        var settings = new EdgeAlertSettings();
        settings.ModAlert.Enabled = false;
        settings.NewChatterAlert.Enabled = false;

        Assert.False(settings.TriggersModAlert(Message("!psst", moderator: true)));
        Assert.False(settings.TriggersNewChatterAlert(Message("hej", firstMessage: true)));
    }

    [Fact]
    public void FirstMessageLightsTheNewChatterGlow()
    {
        var settings = new EdgeAlertSettings();

        Assert.True(settings.TriggersNewChatterAlert(Message("hej", firstMessage: true)));
        Assert.False(settings.TriggersNewChatterAlert(Message("hej")));
    }

    [Theory]
    [InlineData(null, "!psst")]
    [InlineData("", "!psst")]
    [InlineData("   ", "!psst")]
    [InlineData("!", "!psst")]
    [InlineData("psst", "!psst")]
    [InlineData("  !hallå  ", "!hallå")]
    [InlineData("!hallå där", "!hallå")]
    public void CleanCommandAlwaysComesOutUsable(string? typed, string expected)
    {
        Assert.Equal(expected, EdgeAlertSettings.CleanCommand(typed));
    }

    [Fact]
    public void NormalizeRepairsBrokenValues()
    {
        var settings = new EdgeAlertSettings
        {
            ModAlert = new EdgeAlertStyle { Color = "inte en färg", Intensity = double.NaN, DurationSeconds = 900 },
            NewChatterAlert = null!,
            ModCommand = "  ",
            EdgeWidth = double.PositiveInfinity
        };

        settings.Normalize();

        Assert.Equal("#F59E0B", settings.ModAlert.Color);
        Assert.Equal(0.7, settings.ModAlert.Intensity);
        Assert.Equal(20, settings.ModAlert.DurationSeconds);
        Assert.NotNull(settings.NewChatterAlert);
        Assert.Equal("#5FD6C8", settings.NewChatterAlert.Color);
        Assert.Equal("!psst", settings.ModCommand);
        Assert.Equal(160, settings.EdgeWidth);
    }
}
