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
    public void TheSpinWinLinesGetTheRarityWordWithoutTakingOverAWrittenOne()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "settings.json");
        // A file from the build before the rarity existed: the win line still reads word for word as
        // that build shipped it, and the duplicate line is one the streamer has made their own.
        File.WriteAllText(path, """
            {
              "SchemaVersion": 2,
              "Bot": {
                "Messages": [
                  { "Flow": "SpinWin", "Enabled": true, "Template": "🎉 @{viewer} vann {prize} i lyckosnurren!" },
                  { "Flow": "SpinDuplicate", "Enabled": true, "Template": "@{viewer} dubblett! {prize} igen." }
                ]
              }
            }
            """);

        AppSettings loaded = new SettingsStore(path).Load();

        Assert.Contains("{rarity}", loaded.Bot.Rule(BotFlow.SpinWin).Template);
        Assert.Equal("@{viewer} dubblett! {prize} igen.", loaded.Bot.Rule(BotFlow.SpinDuplicate).Template);
    }

    // A flow an old file never held at all: the migration has nothing to reword, and normalisation
    // is what has to put this build's wording there instead.
    [Fact]
    public void AFlowMissingFromAnOldFileArrivesOnThisBuildsWording()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(path, """{"SchemaVersion":2,"Bot":{"Messages":[]}}""");

        AppSettings loaded = new SettingsStore(path).Load();

        Assert.Equal(
            BotSettings.Defaults.First(rule => rule.Flow == BotFlow.SpinWin).Template,
            loaded.Bot.Rule(BotFlow.SpinWin).Template);
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

/// <summary>
/// Reading a settings file written by a version this one is not. Everything here is about the same
/// promise: one thing this build does not recognise must never cost the user the rest of the file.
/// </summary>
public sealed class SettingsForwardCompatibilityTests
{
    [Fact]
    public void AnUnknownBotModeDoesNotTakeTheWholeFileWithIt()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "settings.json");
        // What a later version with a third way of speaking would leave behind.
        File.WriteAllText(path, """
            {
              "Channel": "demo",
              "FontSize": 28,
              "Bot": { "Mode": "Bada", "PetWord": "kompis" }
            }
            """);

        var store = new SettingsStore(path);
        AppSettings loaded = store.Load();

        // The mode itself cannot be honoured, and silence is the safe reading of "somebody else
        // should be speaking as you".
        Assert.Equal(BotMode.Off, loaded.Bot.Mode);
        // But everything around it survived, which is the entire point.
        Assert.Equal("demo", loaded.Channel);
        Assert.Equal(28, loaded.FontSize);
        Assert.Equal("kompis", loaded.Bot.PetWord);
        Assert.Null(store.LastLoad!.QuarantinedPath);
    }

    [Fact]
    public void KeepsUnknownFieldsInsideANestedSettingsGroup()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(path, """
            {
              "Events": { "Subs": false, "Kometer": true },
              "Dock": { "NyGrej": 3 }
            }
            """);

        var store = new SettingsStore(path);
        AppSettings loaded = store.Load();
        store.Save(loaded);

        string written = File.ReadAllText(path);
        Assert.Contains("Kometer", written);
        Assert.Contains("NyGrej", written);
        Assert.False(loaded.Events.Subs);
    }

    // The overlay redraws every card when its copy of these stops matching the live ones. A group
    // from a newer version is not a reason to redraw anything.
    [Fact]
    public void UnknownGroupsDoNotCountAsAChangeToWhatIsShown()
    {
        var mine = new ChatEventVisibility { Subs = false };
        var theirs = new ChatEventVisibility
        {
            Subs = false,
            Unknown = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["Kometer"] = System.Text.Json.JsonDocument.Parse("true").RootElement
            }
        };

        Assert.Equal(mine, theirs);
        Assert.Equal(mine.GetHashCode(), theirs.GetHashCode());
        Assert.NotEqual(mine, theirs with { Raids = false });
    }
}

/// <summary>
/// What the app is told about the load, which is what decides whether the profile gets a snapshot
/// and whether the user hears about any of it.
/// </summary>
public sealed class SettingsLoadReportTests
{
    [Fact]
    public void AnEmptyFileIsMovedAsideRatherThanPassedOverInSilence()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "settings.json");
        // Zero bytes is what a crash between opening and flushing leaves behind.
        File.WriteAllText(path, string.Empty);

        var store = new SettingsStore(path);
        store.Load();

        Assert.NotNull(store.LastLoad!.QuarantinedPath);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AnUnreadableFileIsNotAFirstRun()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(path, "{inte-json");

        var store = new SettingsStore(path);
        store.Load();

        // The difference decides whether the profile is worth copying aside. Reading "could not
        // parse" as "nothing was there" is how the start that most needs a snapshot skips it.
        Assert.False(store.LastLoad!.StartedFresh);
    }

    [Fact]
    public void AProfileWithOnlyBackupsLeftIsNotAFirstRunEither()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "settings.json");
        var store = new SettingsStore(path, backupInterval: TimeSpan.Zero);
        store.Save(new AppSettings { Channel = "demo" });
        File.Delete(path);

        var reopened = new SettingsStore(path);
        AppSettings loaded = reopened.Load();

        Assert.Equal("demo", loaded.Channel);
        Assert.False(reopened.LastLoad!.StartedFresh);
    }
}
