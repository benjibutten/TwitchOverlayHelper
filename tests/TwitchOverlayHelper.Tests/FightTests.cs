using TwitchOverlayHelper.Fight;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Settings;

namespace TwitchOverlayHelper.Tests;

public sealed class FightCatalogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fight-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void WritesTheShippedFightersAndArenasOnFirstStart()
    {
        var catalog = new FightCatalog(_folder);

        Assert.Contains(catalog.Fighters, f => f.Id == "silver" && f.IsDefault);
        Assert.Contains(catalog.Fighters, f => f.Id == "ink" && f.IsDefault);
        Assert.True(File.Exists(Path.Combine(_folder, "fighters", "silver", "sprite.webp")));
        Assert.True(File.Exists(Path.Combine(_folder, "arenas", "sky", "arena.webp")));
        Assert.True(File.Exists(Path.Combine(_folder, "LÄS MIG.txt")));
        Assert.Empty(catalog.Warnings);
    }

    [Fact]
    public void TheTransparentArenaComesFirstAndHasNoPicture()
    {
        var catalog = new FightCatalog(_folder);

        ArenaDefinition first = catalog.Arenas[0];
        Assert.Equal(FightCatalog.TransparentArenaId, first.Id);
        Assert.Null(first.ImageFile);
        Assert.Contains(catalog.Arenas, a => a.Id == "garage");
        Assert.Contains(catalog.Arenas, a => a.Id == "sky");
    }

    [Fact]
    public void ADeletedShippedFighterStaysDeleted()
    {
        new FightCatalog(_folder);
        Directory.Delete(Path.Combine(_folder, "fighters", "ink"), recursive: true);

        var catalog = new FightCatalog(_folder);

        Assert.DoesNotContain(catalog.Fighters, f => f.Id == "ink");
    }

    [Fact]
    public void ADroppedInFighterIsPickedUpOnReload()
    {
        var catalog = new FightCatalog(_folder);
        string folder = Path.Combine(_folder, "fighters", "nova");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "fighter.json"), """{ "displayName": "Nova", "scale": 1.2 }""");
        File.WriteAllBytes(Path.Combine(folder, "sprite.webp"), [1, 2, 3]);

        catalog.Reload();

        FighterDefinition nova = Assert.Single(catalog.Fighters, f => f.Id == "nova");
        Assert.Equal("Nova", nova.Name);
        Assert.Equal(1.2, nova.Scale);
        Assert.False(nova.IsDefault);
        Assert.True(catalog.TryGetFighterSprite("nova", out string path));
        Assert.Equal(Path.Combine(folder, "sprite.webp"), path);
    }

    [Fact]
    public void AManifestCannotPointOutsideItsFolder()
    {
        var catalog = new FightCatalog(_folder);
        string folder = Path.Combine(_folder, "fighters", "sneaky");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "fighter.json"), """{ "spritePath": "..\\silver\\sprite.webp" }""");

        catalog.Reload();

        Assert.DoesNotContain(catalog.Fighters, f => f.Id == "sneaky");
        Assert.Contains(catalog.Warnings, w => w.Contains("sneaky"));
    }

    [Fact]
    public void AnArenaWithOutOfRangeNumbersFallsBackToSaneOnes()
    {
        var catalog = new FightCatalog(_folder);
        string folder = Path.Combine(_folder, "arenas", "odd");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "arena.json"), """{ "floor": 4, "left": 0.9, "right": -1 }""");
        File.WriteAllBytes(Path.Combine(folder, "arena.webp"), [1]);

        catalog.Reload();

        ArenaDefinition odd = Assert.Single(catalog.Arenas, a => a.Id == "odd");
        Assert.Equal(FightCatalog.Transparent.Floor, odd.Floor);
        Assert.Equal(FightCatalog.Transparent.Left, odd.Left);
        Assert.Equal(FightCatalog.Transparent.Right, odd.Right);
    }

    [Fact]
    public void AMissingFighterFallsBackToOneThatExists()
    {
        var catalog = new FightCatalog(_folder);

        // Each corner falls back to its own place in the list, so the two never end up the same.
        Assert.Equal(catalog.Fighters[0].Id, catalog.FighterOrFallback("gone", 0)?.Id);
        Assert.Equal(catalog.Fighters[1].Id, catalog.FighterOrFallback("gone", 1)?.Id);
        Assert.Equal("ink", catalog.FighterOrFallback("INK", 0)?.Id);
        Assert.Equal("garage", catalog.ArenaOrFallback("gone").Id);
    }
}

public sealed class FightServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fight-" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings = new();
    private readonly List<FightAssist> _published = [];
    private readonly FightService _service;
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 20, 0, 0, TimeSpan.Zero);

    public FightServiceTests()
    {
        _settings.Normalize();
        _service = new FightService(_settings, new FightCatalog(_folder), _published.Add);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static ChatMessage Line(string text, string user = "42") =>
        new("m", "Kajsa", text, "#FF00AA", [], false, false, Now) { UserId = user, UserLogin = "kajsa" };

    [Theory]
    [InlineData("!heja 1", 1, FightAssistKind.Cheer)]
    [InlineData("!heja p2", 2, FightAssistKind.Cheer)]
    [InlineData("!HEJA 2 kör!", 2, FightAssistKind.Cheer)]
    [InlineData("!heja1", 1, FightAssistKind.Cheer)]
    [InlineData("!hela 2", 2, FightAssistKind.Heal)]
    [InlineData("!heja silver", 1, FightAssistKind.Cheer)]
    [InlineData("!hela Ink", 2, FightAssistKind.Heal)]
    public void ReadsTheCornerFromTheLine(string text, int player, FightAssistKind kind)
    {
        Assert.True(_service.HandleMessage(Line(text), Now));

        FightAssist assist = Assert.Single(_published);
        Assert.Equal(player, assist.Player);
        Assert.Equal(kind, assist.Kind);
        Assert.Equal("Kajsa", assist.Viewer);
        Assert.Equal("#FF00AA", assist.Color);
    }

    [Theory]
    [InlineData("heja 1")]
    [InlineData("!heja")]
    [InlineData("!heja 3")]
    [InlineData("!hejarop 1")]
    [InlineData("!heja12")]
    public void IgnoresLinesThatAreNotACommandForACorner(string text)
    {
        Assert.False(_service.HandleMessage(Line(text), Now));
        Assert.Empty(_published);
    }

    [Fact]
    public void TheCornerStaysTheSameWhenTheFightersSwap()
    {
        (_settings.Fight.Player1, _settings.Fight.Player2) = ("ink", "silver");

        _service.HandleMessage(Line("!heja 1"), Now);
        _service.HandleMessage(Line("!hela silver", "43"), Now);

        Assert.Equal([1, 2], _published.Select(p => p.Player));
    }

    [Fact]
    public void OneViewerWaitsOutTheirCooldown()
    {
        _settings.Fight.CooldownSeconds = 20;

        _service.HandleMessage(Line("!heja 1"), Now);
        _service.HandleMessage(Line("!heja 2"), Now.AddSeconds(5));
        _service.HandleMessage(Line("!hela 2"), Now.AddSeconds(6));
        _service.HandleMessage(Line("!heja 1", "99"), Now.AddSeconds(7));
        _service.HandleMessage(Line("!heja 2"), Now.AddSeconds(21));

        // The second cheer is swallowed; a heal has its own cooldown, and so does somebody else.
        Assert.Equal(4, _published.Count);
    }

    [Fact]
    public void DoesNothingWhenSwitchedOffOrNobodyIsWatching()
    {
        _settings.Fight.CommandsEnabled = false;
        Assert.False(_service.HandleMessage(Line("!heja 1"), Now));

        _settings.Fight.CommandsEnabled = true;
        _service.IsShowing = () => false;
        Assert.False(_service.HandleMessage(Line("!heja 1"), Now));

        Assert.Empty(_published);
    }

    [Fact]
    public void FollowsRenamedCommands()
    {
        _settings.Fight.CheerCommand = "kör";
        _settings.Fight.Normalize();

        Assert.True(_service.HandleMessage(Line("!kör 2"), Now));
        Assert.False(_service.HandleMessage(Line("!heja 2", "7"), Now));
    }
}

public sealed class FightSettingsTests
{
    [Fact]
    public void TwoCommandsNeverShareAWord()
    {
        var fight = new FightSettings { CheerCommand = "!hela", HealCommand = "hela" };

        fight.Normalize();

        Assert.Equal("!hela", fight.CheerCommand);
        Assert.NotEqual(fight.CheerCommand, fight.HealCommand, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("!")]
    [InlineData("  ")]
    [InlineData("! hej")]
    public void AClearedCommandFallsBackToItsOwnWordNotTheModCall(string typed)
    {
        var fight = new FightSettings { CheerCommand = typed, HealCommand = typed };

        fight.Normalize();

        Assert.Equal("!heja", fight.CheerCommand);
        Assert.Equal("!hela", fight.HealCommand);
    }

    [Fact]
    public void KeepsTheBannerAsWrittenButTrimmed()
    {
        var fight = new FightSettings { Headline = "  Hämtar pizza  ", Subline = "" };

        fight.Normalize();

        Assert.Equal("Hämtar pizza", fight.Headline);
        Assert.Equal(string.Empty, fight.Subline);
    }

    [Fact]
    public void ClampsTheCooldownAndCleansIds()
    {
        var fight = new FightSettings { CooldownSeconds = -5, Player1 = " Silver! ", Arena = "" };

        fight.Normalize();

        Assert.Equal(0, fight.CooldownSeconds);
        Assert.Equal("silver", fight.Player1);
        Assert.Equal("garage", fight.Arena);
    }
}

public sealed class FightTextTests
{
    [Fact]
    public void SavesTheCurrentBannerAtTheTop()
    {
        var fight = new FightSettings { Headline = "Hämtar pizza", Subline = "Tillbaka om tio" };
        fight.SaveCurrentText();
        fight.Headline = "Pausar";
        fight.Subline = "";

        Assert.True(fight.SaveCurrentText());

        Assert.Equal(["Pausar", "Hämtar pizza – Tillbaka om tio"], fight.SavedTexts.Select(t => t.Label));
    }

    [Fact]
    public void SavingTheSameTextAgainMovesItUpInsteadOfDoublingIt()
    {
        var fight = new FightSettings { Headline = "A", Subline = "a" };
        fight.SaveCurrentText();
        fight.Headline = "B";
        fight.SaveCurrentText();
        fight.Headline = "A";

        fight.SaveCurrentText();

        Assert.Equal(["A", "B"], fight.SavedTexts.Select(t => t.Headline));
    }

    [Fact]
    public void AnEmptyBannerIsNotSaved()
    {
        var fight = new FightSettings { Headline = "  ", Subline = "" };

        Assert.False(fight.SaveCurrentText());
        Assert.Empty(fight.SavedTexts);
    }

    [Fact]
    public void KeepsAtMostTheLimit()
    {
        var fight = new FightSettings();
        for (int i = 0; i < FightSettings.MaxSavedTexts + 5; i++)
        {
            fight.Headline = "Text " + i;
            fight.SaveCurrentText();
        }

        Assert.Equal(FightSettings.MaxSavedTexts, fight.SavedTexts.Count);
        Assert.Equal("Text " + (FightSettings.MaxSavedTexts + 4), fight.SavedTexts[0].Headline);
    }

    [Fact]
    public void NormalizeCleansAHandEditedList()
    {
        var fight = new FightSettings
        {
            SavedTexts = [new() { Headline = " A " }, null!, new() { Headline = "A" }, new() { Headline = "", Subline = " " }]
        };

        fight.Normalize();

        FightText kept = Assert.Single(fight.SavedTexts);
        Assert.Equal("A", kept.Headline);
    }

    [Fact]
    public void SavedTextsSurviveTheSettingsFile()
    {
        var settings = new AppSettings();
        settings.Fight.Headline = "Hämtar pizza";
        settings.Fight.SaveCurrentText();

        string json = System.Text.Json.JsonSerializer.Serialize(settings);
        AppSettings back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        back.Normalize();

        Assert.Equal("Hämtar pizza", Assert.Single(back.Fight.SavedTexts).Headline);
    }
}
