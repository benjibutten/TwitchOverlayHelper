using TwitchOverlayHelper.Settings;

namespace TwitchOverlayHelper.Tests;

public sealed class StreamSettingsTests
{
    [Fact]
    public void ClampsWhatASavedFileCouldHold()
    {
        var stream = new StreamSettings
        {
            FontSize = 900,
            LineHeight = double.NaN,
            MaxMessages = 100000,
            FadeAfterSeconds = -5,
            MessageBackgroundOpacity = 4,
            FontFamily = "   "
        };
        stream.Normalize();

        Assert.Equal(64, stream.FontSize);
        Assert.Equal(1.35, stream.LineHeight);
        Assert.Equal(60, stream.MaxMessages);
        Assert.Equal(0, stream.FadeAfterSeconds);
        Assert.Equal(0.9, stream.MessageBackgroundOpacity);
        Assert.Equal("Verdana", stream.FontFamily);
    }

    /// <summary>
    /// The stream overlay's event switches are its own. Turning a card off for the viewers is not the
    /// same decision as turning it off in the column the streamer reads.
    /// </summary>
    [Fact]
    public void EventSwitchesAreSeparateFromTheDocksAndTheOverlays()
    {
        var settings = new AppSettings();
        settings.Normalize();

        settings.Stream.Events.Raids = false;

        Assert.True(settings.Dock.Events.Raids);
        Assert.True(settings.Events.Raids);
        Assert.False(settings.Stream.Events.Raids);
    }

    [Fact]
    public void SurvivesASettingsFileWrittenBeforeTheStreamOverlayExisted()
    {
        var settings = new AppSettings { Stream = null! };
        settings.Normalize();

        Assert.NotNull(settings.Stream);
        Assert.Equal(26, settings.Stream.FontSize);
    }
}
