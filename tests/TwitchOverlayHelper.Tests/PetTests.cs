using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Pets;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Twitch;
using TwitchOverlayHelper.Web;

namespace TwitchOverlayHelper.Tests;

public sealed class PetRegistryTests
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    [Fact]
    public void SpawnsAndListsAPet()
    {
        var registry = new PetRegistry();

        PetSpawnResult result = registry.Spawn("7", "Kajsa", "#9146FF", "robo", Lifetime, maxPets: 6);

        Assert.False(result.Extended);
        Assert.Null(result.RemovedId);
        var pet = Assert.Single(registry.Snapshot());
        Assert.Equal("Kajsa", pet.Name);
        Assert.Equal("robo", pet.Species);
        Assert.True(pet.ExpiresAt > pet.SpawnedAt);
    }

    [Fact]
    public void ExtendsInsteadOfDuplicatingWhenTheSameViewerRedeemsAgain()
    {
        var registry = new PetRegistry();
        PetSpawnResult first = registry.Spawn("7", "Kajsa", null, "robo", Lifetime, 6);

        PetSpawnResult second = registry.Spawn("7", "Kajsa", null, "robo", Lifetime, 6);

        Assert.True(second.Extended);
        Assert.Single(registry.Snapshot());
        Assert.True(second.Pet.ExpiresAt >= first.Pet.ExpiresAt);
    }

    [Fact]
    public void RedeemingAnotherSpeciesTransformsThePet()
    {
        var registry = new PetRegistry();
        registry.Spawn("7", "Kajsa", null, "robo", Lifetime, 6);

        PetSpawnResult result = registry.Spawn("7", "Kajsa", null, "boo", Lifetime, 6);

        Assert.True(result.Extended);
        Assert.Equal("boo", Assert.Single(registry.Snapshot()).Species);
    }

    [Fact]
    public void EvictsTheOldestPetWhenFull()
    {
        var registry = new PetRegistry();
        registry.Spawn("a", "Första", null, "robo", Lifetime, maxPets: 2);
        registry.Spawn("b", "Andra", null, "robo", Lifetime, maxPets: 2);

        PetSpawnResult result = registry.Spawn("c", "Tredje", null, "robo", Lifetime, maxPets: 2);

        Assert.Equal("a", result.RemovedId);
        Assert.Equal(["b", "c"], registry.Snapshot().Select(pet => pet.Id));
    }

    [Fact]
    public void DropsExpiredPetsFromTheSnapshot()
    {
        var registry = new PetRegistry();
        registry.Spawn("a", "Kort liv", null, "robo", TimeSpan.FromMilliseconds(-1), 6);

        Assert.Empty(registry.Snapshot());
    }
}

public sealed class PetRewardRuleTests
{
    private static PetSettings Settings(params PetRewardRule[] rules)
    {
        var settings = new PetSettings { Rewards = [.. rules] };
        settings.Normalize();
        return settings;
    }

    [Fact]
    public void WithoutAnyRulesEveryRedemptionIsWorthTheDefaultTime()
    {
        PetSettings settings = Settings();

        Assert.Equal(5, settings.LifetimeMinutesFor("vad-som-helst"));
    }

    [Fact]
    public void EachRuleAnswersForItsOwnReward()
    {
        PetSettings settings = Settings(
            new PetRewardRule { RewardId = "kort", Minutes = 5 },
            new PetRewardRule { RewardId = "lang", Minutes = 10 });

        Assert.Equal(5, settings.LifetimeMinutesFor("KORT"));
        Assert.Equal(10, settings.LifetimeMinutesFor("lang"));
        Assert.Null(settings.LifetimeMinutesFor("nagot-annat"));
    }

    [Fact]
    public void ARuleWithoutAnIdCatchesEveryOtherReward()
    {
        PetSettings settings = Settings(
            new PetRewardRule { RewardId = "lang", Minutes = 10 },
            new PetRewardRule { Minutes = 2 });

        Assert.Equal(10, settings.LifetimeMinutesFor("lang"));
        Assert.Equal(2, settings.LifetimeMinutesFor("nagot-annat"));
    }

    [Fact]
    public void TheOldSingleRewardSettingBecomesTheFirstRule()
    {
        var settings = new PetSettings { RewardId = "gammal", LifetimeMinutes = 7 };

        settings.Normalize();

        PetRewardRule rule = Assert.Single(settings.Rewards);
        Assert.Equal("gammal", rule.RewardId);
        Assert.Equal(7, rule.Minutes);
        Assert.Equal(string.Empty, settings.RewardId);
        Assert.Null(settings.LifetimeMinutesFor("nagot-annat"));
    }

    [Fact]
    public void AHandEditedNullRuleIsDroppedInsteadOfTakingTheAppDown()
    {
        var settings = new PetSettings { Rewards = [null!, new PetRewardRule { RewardId = "kort", Minutes = 5 }] };

        settings.Normalize();

        Assert.Equal("kort", Assert.Single(settings.Rewards).RewardId);
        Assert.Equal(5, settings.LifetimeMinutesFor("kort"));
    }

    [Fact]
    public void DuplicateRewardsAndOutOfRangeTimesAreCleanedUp()
    {
        PetSettings settings = Settings(
            new PetRewardRule { RewardId = " kort ", Minutes = 900 },
            new PetRewardRule { RewardId = "KORT", Minutes = 3 });

        PetRewardRule rule = Assert.Single(settings.Rewards);
        Assert.Equal("kort", rule.RewardId);
        Assert.Equal(60, rule.Minutes);
    }
}

public sealed class PetCatalogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private PetCatalog Catalog() => new(_folder);

    private void WritePet(string folderName, string json, bool withSprite = true)
    {
        string dir = Path.Combine(_folder, folderName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pet.json"), json);
        if (withSprite) File.WriteAllBytes(Path.Combine(dir, "spritesheet.webp"), [1, 2, 3]);
    }

    [Fact]
    public void TheShippedPetsAreWrittenToTheFolderSoTheyCanBeEdited()
    {
        PetCatalog catalog = Catalog();

        Assert.True(catalog.Pets.Count >= 8);
        Assert.All(catalog.Pets, pet => Assert.True(pet.IsDefault));
        // Every one of them is a real folder the streamer can open and change.
        Assert.All(catalog.Pets, pet =>
        {
            Assert.True(File.Exists(Path.Combine(_folder, pet.Id, "pet.json")));
            Assert.True(File.Exists(Path.Combine(_folder, pet.Id, "body.svg")));
        });
        Assert.Empty(catalog.Warnings);
    }

    [Fact]
    public void AnEditedPetIsWhatTheOverlayGetsServed()
    {
        PetCatalog catalog = Catalog();
        File.WriteAllText(Path.Combine(_folder, "robo", "body.svg"), "<svg><circle r=\"1\" /></svg>");
        File.WriteAllText(Path.Combine(_folder, "robo", "pet.json"),
            """{ "id": "robo", "displayName": "Rostiga Rolf", "aliases": ["rolf"] }""");

        catalog.Reload();

        Assert.Equal("Rostiga Rolf", catalog.Find("robo")!.Name);
        Assert.Equal("robo", catalog.Find("rolf")!.Id);
        Assert.True(catalog.TryGetBody("robo", out string svg));
        Assert.Contains("circle", svg);
    }

    [Fact]
    public void ADeletedPetStaysDeleted()
    {
        PetCatalog catalog = Catalog();
        Directory.Delete(Path.Combine(_folder, "boo"), recursive: true);

        catalog.Reload();
        Assert.Null(catalog.Find("boo"));

        // A fresh start must not quietly bring it back.
        Assert.Null(Catalog().Find("boo"));
    }

    [Fact]
    public void ThePetsSurviveAFolderThatCannotBeWritten()
    {
        // A file where the folder should be: the copies inside the exe have to carry the overlay.
        string blocked = Path.Combine(_folder, "not-a-folder");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(blocked, "i vägen");
        var catalog = new PetCatalog(blocked);

        Assert.True(catalog.Pets.Count >= 8);
        Assert.NotNull(catalog.Find("blaze"));
        Assert.True(catalog.TryGetBody("blaze", out string svg));
        Assert.Contains("<svg", svg);
        Assert.NotEmpty(catalog.Warnings);
    }

    [Fact]
    public void ResolvesSpeciesByIdNameAndAliasCaseInsensitively()
    {
        PetCatalog catalog = Catalog();

        Assert.Equal("blaze", catalog.Find("BLAZE")!.Id);
        Assert.Equal("owly", catalog.Find("uggla")!.Id);
        Assert.Null(catalog.Find("enhörning"));
    }

    [Fact]
    public void FindsTheSpeciesNamedAnywhereInTheRedemptionText()
    {
        PetCatalog catalog = Catalog();

        Assert.Equal("boo", catalog.ResolveFromText("kan jag få ett SPÖKE tack!")!.Id);
        Assert.Equal("blaze", catalog.ResolveFromText("blaze")!.Id);
        Assert.Null(catalog.ResolveFromText("hej på er"));
        Assert.Null(catalog.ResolveFromText(""));
    }

    [Fact]
    public void FindsNamesThatAreNotASingleWord()
    {
        // The id format allows - and _, and a display name may well be two words.
        WritePet("space-cat", """{ "id": "space-cat", "displayName": "Rymd Katten", "aliases": ["rymd_katt"] }""");
        PetCatalog catalog = Catalog();

        Assert.Equal("space-cat", catalog.ResolveFromText("kan jag få en space-cat tack!")!.Id);
        Assert.Equal("space-cat", catalog.ResolveFromText("en RYMD KATTEN, tack")!.Id);
        Assert.Equal("space-cat", catalog.ResolveFromText("rymd_katt")!.Id);
        // A name only counts when it stands on its own.
        Assert.Null(catalog.ResolveFromText("spacecat"));
        Assert.Null(catalog.ResolveFromText("blazer"));
    }

    [Fact]
    public void TheFirstSpeciesNamedInTheTextIsTheOneSpawned()
    {
        PetCatalog catalog = Catalog();

        Assert.Equal("boo", catalog.ResolveFromText("ett spöke eller en blaze")!.Id);
        Assert.Equal("blaze", catalog.ResolveFromText("en blaze eller ett spöke")!.Id);
    }

    [Fact]
    public void ChoosePrefersTextThenDefaultThenAnything()
    {
        PetCatalog catalog = Catalog();

        Assert.Equal("rocky", catalog.Choose("en sten tack", "owly").Id);
        Assert.Equal("owly", catalog.Choose("hej", "owly").Id);
        Assert.NotNull(catalog.Choose("hej", ""));
    }

    [Fact]
    public void LoadsACodexHatchPetFolderUnchanged()
    {
        // The exact fields hatch-pet writes: id, displayName, description, spritesheetPath.
        WritePet("nova", """{ "id": "nova", "displayName": "Nova", "description": "En stjärna.", "spritesheetPath": "spritesheet.webp" }""");
        PetCatalog catalog = Catalog();

        PetDefinition pet = Assert.Single(catalog.Pets, p => !p.IsDefault);
        Assert.Equal("nova", pet.Id);
        Assert.Equal("Nova", pet.Name);
        Assert.True(catalog.TryGetSpriteFile("nova", out string path));
        Assert.True(File.Exists(path));
        Assert.Equal("nova", catalog.ResolveFromText("jag vill ha nova!")!.Id);
    }

    [Fact]
    public void LoadsAVersionTwoHatchPetFolderUnchanged()
    {
        // The exact fields hatch-pet writes for an extended sheet, look-direction rows included.
        WritePet("mypet", """
            { "id": "mypet", "displayName": "MyPet", "description": "En chibi-flicka.",
              "spriteVersionNumber": 2, "spritesheetPath": "spritesheet.webp" }
            """);
        PetCatalog catalog = Catalog();

        PetDefinition pet = catalog.Find("mypet")!;
        Assert.Equal(2, pet.SpriteVersion);
        Assert.True(catalog.TryGetSpriteFile("mypet", out _));
    }

    [Fact]
    public void SheetsWithoutAVersionAreVersionOne()
    {
        WritePet("nova", """{ "id": "nova" }""");

        Assert.Equal(1, Catalog().Find("nova")!.SpriteVersion);
    }

    [Fact]
    public void AFutureSpriteVersionIsReadAsTheNewestKnownOne()
    {
        // Later versions extend the sheet downwards, so the rows a version 2 knows stay where they are.
        WritePet("framtid", """{ "id": "framtid", "spriteVersionNumber": 7 }""");

        Assert.Equal(2, Catalog().Find("framtid")!.SpriteVersion);
    }

    [Fact]
    public void CustomAliasesAndFpsAreOptionalExtras()
    {
        WritePet("nova", """{ "id": "nova", "displayName": "Nova", "aliases": ["stjärna"], "fps": 12 }""");
        PetCatalog catalog = Catalog();

        Assert.Equal("nova", catalog.Find("stjärna")!.Id);
        Assert.Equal(12, catalog.Find("nova")!.Fps);
    }

    [Fact]
    public void BrokenPetsBecomeWarningsInsteadOfCrashes()
    {
        WritePet("trasig", "{ inte json");
        WritePet("utan-bild", """{ "id": "utan-bild" }""", withSprite: false);
        PetCatalog catalog = Catalog();

        Assert.DoesNotContain(catalog.Pets, pet => !pet.IsDefault);
        Assert.Equal(2, catalog.Warnings.Count);
    }

    [Fact]
    public void ACustomPetCannotStealABuiltInName()
    {
        WritePet("fusk", """{ "id": "fusk", "displayName": "Blaze", "aliases": ["robo"] }""");
        PetCatalog catalog = Catalog();

        Assert.Equal("blaze", catalog.Find("blaze")!.Id);
        Assert.Equal("robo", catalog.Find("robo")!.Id);
        Assert.Equal("fusk", catalog.Find("fusk")!.Id);
    }

    [Fact]
    public void ReloadPicksUpANewlyDroppedInPet()
    {
        PetCatalog catalog = Catalog();
        Assert.DoesNotContain(catalog.Pets, pet => !pet.IsDefault);

        WritePet("nova", """{ "id": "nova" }""");
        catalog.Reload();

        Assert.Contains(catalog.Pets, pet => pet.Id == "nova");
    }

    [Fact]
    public void ASpritesheetPathOutsideThePetFolderIsRejected()
    {
        WritePet("smitare", """{ "id": "smitare", "spritesheetPath": "../../settings.json" }""");
        PetCatalog catalog = Catalog();

        Assert.DoesNotContain(catalog.Pets, pet => pet.Id == "smitare");
        Assert.Contains(catalog.Warnings, warning => warning.Contains("utanför"));
    }
}

public sealed class PetServiceTests
{
    /// <summary>Shared so the pets that ship with the app are written out once, not once per test.</summary>
    private static readonly string PetsFolder = Path.Combine(Path.GetTempPath(), "toh-tests-pets");

    private static (PetService Service, PetRegistry Registry, AppSettings Settings) Build()
    {
        var settings = new AppSettings();
        settings.Normalize();
        var registry = new PetRegistry();
        var catalog = new PetCatalog(PetsFolder);
        var hub = new ChatHub(settings, new TwitchBadgeCatalog(), new TwitchSession(new System.Net.Http.HttpClient()), registry, catalog,
            new TwitchOverlayHelper.Nicknames.NicknameBook());
        return (new PetService(settings, catalog, registry, hub), registry, settings);
    }

    private static ChatMessage Message(string text, string? rewardId = null, ChatBadge[]? badges = null) =>
        new("m1", "Kajsa", text, "#9146FF", badges ?? [], false, false, DateTimeOffset.Now)
        { UserId = "7", UserLogin = "kajsa", RewardId = rewardId };

    private static RewardRedemption Redemption(string rewardId, string title, string? userInput) =>
        new("r1", rewardId, title, 500, "7", "kajsa", "Kajsa", userInput, DateTimeOffset.Now);

    [Fact]
    public void SpawnsAPetForAnyRedemptionWhenNoRewardIsPinned()
    {
        (PetService service, PetRegistry registry, _) = Build();

        service.HandleMessage(Message("en robot tack", rewardId: "abc"));

        Assert.Single(registry.Snapshot());
    }

    // The whole reason EventSub was worth building: a reward with no text field never reaches IRC,
    // so before this it could not spawn anything at all.
    [Fact]
    public void SpawnsAPetForARedemptionThatCarriesNoText()
    {
        (PetService service, PetRegistry registry, _) = Build();

        service.HandleRedemption(Redemption("abc", "Pet i 5 minuter", userInput: null));

        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void LetsARuleNameTheRewardInsteadOfPastingItsId()
    {
        (PetService service, PetRegistry registry, AppSettings settings) = Build();
        settings.Pets.Rewards = [new PetRewardRule { RewardName = "Pet i 5 minuter", Minutes = 5 }];

        service.HandleRedemption(Redemption("vilket-guid-som-helst", "pet i 5 MINUTER", userInput: null));
        Assert.Single(registry.Snapshot());

        service.HandleRedemption(Redemption("annat-guid", "Något helt annat", userInput: null));
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void ReadsTheSpeciesOutOfWhatTheViewerTyped()
    {
        (PetService service, PetRegistry registry, _) = Build();

        service.HandleRedemption(Redemption("abc", "Pet", userInput: "en BLAZE tack"));

        Assert.Equal("blaze", Assert.Single(registry.Snapshot()).Species);
    }

    // A redemption that asked for text arrives twice: once over EventSub and once as the chat line
    // it produced. Acting on both would give one purchase two pets.
    [Fact]
    public void SpawnsOnlyOnePetWhenTheSameRedemptionArrivesOverBothRoutes()
    {
        (PetService service, PetRegistry registry, _) = Build();
        service.RedemptionsFromEventSub = true;

        service.HandleRedemption(Redemption("abc", "Pet", userInput: "en robot tack"));
        service.HandleMessage(Message("en robot tack", rewardId: "abc"));

        Assert.Single(registry.Snapshot());
    }

    // Without EventSub – someone else's channel, or a login that predates the scope – IRC is the
    // only route there is, and it has to keep working exactly as it did.
    [Fact]
    public void StillSpawnsFromIrcAloneWhenEventSubIsNotRunning()
    {
        (PetService service, PetRegistry registry, _) = Build();
        service.RedemptionsFromEventSub = false;

        service.HandleMessage(Message("en robot tack", rewardId: "abc"));

        Assert.Single(registry.Snapshot());
    }

    // The failure the flag could cause if it were ever left switched on: EventSub drops, its events
    // reach nobody, and IRC has been told to keep quiet – so a redemption spawns nothing at all.
    // Dropping coverage is what hands the job back to IRC.
    [Fact]
    public void GoesBackToIrcWhenEventSubStopsCovering()
    {
        (PetService service, PetRegistry registry, _) = Build();

        service.RedemptionsFromEventSub = true;
        service.HandleMessage(Message("under avbrottet", rewardId: "abc"));
        Assert.Empty(registry.Snapshot());

        service.RedemptionsFromEventSub = false;
        service.HandleMessage(Message("efter avbrottet", rewardId: "abc"));
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void TheRedemptionTextPicksTheSpecies()
    {
        (PetService service, PetRegistry registry, _) = Build();

        service.HandleMessage(Message("jag vill ha en BLAZE tack", rewardId: "abc"));

        Assert.Equal("blaze", Assert.Single(registry.Snapshot()).Species);
    }

    [Fact]
    public void FallsBackToTheDefaultPetWhenTheTextNamesNothing()
    {
        (PetService service, PetRegistry registry, AppSettings settings) = Build();
        settings.Pets.DefaultPet = "owly";

        service.HandleMessage(Message("hejsan!", rewardId: "abc"));

        Assert.Equal("owly", Assert.Single(registry.Snapshot()).Species);
    }

    [Fact]
    public void IgnoresRedemptionsOfRewardsThatAreNotInTheList()
    {
        (PetService service, PetRegistry registry, AppSettings settings) = Build();
        settings.Pets.Rewards = [new PetRewardRule { RewardId = "pet-reward", Minutes = 5 }];

        service.HandleMessage(Message("fel belöning", rewardId: "hydrate"));
        Assert.Empty(registry.Snapshot());

        service.HandleMessage(Message("rätt belöning", rewardId: "PET-REWARD"));
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void EachRewardBuysItsOwnTimeOnScreen()
    {
        (PetService service, PetRegistry registry, AppSettings settings) = Build();
        settings.Pets.Rewards =
        [
            new PetRewardRule { Label = "Pet 5 min", RewardId = "kort", Minutes = 5 },
            new PetRewardRule { Label = "Pet 10 min", RewardId = "lang", Minutes = 10 }
        ];

        service.HandleMessage(Message("kort tack", rewardId: "kort"));
        service.HandleMessage(new ChatMessage("m2", "Pelle", "lång tack", null, [], false, false, DateTimeOffset.Now)
        { UserId = "8", UserLogin = "pelle", RewardId = "lang" });

        PetState[] pets = registry.Snapshot().ToArray();
        long shortMs = pets[0].ExpiresAt - pets[0].SpawnedAt;
        long longMs = pets[1].ExpiresAt - pets[1].SpawnedAt;
        Assert.Equal(TimeSpan.FromMinutes(5).TotalMilliseconds, shortMs);
        Assert.Equal(TimeSpan.FromMinutes(10).TotalMilliseconds, longMs);
    }

    [Fact]
    public void IgnoresOrdinaryChatMessages()
    {
        (PetService service, PetRegistry registry, _) = Build();

        service.HandleMessage(Message("bara en vanlig rad"));

        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public void LetsModeratorsTestWithTheCommandButNotViewers()
    {
        (PetService service, PetRegistry registry, _) = Build();

        service.HandleMessage(Message("!pet"));
        Assert.Empty(registry.Snapshot());

        service.HandleMessage(Message("!pet", badges: [new ChatBadge("moderator", "1")]));
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void ModeratorsCanNameASpeciesInTheTestCommand()
    {
        (PetService service, PetRegistry registry, _) = Build();

        service.HandleMessage(Message("!pet boo", badges: [new ChatBadge("moderator", "1")]));

        Assert.Equal("boo", Assert.Single(registry.Snapshot()).Species);
    }

    [Fact]
    public void TestSpawnsAreLabelledWithTheSpeciesTheyShow()
    {
        (PetService service, PetRegistry registry, _) = Build();

        service.SpawnTest();

        PetState pet = Assert.Single(registry.Snapshot());
        Assert.EndsWith(" (test)", pet.Name);
        Assert.Equal(pet.Species, pet.Name[..^" (test)".Length].ToLowerInvariant());
    }

    [Fact]
    public void DoesNothingWhenPetsAreDisabled()
    {
        (PetService service, PetRegistry registry, AppSettings settings) = Build();
        settings.Pets.Enabled = false;

        service.HandleMessage(Message("en robot tack", rewardId: "abc"));

        Assert.Empty(registry.Snapshot());
    }
}
