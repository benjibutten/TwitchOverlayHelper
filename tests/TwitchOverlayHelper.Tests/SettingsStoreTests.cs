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

    [Fact]
    public void LoadNormalizesOutOfRangeSettings()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "settings.json");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, """{"FontSize":999,"LineSpacing":0,"MaxMessages":-4,"OverlayWidth":12}""");

            AppSettings loaded = new SettingsStore(path).Load();

            Assert.Equal(36, loaded.FontSize);
            Assert.Equal(1.15, loaded.LineSpacing);
            Assert.Equal(1, loaded.MaxMessages);
            Assert.Equal(320, loaded.OverlayWidth);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void LoadFallsBackWhenSettingsAreMalformed()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "settings.json");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, "{inte-json");

            AppSettings loaded = new SettingsStore(path).Load();

            Assert.Equal(22, loaded.FontSize);
            Assert.Equal(18, loaded.MaxMessages);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
