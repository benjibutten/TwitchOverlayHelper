using TwitchOverlayHelper.Settings;

namespace TwitchOverlayHelper.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void RoundTripsSettings()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings { Channel = "demo", FontSize = 28, BackgroundOpacity = 0.6 });
            AppSettings loaded = store.Load();
            Assert.Equal("demo", loaded.Channel);
            Assert.Equal(28, loaded.FontSize);
            Assert.Equal(0.6, loaded.BackgroundOpacity);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
