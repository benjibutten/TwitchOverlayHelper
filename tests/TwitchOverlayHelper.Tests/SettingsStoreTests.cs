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

/// <summary>
/// The part of the settings file that is about not losing it: the copies beside it, the unreadable
/// file being moved aside instead of overwritten, and the version stamp that lets a later build
/// migrate rather than quietly reset.
/// </summary>
public sealed class SettingsDurabilityTests
{
    [Fact]
    public void RecoversFromACopyWhenTheFileIsTruncated()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "settings.json");
        var store = new SettingsStore(path);
        store.Save(new AppSettings { Channel = "demo", FontSize = 28 });

        // What a power cut mid-write used to leave behind – and what used to cost every setting.
        File.WriteAllText(path, """{"Channel":"de""");
        AppSettings loaded = new SettingsStore(path).Load();

        Assert.Equal("demo", loaded.Channel);
        Assert.Equal(28, loaded.FontSize);
    }

    [Fact]
    public void MovesAnUnreadableFileAsideInsteadOfOverwritingIt()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(path, "{inte-json");

        var store = new SettingsStore(path);
        store.Load();

        Assert.NotNull(store.LastLoad);
        string? quarantined = store.LastLoad!.QuarantinedPath;
        Assert.NotNull(quarantined);
        Assert.Equal("{inte-json", File.ReadAllText(quarantined!));
        // And the save that follows the failed load has nothing left to destroy.
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CarriesSettingsFromANewerVersionThroughUntouched()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(path, """
            {
              "SchemaVersion": 99,
              "Channel": "demo",
              "NagotVikommerFinnaPa": "viktigt",
              "Tts": { "Enabled": true, "NyInstallning": 7 }
            }
            """);

        var store = new SettingsStore(path);
        AppSettings loaded = store.Load();
        store.Save(loaded);

        string written = File.ReadAllText(path);
        Assert.Contains("NagotVikommerFinnaPa", written);
        Assert.Contains("NyInstallning", written);
        // Nothing is migrated backwards, so the stamp stays where the newer build put it.
        Assert.True(store.LastLoad!.Migration.FromFuture);
        Assert.Contains("\"SchemaVersion\": 99", written);
    }

    [Fact]
    public void StampsAnUnversionedFileWithoutChangingWhatItSaid()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(path, """{"Channel":"demo","FontSize":28}""");

        var store = new SettingsStore(path);
        AppSettings loaded = store.Load();

        Assert.Equal(0, store.LastLoad!.Migration.From);
        Assert.Equal(SettingsMigrations.CurrentVersion, loaded.SchemaVersion);
        Assert.Equal("demo", loaded.Channel);
        Assert.Equal(28, loaded.FontSize);
    }

    [Fact]
    public void SaysWhenItStartedWithNothing()
    {
        using var folder = new TempFolder();
        var store = new SettingsStore(Path.Combine(folder.Path, "settings.json"));

        store.Load();

        Assert.True(store.LastLoad!.StartedFresh);
        Assert.Null(store.LastLoad.QuarantinedPath);
    }

    [Fact]
    public void KeepsACopyOfEverySave()
    {
        using var folder = new TempFolder();
        var store = new SettingsStore(Path.Combine(folder.Path, "settings.json"), backupInterval: TimeSpan.Zero);

        store.Save(new AppSettings { Channel = "ett" });
        store.Save(new AppSettings { Channel = "tva" });

        Assert.Equal(2, Directory.GetFiles(store.BackupFolder, "settings-*.json").Length);
    }

    // A slider being dragged saves every 450 ms. Twenty copies of the same minute would leave the
    // folder unable to answer the only question anyone asks it: what did this look like before.
    [Fact]
    public void DoesNotSpendTheWholeHistoryOnOneSitting()
    {
        using var folder = new TempFolder();
        var store = new SettingsStore(Path.Combine(folder.Path, "settings.json"), backupInterval: TimeSpan.FromMinutes(5));

        for (int i = 0; i < 20; i++) store.Save(new AppSettings { FontSize = 20 + i * 0.1 });

        Assert.Single(Directory.GetFiles(store.BackupFolder, "settings-*.json"));
    }
}
