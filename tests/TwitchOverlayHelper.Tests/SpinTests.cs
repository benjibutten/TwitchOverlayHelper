using System.IO;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Nicknames;
using TwitchOverlayHelper.Pets;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Spins;
using TwitchOverlayHelper.Twitch;
using TwitchOverlayHelper.Web;

namespace TwitchOverlayHelper.Tests;

public sealed class PetRarityTests
{
    [Fact]
    public void UnknownAndEmptyRaritiesReadAsCommon()
    {
        Assert.Equal(PetRarity.Common, PetRarity.Normalize(null));
        Assert.Equal(PetRarity.Common, PetRarity.Normalize("  "));
        Assert.Equal(PetRarity.Common, PetRarity.Normalize("mytisk"));
        Assert.Equal(PetRarity.Legendary, PetRarity.Normalize(" LEGENDARISK "));
    }

    [Fact]
    public void RarerTiersWeighLess()
    {
        Assert.True(PetRarity.Weight(PetRarity.Common) > PetRarity.Weight(PetRarity.Uncommon));
        Assert.True(PetRarity.Weight(PetRarity.Uncommon) > PetRarity.Weight(PetRarity.Rare));
        Assert.True(PetRarity.Weight(PetRarity.Rare) > PetRarity.Weight(PetRarity.Legendary));
        Assert.True(PetRarity.Weight(PetRarity.Legendary) > 0);
    }
}

public sealed class SpinDrawTests
{
    private static PetDefinition Pet(string id, string rarity) =>
        new(id, id, string.Empty, [], ["✨"], IsDefault: false, Rarity: rarity);

    [Fact]
    public void ASinglePetAlwaysWins()
    {
        PetDefinition only = Pet("drake", PetRarity.Legendary);
        Assert.Equal("drake", SpinService.Draw([only], new Random(1)).Id);
    }

    // The point of the weights: a legendary shows up, but far less often than a common. Seeded, so
    // the test is the same test every run.
    [Fact]
    public void TheDrawFollowsTheRarityWeights()
    {
        PetDefinition[] pool = [Pet("vanlig", PetRarity.Common), Pet("legend", PetRarity.Legendary)];
        var random = new Random(42);

        int legends = 0;
        for (int i = 0; i < 10_000; i++)
            if (SpinService.Draw(pool, random).Id == "legend") legends++;

        // Expected share is 3/103 ≈ 291 of 10 000; anywhere in this band says the weights work.
        Assert.InRange(legends, 100, 600);
    }
}

public sealed class SpinSettingsTests
{
    [Fact]
    public void NormalizeCleansTheEdges()
    {
        var settings = new SpinSettings
        {
            Side = "UPPÅT",
            SpinSeconds = 99,
            GiftTimeoutMinutes = 0,
            Title = " ",
            Managed = true,
            RewardId = ""
        };

        settings.Normalize();

        Assert.Equal("right", settings.Side);
        Assert.Equal(20, settings.SpinSeconds);
        Assert.Equal(1, settings.GiftTimeoutMinutes);
        Assert.Equal("Lyckosnurren", settings.Title);
        // No reward id means nothing this app could ever answer for, whatever the file claims.
        Assert.False(settings.Managed);
        Assert.False(settings.CanRefund);
    }

    [Fact]
    public void TheThreeCommandsCanNeverCollide()
    {
        var settings = new SpinSettings { ListCommand = "mina", GiveCommand = "!MINA" };
        settings.Normalize();
        Assert.Equal("!mina", settings.ListCommand);
        Assert.NotEqual(settings.ListCommand, settings.GiveCommand, StringComparer.OrdinalIgnoreCase);

        var reversed = new SpinSettings { ListCommand = "!ge", GiveCommand = "ge" };
        reversed.Normalize();
        Assert.NotEqual(reversed.ListCommand, reversed.GiveCommand, StringComparer.OrdinalIgnoreCase);

        // The pool command gives way to both, and to a word that is actually free rather than one
        // that would only collide with the other of them.
        var crowded = new SpinSettings { ListCommand = "!alla", GiveCommand = "!vinster", PoolCommand = "ALLA" };
        crowded.Normalize();
        Assert.Equal("!alla", crowded.ListCommand);
        Assert.NotEqual(crowded.PoolCommand, crowded.ListCommand, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(crowded.PoolCommand, crowded.GiveCommand, StringComparer.OrdinalIgnoreCase);

        var blank = new SpinSettings { PoolCommand = "   " };
        blank.Normalize();
        Assert.Equal("!alla", blank.PoolCommand);
        Assert.Equal("!släpp", blank.ReleaseCommand);

        var release = new SpinSettings { GiveCommand = "!släpp", ReleaseCommand = "släpp" };
        release.Normalize();
        Assert.NotEqual(release.ReleaseCommand, release.GiveCommand, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void MatchesByIdOrByName()
    {
        var settings = new SpinSettings { RewardId = "abc", RewardName = "Lyckosnurr" };
        Assert.True(settings.MatchesReward("ABC", null));
        Assert.True(settings.MatchesReward("annat", "lyckosnurr"));
        Assert.False(settings.MatchesReward("annat", "något annat"));
    }
}

public sealed class SpinWinStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "spinstore-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_folder, "spinwins.json");

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static SpinWin Win(string userId, string petId, string name = "Kajsa") =>
        new(userId, name.ToLowerInvariant(), name, petId, DateTimeOffset.UtcNow);

    [Fact]
    public void WinsSurviveARestart()
    {
        var store = new SpinWinStore(StorePath);
        store.Add(Win("7", "drake"));
        store.Add(Win("7", "katt"));

        var reloaded = new SpinWinStore(StorePath);

        Assert.True(reloaded.Owns("7", "drake"));
        Assert.Equal(2, reloaded.WinsFor("7").Count);
        Assert.False(reloaded.Owns("8", "drake"));
    }

    [Fact]
    public void ASecondCopyOfTheSamePetIsRefused()
    {
        var store = new SpinWinStore(StorePath);
        store.Add(Win("7", "drake"));
        store.Add(Win("7", "drake"));

        Assert.Single(store.WinsFor("7"));
    }

    // The whole reason the id is the key: the person is the same after a rename, and the stored
    // names catch up the next time they are seen.
    [Fact]
    public void TouchFollowsARename()
    {
        var store = new SpinWinStore(StorePath);
        store.Add(Win("7", "drake", "GamlaNamnet"));

        store.Touch("7", "nyanamnet", "NyaNamnet");

        SpinWin win = Assert.Single(store.WinsFor("7"));
        Assert.Equal("NyaNamnet", win.DisplayName);
        Assert.Equal("nyanamnet", win.Login);
    }

    // Renaming a pet in the settings window must not quietly take everybody's copy of it away.
    [Fact]
    public void RenamingAPetTakesItsWinsAndOpenGiftsAlong()
    {
        var store = new SpinWinStore(StorePath);
        store.Add(Win("7", "drake"));
        store.Add(Win("8", "drake"));
        store.Add(Win("8", "katt"));
        store.AddPending(new SpinPendingGift("r1", "9", "Pelle", "drake", DateTimeOffset.UtcNow.AddMinutes(10)));

        Assert.Equal(2, store.RenamePet("drake", "gyllene-draken"));

        var reloaded = new SpinWinStore(StorePath);
        Assert.True(reloaded.Owns("7", "gyllene-draken"));
        Assert.False(reloaded.Owns("7", "drake"));
        Assert.Equal(2, reloaded.OwnerCount("gyllene-draken"));
        Assert.True(reloaded.Owns("8", "katt"));
        Assert.Equal("gyllene-draken", reloaded.PendingFor("9")!.PetId);
    }

    // A pet renamed back to something an old row still points at would otherwise leave one viewer
    // holding the same prize twice – the one thing every other path here refuses outright.
    [Fact]
    public void RenamingOntoAnIdSomebodyAlreadyHoldsLeavesOneCopy()
    {
        var store = new SpinWinStore(StorePath);
        store.Add(Win("7", "katt"));
        store.Add(Win("7", "drake"));

        store.RenamePet("drake", "KATT");

        Assert.Single(store.WinsFor("7"));
        Assert.True(store.Owns("7", "katt"));
    }

    [Fact]
    public void PendingGiftsAreHeldExpiredAndTakenExactlyOnce()
    {
        var store = new SpinWinStore(StorePath);
        var fresh = new SpinPendingGift("r1", "7", "Kajsa", "drake", DateTimeOffset.UtcNow.AddMinutes(10));
        var stale = new SpinPendingGift("r2", "8", "Pelle", "katt", DateTimeOffset.UtcNow.AddMinutes(-1));
        store.AddPending(fresh);
        store.AddPending(stale);

        Assert.True(store.HoldsPending("r1"));
        Assert.Equal("drake", store.PendingFor("7")!.PetId);

        IReadOnlyList<SpinPendingGift> expired = store.TakeExpired(DateTimeOffset.UtcNow);
        Assert.Equal("r2", Assert.Single(expired).RedemptionId);
        Assert.Empty(store.TakeExpired(DateTimeOffset.UtcNow));
        Assert.True(store.HoldsPending("r1"));
        Assert.False(store.HoldsPending("r2"));
    }

    [Fact]
    public void ASecondDuplicateReplacesTheWinnersOpenGiftAndHandsTheOldOneBack()
    {
        var store = new SpinWinStore(StorePath);
        var first = new SpinPendingGift("r1", "7", "Kajsa", "drake", DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.Null(store.AddPending(first));

        SpinPendingGift? displaced = store.AddPending(
            new SpinPendingGift("r2", "7", "Kajsa", "katt", DateTimeOffset.UtcNow.AddMinutes(10)));

        // The one that was pushed out has to come back out, or its winner is left waiting for a
        // prompt that no longer means anything.
        Assert.Equal("r1", displaced!.RedemptionId);
        Assert.Equal("katt", store.PendingFor("7")!.PetId);
        Assert.False(store.HoldsPending("r1"));
    }

    // The same crash window a win has: a gift on disk with no debt beside it is a spin the startup
    // sweep would pay back, on top of a chance its winner has already been given.
    [Fact]
    public void AnOpenGiftAndTheDebtItLeavesAreTheSameWrite()
    {
        var store = new SpinWinStore(StorePath);
        store.AddPending(
            new SpinPendingGift("r1", "7", "Kajsa", "drake", DateTimeOffset.UtcNow.AddMinutes(10)),
            new SpinOwedFulfilment("r1", "snurr", "kanal", "Kajsa", 500, "dubbletten är bokförd"));

        var reloaded = new SpinWinStore(StorePath);
        Assert.True(reloaded.HoldsPending("r1"));
        Assert.True(reloaded.Owes("r1"));
    }

    // Gifts written before a duplicate's points were answered at the draw. Their redemption is
    // still open on Twitch, and nothing on today's record can say so – so the load has to finish
    // them: the one still open is answered as delivered, and the one that ran out while the app was
    // closed is let go, back to the sweep whose job paying it back is.
    [Fact]
    public void GiftsFromBeforeTheDuplicateWasPaidForAreSettledOnTheWayIn()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(StorePath, """
            {
              "Wins": [],
              "Pending": [
                { "RedemptionId": "r1", "RewardId": "snurr", "WinnerUserId": "7", "WinnerLogin": "kajsa",
                  "WinnerName": "Kajsa", "PetId": "drake", "Cost": 500, "Refundable": true,
                  "ExpiresAt": "2999-01-01T00:00:00+00:00" },
                { "RedemptionId": "r2", "RewardId": "snurr", "WinnerUserId": "8", "WinnerLogin": "pelle",
                  "WinnerName": "Pelle", "PetId": "katt", "Cost": 500, "Refundable": true,
                  "ExpiresAt": "2020-01-01T00:00:00+00:00" }
              ],
              "Owed": []
            }
            """);

        var store = new SpinWinStore(StorePath);

        // Still open, so the chance stands – and the purchase behind it is owed a verdict the app
        // can actually give. No channel was ever written down, so it is raised wherever we land.
        Assert.True(store.HoldsPending("r1"));
        Assert.Equal("Kajsa", store.PendingFor("7")?.WinnerName);
        SpinOwedFulfilment debt = Assert.Single(store.ResumeOwed("kanal", 5));
        Assert.Equal("r1", debt.RedemptionId);
        Assert.Equal("snurr", debt.RewardId);
        Assert.Equal(500, debt.Cost);
        Assert.Equal("Kajsa", debt.ViewerName);

        // Timed out unseen: gone, and holding nothing – which is what lets the sweep find its
        // redemption and hand the points back.
        Assert.False(store.HoldsPending("r2"));
        Assert.False(store.Owes("r2"));
        Assert.Null(store.PendingFor("8"));

        // And the file is in today's shape, so the same two rows are not settled all over again.
        Assert.DoesNotContain("Refundable", File.ReadAllText(StorePath));
        var reloaded = new SpinWinStore(StorePath);
        Assert.True(reloaded.HoldsPending("r1"));
        Assert.Single(reloaded.ResumeOwed("kanal", 5));
    }

    [Fact]
    public void LeavingTheChannelDropsTheGiftsAndKeepsTheWins()
    {
        var store = new SpinWinStore(StorePath);
        store.Add(Win("7", "drake"));
        store.AddPending(new SpinPendingGift("r1", "7", "Kajsa", "katt", DateTimeOffset.UtcNow.AddMinutes(10)));

        store.ClearPending();

        Assert.Null(store.PendingFor("7"));
        Assert.False(store.HoldsPending("r1"));
        Assert.True(new SpinWinStore(StorePath).Owns("7", "drake"));
    }

    [Fact]
    public void AnOwedFulfilmentOutlivesTheProcessAndIsGivenUpOnEventually()
    {
        var store = new SpinWinStore(StorePath);
        store.Owe(new SpinOwedFulfilment("r1", "snurr", "kanal", "Kajsa", 500, "vinsten är bokförd"));

        var reloaded = new SpinWinStore(StorePath);
        Assert.True(reloaded.Owes("r1"));
        // Another channel's debt waits for that channel rather than spending its attempts on refusals.
        Assert.Empty(reloaded.ResumeOwed("annan-kanal", 5));
        Assert.Equal("Kajsa", Assert.Single(reloaded.ResumeOwed("kanal", 5)).ViewerName);

        // Four replays in all; the fifth is the one it never gets, and the debt is written off.
        for (int i = 0; i < 3; i++) Assert.Single(reloaded.ResumeOwed("kanal", 5));
        Assert.Empty(reloaded.ResumeOwed("kanal", 5));
        Assert.False(reloaded.Owes("r1"));
    }

    // The crash window: a win on disk with no debt beside it is a prize the startup sweep would pay
    // back. One write, so there is no moment where only half of it is true.
    [Fact]
    public void AWinAndTheDebtItLeavesAreTheSameWrite()
    {
        var store = new SpinWinStore(StorePath);
        store.Add(
            Win("7", "drake") with { RedemptionId = "r1" },
            new SpinOwedFulfilment("r1", "snurr", "kanal", "Kajsa", 500, "vinsten är bokförd"));

        var reloaded = new SpinWinStore(StorePath);
        Assert.True(reloaded.Owns("7", "drake"));
        Assert.True(reloaded.Owes("r1"));
    }

    // Two winners can come back from Twitch at the same moment holding the same pet and the same
    // friend's name. Only one of them gave it, and the other must not be answered as if it had.
    [Fact]
    public void OnlyOneOfTwoGiftsOfTheSamePetToTheSamePersonLands()
    {
        var store = new SpinWinStore(StorePath);
        store.AddPending(new SpinPendingGift("r1", "7", "Kajsa", "drake", DateTimeOffset.UtcNow.AddMinutes(10)));
        store.AddPending(new SpinPendingGift("r2", "8", "Pelle", "drake", DateTimeOffset.UtcNow.AddMinutes(10)));

        SpinWin gift = new("99", "olle", "Olle", "drake", DateTimeOffset.UtcNow);
        Assert.Equal(SpinGiftResult.Given, store.TryGive("r1", gift));
        Assert.Equal(SpinGiftResult.AlreadyOwned, store.TryGive("r2", gift));

        // Olle keeps the one copy, and the second gift is spent rather than left open: naming
        // somebody who cannot take the pet is how the chance goes.
        Assert.Single(store.WinsFor("99"));
        Assert.False(store.HoldsPending("r2"));

        // And a gift that timed out or was already given hands back nothing at all.
        Assert.Equal(SpinGiftResult.Gone, store.TryGive("r1", new SpinWin("55", "moa", "Moa", "drake", DateTimeOffset.UtcNow)));
        Assert.Empty(store.WinsFor("55"));
    }

    // The refund takes the prize with it, or the viewer keeps both the pet and the points.
    [Fact]
    public void RevokingTakesBackExactlyTheWinTheRedemptionBought()
    {
        var store = new SpinWinStore(StorePath);
        store.Add(Win("7", "drake") with { RedemptionId = "r1" });
        store.Add(Win("7", "katt") with { RedemptionId = "r2" });
        store.Add(Win("8", "drake"));

        SpinWin? revoked = store.RevokeWin("r1");

        Assert.Equal("drake", revoked!.PetId);
        Assert.False(store.Owns("7", "drake"));
        Assert.True(store.Owns("7", "katt"));
        // Somebody else's copy, and a win from before this was written down, are nobody's refund.
        Assert.True(store.Owns("8", "drake"));
        Assert.Null(store.RevokeWin("r1"));
        Assert.Null(store.RevokeWin(""));
        Assert.False(new SpinWinStore(StorePath).Owns("7", "drake"));
    }

    [Fact]
    public void AnOwedFulfilmentIsClearedWhenTwitchTakesIt()
    {
        var store = new SpinWinStore(StorePath);
        store.Owe(new SpinOwedFulfilment("r1", "snurr", "kanal", "Kajsa", 500, "vinsten är bokförd"));

        Assert.True(store.Settled("r1"));
        Assert.False(store.Settled("r1"));
        Assert.False(new SpinWinStore(StorePath).Owes("r1"));
    }
}

public sealed class WinnablePetCatalogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "spin-pets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private void WritePet(string id, string json)
    {
        string dir = Path.Combine(_folder, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pet.json"), json);
        File.WriteAllBytes(Path.Combine(dir, "spritesheet.webp"), [1, 2, 3]);
    }

    [Fact]
    public void WinOnlyAndRarityAreReadFromTheManifest()
    {
        WritePet("drake", """{ "id": "drake", "displayName": "Gyllene Draken", "winOnly": true, "rarity": "legendarisk" }""");
        var catalog = new PetCatalog(_folder);

        PetDefinition drake = Assert.Single(catalog.Winnable);
        Assert.Equal("drake", drake.Id);
        Assert.Equal(PetRarity.Legendary, drake.Rarity);
        // The shipped pets carry neither field and stay exactly what they were.
        Assert.All(catalog.Pets.Where(pet => pet.IsDefault), pet => Assert.False(pet.WinOnly));
    }

    [Fact]
    public void ChooseNeverHandsOutAWinOnlyPetUninvited()
    {
        WritePet("drake", """{ "id": "drake", "winOnly": true }""");
        var catalog = new PetCatalog(_folder);

        // Asked for by name, set as the channel default – both are ways around the win.
        Assert.NotEqual("drake", catalog.Choose("en drake tack", null).Id);
        Assert.NotEqual("drake", catalog.Choose(null, "drake").Id);

        // The winner's own answer opens the door.
        Assert.Equal("drake", catalog.Choose("en drake tack", null, _ => true).Id);
    }

    [Fact]
    public void SettingWinnableWritesTheManifestWithoutLosingForeignFields()
    {
        WritePet("nova", """{ "id": "nova", "displayName": "Nova", "hatchedBy": "codex" }""");
        var catalog = new PetCatalog(_folder);
        Assert.Empty(catalog.Winnable);

        Assert.True(catalog.TrySetWinnable("nova", winOnly: true, rarity: "sällsynt", out string error), error);

        PetDefinition nova = Assert.Single(catalog.Winnable);
        Assert.Equal(PetRarity.Rare, nova.Rarity);
        // The field this app has no property for is Codex's, and it must ride through the edit.
        string json = File.ReadAllText(Path.Combine(_folder, "nova", "pet.json"));
        Assert.Contains("hatchedBy", json);
        Assert.Contains("codex", json);
    }

    [Fact]
    public void RenamingWritesBothNamesAndLeavesTheFolderAlone()
    {
        WritePet("nova", """{ "id": "nova", "displayName": "Nova", "winOnly": true, "hatchedBy": "codex" }""");
        var catalog = new PetCatalog(_folder);

        Assert.True(catalog.TryRename("nova", "Stjärn-Nova!", "  Stjärnan  ", out string error), error);

        PetDefinition renamed = Assert.Single(catalog.Winnable);
        // The id is cleaned the same way one read from a manifest is, and the name merely trimmed.
        Assert.Equal("stjrn-nova", renamed.Id);
        Assert.Equal("Stjärnan", renamed.Name);
        // The pet is still where it was: the folder name is not what the app goes by.
        Assert.True(Directory.Exists(Path.Combine(_folder, "nova")));
        Assert.Contains("hatchedBy", File.ReadAllText(Path.Combine(_folder, "nova", "pet.json")));
        // And it is reachable under the new name, having lost the old one.
        Assert.Equal("stjrn-nova", catalog.Find("Stjärnan")?.Id);
        Assert.Null(catalog.Find("Nova"));
    }

    [Fact]
    public void ANameOrIdAnotherPetAnswersToIsRefused()
    {
        WritePet("nova", """{ "id": "nova", "displayName": "Nova", "aliases": ["stjärnan"] }""");
        WritePet("drake", """{ "id": "drake", "displayName": "Draken" }""");
        var catalog = new PetCatalog(_folder);

        Assert.False(catalog.TryRename("drake", "nova", "Draken", out string byId));
        Assert.Contains("nova", byId);
        // Aliases count too: two pets answering to one word makes one of them unreachable.
        Assert.False(catalog.TryRename("drake", "drake", "stjärnan", out string byAlias));
        Assert.Contains("stjärnan", byAlias);
        // An empty id is nothing to be reached by at all.
        Assert.False(catalog.TryRename("drake", "!!!", "Draken", out _));

        Assert.Equal("Draken", catalog.Find("drake")?.Name);
    }

    [Fact]
    public void APetKeepingItsIdMayKeepItsName()
    {
        WritePet("nova", """{ "id": "nova", "displayName": "Nova" }""");
        var catalog = new PetCatalog(_folder);

        // Renaming a pet to what it is already called must not read as a clash with itself.
        Assert.True(catalog.TryRename("nova", "nova", "Nova", out string error), error);
    }
}

public sealed class WinOnlyPetSpawnTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "spin-gate-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private (PetService Service, PetRegistry Registry, AppSettings Settings) Build()
    {
        string dir = Path.Combine(_folder, "drake");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pet.json"), """{ "id": "drake", "displayName": "Gyllene Draken", "winOnly": true }""");
        File.WriteAllBytes(Path.Combine(dir, "spritesheet.webp"), [1, 2, 3]);

        var settings = new AppSettings();
        settings.Normalize();
        var registry = new PetRegistry();
        var catalog = new PetCatalog(_folder);
        var hub = new ChatHub(settings, new TwitchBadgeCatalog(), new TwitchSession(new System.Net.Http.HttpClient()), registry, catalog, new NicknameBook());
        return (new PetService(settings, catalog, registry, hub), registry, settings);
    }

    private static RewardRedemption Redemption(string? userInput) =>
        new("r1", "abc", "Pet", 500, "7", "kajsa", "Kajsa", userInput, DateTimeOffset.Now);

    [Fact]
    public void ARefundableRewardPaysBackSomebodyAskingForAPetTheyNeverWon()
    {
        (PetService service, PetRegistry registry, AppSettings settings) = Build();
        settings.Pets.Rewards = [new PetRewardRule { RewardId = "abc", Minutes = 5, Managed = true }];

        PetRedemptionResult result = service.HandleRedemption(Redemption("en gyllene draken tack"));

        Assert.Equal(PetSpawnOutcome.NotOwned, result.Outcome);
        Assert.True(result.Refundable);
        Assert.Equal("drake", result.Asked!.Id);
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public void TheWinnerGetsTheirPetByName()
    {
        (PetService service, PetRegistry registry, _) = Build();
        service.OwnsWonPet = (userId, petId) => userId == "7" && petId == "drake";

        service.HandleRedemption(Redemption("en gyllene draken tack"));

        Assert.Equal("drake", Assert.Single(registry.Snapshot()).Species);
    }

    // The IRC fallback runs only while EventSub is down, and a channel with no pet rules spawns for
    // every reward id it meets. The spin's own reward has to be excluded there as the reading's is,
    // or one purchase becomes an ordinary pet and never reaches the spin at all.
    [Fact]
    public void TheChatFallbackLeavesTheSpinsOwnRewardAlone()
    {
        (PetService service, PetRegistry registry, AppSettings settings) = Build();
        settings.Spin.RewardId = "snurr";
        settings.Spin.RewardName = "Lyckosnurren";
        service.RedemptionsFromEventSub = false;

        service.HandleMessage(new ChatMessage("m1", "Kajsa", "hej", "#9146FF", [], false, false, DateTimeOffset.Now)
        {
            UserId = "7",
            UserLogin = "kajsa",
            RewardId = "snurr",
            RewardTitle = "Lyckosnurren"
        });

        Assert.Empty(registry.Snapshot());
    }

    // Where nothing can be paid back the points are spent either way, so the viewer gets a pet –
    // just never the one that belongs to somebody else's win.
    [Fact]
    public void AnUnrefundableAskFallsBackToAnOrdinaryPet()
    {
        (PetService service, PetRegistry registry, _) = Build();

        PetRedemptionResult result = service.HandleRedemption(Redemption("en gyllene draken tack"));

        Assert.Equal(PetSpawnOutcome.Spawned, result.Outcome);
        Assert.NotEqual("drake", Assert.Single(registry.Snapshot()).Species);
    }
}

public sealed class SpinServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "spin-svc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private void WriteWinnablePet(string id, string displayName, string rarity = "vanlig")
    {
        string dir = Path.Combine(_folder, "pets", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pet.json"),
            $$"""{ "id": "{{id}}", "displayName": "{{displayName}}", "winOnly": true, "rarity": "{{rarity}}" }""");
        File.WriteAllBytes(Path.Combine(dir, "spritesheet.webp"), [1, 2, 3]);
    }

    /// <summary>An ordinary pet: on the lawn for anyone to redeem, and never something the reel draws.</summary>
    private void WriteOrdinaryPet(string id, string displayName)
    {
        string dir = Path.Combine(_folder, "pets", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pet.json"),
            $$"""{ "id": "{{id}}", "displayName": "{{displayName}}" }""");
        File.WriteAllBytes(Path.Combine(dir, "spritesheet.webp"), [1, 2, 3]);
    }

    private sealed record Harness(
        SpinService Spins,
        SpinWinStore Store,
        PetCatalog Catalog,
        PetRegistry Registry,
        AppSettings Settings,
        List<SpinVerdict> Verdicts,
        List<(BotFlow Flow, IReadOnlyDictionary<string, string> Values)> Announced);

    private string StorePath => Path.Combine(_folder, "spinwins.json");

    private Harness Build(bool managed = true, bool botOn = true, int overlays = 1, Func<string, Task<TwitchUser?>>? resolve = null,
        Func<bool>? canGift = null)
    {
        var settings = new AppSettings();
        settings.Spin.RewardId = "snurr";
        settings.Spin.Managed = managed;
        settings.Bot.Mode = botOn ? BotMode.Streamer : BotMode.Off;
        settings.Normalize();

        var registry = new PetRegistry();
        var catalog = new PetCatalog(Path.Combine(_folder, "pets"));
        var hub = new ChatHub(settings, new TwitchBadgeCatalog(), new TwitchSession(new System.Net.Http.HttpClient()), registry, catalog, new NicknameBook());
        var store = new SpinWinStore(StorePath);
        var pets = new PetService(settings, catalog, registry, hub) { OwnsWonPet = store.Owns };
        var spins = new SpinService(settings, catalog, store, pets, hub, resolve, () => overlays, canGift);

        var verdicts = new List<SpinVerdict>();
        spins.Verdict += verdicts.Add;
        var announced = new List<(BotFlow, IReadOnlyDictionary<string, string>)>();
        spins.Announced += (flow, values, _) => announced.Add((flow, values));
        return new Harness(spins, store, catalog, registry, settings, verdicts, announced);
    }

    private static RewardRedemption Redemption(string rewardId = "snurr", string id = "r1", string userId = "7", string name = "Kajsa") =>
        new(id, rewardId, "Lyckosnurren", 500, userId, name.ToLowerInvariant(), name, null, DateTimeOffset.Now);

    private static ChatMessage Message(string text, string userId = "7", string name = "Kajsa") =>
        new("m1", name, text, "#9146FF", [], false, false, DateTimeOffset.Now) { UserId = userId, UserLogin = name.ToLowerInvariant() };

    [Fact]
    public void SomebodyElsesRewardIsNotASpin()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();

        Assert.Equal(SpinOutcome.NotASpinReward, harness.Spins.HandleRedemption(Redemption(rewardId: "annat")).Outcome);
        Assert.Equal(0, harness.Spins.QueueLength);
    }

    [Fact]
    public void TheUnhappyPathsNameTheirReasons()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness disabled = Build();
        disabled.Settings.Spin.Enabled = false;
        Assert.Equal(SpinOutcome.Disabled, disabled.Spins.HandleRedemption(Redemption()).Outcome);

        Harness dark = Build(overlays: 0);
        Assert.Equal(SpinOutcome.NoOverlay, dark.Spins.HandleRedemption(Redemption()).Outcome);
    }

    [Fact]
    public void WithNothingMarkedWinnableTheSpinRefusesInsteadOfInventingAPrize()
    {
        // No pets written: the catalog seeds its shipped pets, none of which are win-only.
        Harness harness = Build();

        Assert.Equal(SpinOutcome.NothingToWin, harness.Spins.HandleRedemption(Redemption()).Outcome);
    }

    [Fact]
    public void AWinIsBookedAndFulfilledBeforeTheReelEvenTurns()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();

        SpinRedemptionResult result = harness.Spins.HandleRedemption(Redemption());

        Assert.Equal(SpinOutcome.Spinning, result.Outcome);
        // The prize is durable and the redemption answered while the animation is still queued.
        Assert.True(harness.Store.Owns("7", "drake"));
        SpinVerdict verdict = Assert.Single(harness.Verdicts);
        Assert.False(verdict.Refund);
        Assert.Equal("r1", verdict.RedemptionId);
        Assert.Empty(harness.Announced);
        Assert.Equal(1, harness.Spins.QueueLength);
    }

    [Fact]
    public void TheCurtainCallAnnouncesTheWinAndPutsThePetOnTheLawn()
    {
        WriteWinnablePet("drake", "Gyllene Draken", PetRarity.Rare);
        Harness harness = Build();
        harness.Spins.HandleRedemption(Redemption());

        harness.Spins.OnSpinShown("fel-id");
        Assert.Empty(harness.Announced);

        harness.Spins.OnSpinShown(harness.Spins.CurrentSpinId!);

        (BotFlow flow, IReadOnlyDictionary<string, string> values) = Assert.Single(harness.Announced);
        Assert.Equal(BotFlow.SpinWin, flow);
        Assert.Equal("Gyllene Draken", values["prize"]);
        Assert.Equal("sällsynt", values["rarity"]);
        Assert.Equal("drake", Assert.Single(harness.Registry.Snapshot()).Species);
        Assert.Equal(0, harness.Spins.QueueLength);
    }

    [Fact]
    public void TheTierAnnouncedIsTheOneTheReelWasDrawnOnEvenIfThePetIsRetieredMeanwhile()
    {
        WriteWinnablePet("drake", "Gyllene Draken", PetRarity.Legendary);
        Harness harness = Build();
        harness.Spins.HandleRedemption(Redemption());

        // The streamer re-tiers the art in the settings window while its reel is still turning. The
        // draw is long since made against the old odds, and that is what the winner is owed the word
        // for – the new tier belongs to the next spin.
        Assert.True(harness.Catalog.TrySetWinnable("drake", winOnly: true, PetRarity.Common, out _));

        harness.Spins.OnSpinShown(harness.Spins.CurrentSpinId!);

        (_, IReadOnlyDictionary<string, string> values) = Assert.Single(harness.Announced);
        Assert.Equal(PetRarity.Legendary, values["rarity"]);
    }

    [Fact]
    public void SpinsQueueAndPlayOneAtATime()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();

        harness.Spins.HandleRedemption(Redemption(id: "r1", userId: "7", name: "Kajsa"));
        harness.Spins.HandleRedemption(Redemption(id: "r2", userId: "8", name: "Pelle"));
        Assert.Equal(2, harness.Spins.QueueLength);

        string first = harness.Spins.CurrentSpinId!;
        harness.Spins.OnSpinShown(first);
        string second = harness.Spins.CurrentSpinId!;
        Assert.NotEqual(first, second);
        harness.Spins.OnSpinShown(second);

        Assert.Equal(0, harness.Spins.QueueLength);
    }

    [Fact]
    public void ADuplicateIsAnsweredLikeAWinAndBecomesAGiftPrompt()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();
        harness.Store.Add(new SpinWin("7", "kajsa", "Kajsa", "drake", DateTimeOffset.UtcNow));

        harness.Spins.HandleRedemption(Redemption());

        // The points are spent the moment the duplicate is drawn: nothing that happens to the gift
        // from here can hand them back, so the redemption is answered as delivered straight away.
        SpinVerdict booked = Assert.Single(harness.Verdicts);
        Assert.False(booked.Refund);
        Assert.True(harness.Store.HoldsPending("r1"));
        Assert.True(harness.Spins.Holds("r1"));

        harness.Spins.OnSpinShown(harness.Spins.CurrentSpinId!);
        (BotFlow flow, IReadOnlyDictionary<string, string> values) = Assert.Single(harness.Announced);
        Assert.Equal(BotFlow.SpinDuplicate, flow);
        Assert.Equal(harness.Settings.Spin.GiveCommand, values["command"]);
        // The duplicate stays off the lawn; its story continues in the gift flow.
        Assert.Empty(harness.Registry.Snapshot());
    }

    // With no bot there is nobody to hold the gift conversation, so the draw dodges what is owned.
    [Fact]
    public void WithoutABotTheDrawSkipsWhatTheViewerAlreadyOwns()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        WriteWinnablePet("katt", "Space-Cat");
        Harness harness = Build(botOn: false);
        harness.Store.Add(new SpinWin("7", "kajsa", "Kajsa", "drake", DateTimeOffset.UtcNow));

        Assert.Equal(SpinOutcome.Spinning, harness.Spins.HandleRedemption(Redemption()).Outcome);
        Assert.True(harness.Store.Owns("7", "katt"));

        // And a completed collection is the one case with nothing left to give.
        Assert.Equal(SpinOutcome.AllOwned, harness.Spins.HandleRedemption(Redemption(id: "r2")).Outcome);
    }

    [Fact]
    public void AGiftLandsBooksAndAnnounces()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build(resolve: _ => Task.FromResult<TwitchUser?>(new TwitchUser("99", "pelle", "Pelle")));
        harness.Store.Add(new SpinWin("7", "kajsa", "Kajsa", "drake", DateTimeOffset.UtcNow));
        harness.Spins.HandleRedemption(Redemption());

        Assert.True(harness.Spins.HandleChatMessage(Message("!ge @Pelle")));

        Assert.True(harness.Store.Owns("99", "drake"));
        Assert.Equal("7", Assert.Single(harness.Store.WinsFor("99")).GiftedFrom);
        Assert.False(harness.Store.HoldsPending("r1"));
        SpinVerdict verdict = Assert.Single(harness.Verdicts);
        Assert.False(verdict.Refund);
        Assert.Contains(harness.Announced, entry => entry.Flow == BotFlow.SpinGifted && entry.Values["target"] == "Pelle");
    }

    // The chance was to name somebody who could take it. Naming somebody who cannot is how that
    // chance is spent – there is no second attempt and nothing comes back.
    [Fact]
    public void AGiftToSomebodyWhoOwnsItAlreadyIsSpent()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build(resolve: _ => Task.FromResult<TwitchUser?>(new TwitchUser("99", "pelle", "Pelle")));
        harness.Store.Add(new SpinWin("7", "kajsa", "Kajsa", "drake", DateTimeOffset.UtcNow));
        harness.Store.Add(new SpinWin("99", "pelle", "Pelle", "drake", DateTimeOffset.UtcNow));
        harness.Spins.HandleRedemption(Redemption());
        harness.Verdicts.Clear();

        harness.Spins.HandleChatMessage(Message("!ge pelle"));

        Assert.False(harness.Store.HoldsPending("r1"));
        Assert.Null(harness.Store.PendingFor("7"));
        Assert.Empty(harness.Verdicts);
        Assert.Contains(harness.Announced, entry => entry.Flow == BotFlow.SpinGiftOwned);

        // And the winner cannot simply try again with a better name.
        Assert.False(harness.Spins.HandleChatMessage(Message("!ge olle")));
    }

    [Fact]
    public void ANameTwitchDoesNotKnowIsAnsweredAndTheGiftWaits()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build(resolve: _ => Task.FromResult<TwitchUser?>(null));
        harness.Store.Add(new SpinWin("7", "kajsa", "Kajsa", "drake", DateTimeOffset.UtcNow));
        harness.Spins.HandleRedemption(Redemption());

        harness.Spins.HandleChatMessage(Message("!ge ingenalls"));

        Assert.True(harness.Store.HoldsPending("r1"));
        Assert.Contains(harness.Announced, entry => entry.Flow == BotFlow.SpinGiftUnknown && entry.Values["target"] == "ingenalls");
    }

    [Fact]
    public void ABareGiveCommandHandsTheInstructionsBack()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();
        harness.Store.Add(new SpinWin("7", "kajsa", "Kajsa", "drake", DateTimeOffset.UtcNow));
        harness.Spins.HandleRedemption(Redemption());

        Assert.True(harness.Spins.HandleChatMessage(Message("!ge")));

        Assert.Contains(harness.Announced, entry => entry.Flow == BotFlow.SpinDuplicate);
        Assert.True(harness.Store.HoldsPending("r1"));
    }

    [Fact]
    public void AReleasedDuplicateWalksOnForItsWinnerAndSpendsTheGift()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();
        harness.Store.Add(new SpinWin("7", "kajsa", "Kajsa", "drake", DateTimeOffset.UtcNow));
        harness.Spins.HandleRedemption(Redemption());

        Assert.True(harness.Spins.HandleChatMessage(Message("!släpp")));

        Assert.Equal("drake", Assert.Single(harness.Registry.Snapshot()).Species);
        Assert.False(harness.Store.HoldsPending("r1"));
        Assert.Contains(harness.Announced, entry => entry.Flow == BotFlow.SpinReleased && entry.Values["prize"] == "Gyllene Draken");
        // Released is placed: there is no gift left to hand to anybody.
        Assert.False(harness.Spins.HandleChatMessage(Message("!ge pelle")));
    }

    [Fact]
    public void TheReleaseCommandWithoutAnOpenGiftIsLeftForOthers()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();

        Assert.False(harness.Spins.HandleChatMessage(Message("!släpp")));
        Assert.Empty(harness.Registry.Snapshot());
    }

    // Somebody with nothing to give may be using a command the streamer defined for something else.
    [Fact]
    public void TheGiveCommandWithoutAnOpenGiftIsLeftForOthers()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();

        Assert.False(harness.Spins.HandleChatMessage(Message("!ge pelle")));
        Assert.Empty(harness.Announced);
    }

    // Off means off: the streamer's own command by that name gets to answer instead.
    [Fact]
    public void TheCommandsGoQuietWhenTheSpinIsSwitchedOff()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();
        harness.Store.Add(new SpinWin("7", "kajsa", "Kajsa", "drake", DateTimeOffset.UtcNow));
        harness.Settings.Spin.Enabled = false;

        Assert.False(harness.Spins.HandleChatMessage(Message("!mina")));
        Assert.Empty(harness.Announced);
    }

    [Fact]
    public void TheListCommandAnswersWithTheCollectionOrItsAbsence()
    {
        WriteWinnablePet("drake", "Gyllene Draken", PetRarity.Legendary);
        Harness harness = Build();

        Assert.True(harness.Spins.HandleChatMessage(Message("!mina")));
        Assert.Equal(BotFlow.SpinListEmpty, Assert.Single(harness.Announced).Flow);

        harness.Announced.Clear();
        harness.Store.Add(new SpinWin("7", "kajsa", "Kajsa", "drake", DateTimeOffset.UtcNow));
        Assert.True(harness.Spins.HandleChatMessage(Message("!mina")));

        // The tier travels with every art, so a collection reads for what is rare in it.
        (BotFlow flow, IReadOnlyDictionary<string, string> values) = Assert.Single(harness.Announced);
        Assert.Equal(BotFlow.SpinList, flow);
        Assert.Equal("Gyllene Draken (legendarisk)", values["list"]);
        Assert.Equal("1", values["count"]);
    }

    [Fact]
    public void ThePoolCommandListsEveryWinnableArtUnderItsTier()
    {
        WriteWinnablePet("drake", "Gyllene Draken", PetRarity.Legendary);
        WriteWinnablePet("katt", "Space-Cat");
        WriteWinnablePet("rav", "Räven", PetRarity.Uncommon);
        // Not marked winnable, so it is not something the reel can land on and not something to list.
        WriteOrdinaryPet("hund", "Vovven");
        Harness harness = Build();

        Assert.True(harness.Spins.HandleChatMessage(Message("!alla")));

        // Rarest first, and gathered under the tier rather than repeated art by art: a list that has
        // to be cut loses the commons at the end, not the legendary somebody asked in order to see.
        (BotFlow flow, IReadOnlyDictionary<string, string> values) = Assert.Single(harness.Announced);
        Assert.Equal(BotFlow.SpinPool, flow);
        Assert.Equal("legendarisk: Gyllene Draken · ovanlig: Räven · vanlig: Space-Cat", values["list"]);
        Assert.Equal("3", values["count"]);
    }

    [Fact]
    public void ThePoolCommandSaysSoWhenNothingIsMarkedWinnable()
    {
        WriteOrdinaryPet("hund", "Vovven");
        Harness harness = Build();

        Assert.True(harness.Spins.HandleChatMessage(Message("!alla")));
        Assert.Equal(BotFlow.SpinPoolEmpty, Assert.Single(harness.Announced).Flow);
    }

    [Fact]
    public void AGiftNobodyClaimedRunsOutAndIsSaidOnce()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();
        harness.Store.AddPending(new SpinPendingGift("r9", "7", "Kajsa", "drake", DateTimeOffset.UtcNow.AddMinutes(-1)));

        harness.Spins.Tick();
        harness.Spins.Tick();

        // Nothing to answer – the redemption was settled when the duplicate was drawn – and nothing
        // to hand back. All the timeout owes the winner is the word that their chance is over.
        (BotFlow flow, IReadOnlyDictionary<string, string> values) = Assert.Single(harness.Announced);
        Assert.Equal(BotFlow.SpinGiftExpired, flow);
        Assert.Equal("Gyllene Draken", values["prize"]);
        Assert.Equal("Kajsa", values["viewer"]);
        Assert.Empty(harness.Verdicts);
        Assert.False(harness.Store.HoldsPending("r9"));
    }

    [Fact]
    public void ARefundMadeInTwitchsOwnQueueClosesTheGift()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();
        harness.Store.Add(new SpinWin("7", "kajsa", "Kajsa", "drake", DateTimeOffset.UtcNow));
        harness.Spins.HandleRedemption(Redemption());
        Assert.True(harness.Store.HoldsPending("r1"));

        harness.Spins.HandleExternalUpdate("r1", "CANCELED");

        Assert.False(harness.Store.HoldsPending("r1"));
    }

    // The reel plays onto a hidden lawn and SpawnDirect refuses, so the prize would never walk on.
    [Fact]
    public void PetsSwitchedOffStopsTheSpinBeforeAnythingIsSpent()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();
        harness.Settings.Pets.Enabled = false;

        SpinRedemptionResult result = harness.Spins.HandleRedemption(Redemption());

        Assert.Equal(SpinOutcome.PetsOff, result.Outcome);
        Assert.True(result.Refundable);
        Assert.Equal(0, harness.Spins.QueueLength);
        Assert.Empty(harness.Store.All());
        Assert.Empty(harness.Verdicts);
    }

    // A bot that is switched on but cannot write is a gift prompt nobody would ever read, so the
    // draw dodges what the viewer owns exactly as it does with no bot at all.
    [Fact]
    public void ABotThatCannotWriteIsNoBotAsFarAsGiftsAreConcerned()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        WriteWinnablePet("katt", "Space-Cat");
        Harness harness = Build(canGift: () => false);
        harness.Store.Add(new SpinWin("7", "kajsa", "Kajsa", "drake", DateTimeOffset.UtcNow));

        Assert.Equal(SpinOutcome.Spinning, harness.Spins.HandleRedemption(Redemption()).Outcome);

        Assert.True(harness.Store.Owns("7", "katt"));
        Assert.Null(harness.Store.PendingFor("7"));
    }

    [Fact]
    public void SpinningAgainEndsTheGiftTheNewOnePushedOut()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();
        harness.Store.Add(new SpinWin("7", "kajsa", "Kajsa", "drake", DateTimeOffset.UtcNow));

        harness.Spins.HandleRedemption(Redemption(id: "r1"));
        harness.Spins.HandleRedemption(Redemption(id: "r2"));

        // The second gift is the live one. The first is over, and said so – but its points were
        // answered when it was drawn, so neither spin is paid back.
        Assert.Equal("r2", harness.Store.PendingFor("7")!.RedemptionId);
        Assert.Equal(2, harness.Verdicts.Count);
        Assert.DoesNotContain(harness.Verdicts, verdict => verdict.Refund);
        Assert.Contains(harness.Announced, entry => entry.Flow == BotFlow.SpinGiftExpired);
        Assert.False(harness.Store.HoldsPending("r1"));
    }

    // The one the sweep must never touch: the pet is on disk, so paying the redemption back would
    // hand the viewer their points on top of a prize they keep.
    [Fact]
    public void AWinWhoseFulfilmentTwitchNeverTookIsHeldAndReplayedAfterARestart()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();
        harness.Spins.HandleRedemption(Redemption());

        Assert.Single(harness.Verdicts);
        Assert.True(harness.Spins.Holds("r1"));

        // The app closed before Twitch took it. A fresh service over the same file still owes it.
        Harness restarted = Build();
        Assert.True(restarted.Spins.Holds("r1"));
        restarted.Spins.ResumeOwed();

        SpinVerdict replayed = Assert.Single(restarted.Verdicts);
        Assert.Equal("r1", replayed.RedemptionId);
        Assert.False(replayed.Refund);

        // And once Twitch has taken it the debt is gone, in this session and the next.
        restarted.Spins.Settled("r1");
        Assert.False(restarted.Spins.Holds("r1"));
        Assert.False(Build().Spins.Holds("r1"));
    }

    [Fact]
    public void LeavingTheChannelLetsTheOpenGiftsGoWithTheReels()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();
        harness.Store.Add(new SpinWin("7", "kajsa", "Kajsa", "drake", DateTimeOffset.UtcNow));
        harness.Spins.HandleRedemption(Redemption());
        // The duplicate answered its own redemption as it was drawn; what is left is the chance.
        harness.Verdicts.Clear();

        harness.Spins.LeaveChannel();

        // Unanswered on purpose: the redemption belongs to the channel we have left.
        Assert.Empty(harness.Verdicts);
        // The gift goes, but the fulfilment the spin already owes Twitch does not: that debt belongs
        // to the channel it was run up in and waits there for the app to come back.
        Assert.False(harness.Store.HoldsPending("r1"));
        Assert.True(harness.Store.Owes("r1"));
        Assert.Equal(0, harness.Spins.QueueLength);
        Assert.False(harness.Spins.HandleChatMessage(Message("!ge pelle")));
        // The win itself is the viewers' property and stays where it is.
        Assert.True(harness.Store.Owns("7", "drake"));

        // Nothing is left for the timer to pay back in the wrong room either.
        harness.Spins.Tick();
        Assert.Empty(harness.Verdicts);
    }

    // A shutdown is not a channel switch: the winner is still choosing, and their gift has to be
    // there when the app comes back.
    [Fact]
    public void ClosingTheAppLeavesTheOpenGiftOnDisk()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();
        harness.Store.Add(new SpinWin("7", "kajsa", "Kajsa", "drake", DateTimeOffset.UtcNow));
        harness.Spins.HandleRedemption(Redemption());

        harness.Spins.Reset();

        Assert.Equal(0, harness.Spins.QueueLength);
        Assert.True(new SpinWinStore(StorePath).HoldsPending("r1"));
    }

    [Fact]
    public void AGiftWhoseRedemptionIsClosedInTwitchsOwnQueueIsStillClaimable()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();
        harness.Store.Add(new SpinWin("7", "kajsa", "Kajsa", "drake", DateTimeOffset.UtcNow));
        harness.Spins.HandleRedemption(Redemption());
        harness.Verdicts.Clear();

        // Our own verdict coming back, or the streamer closing it by hand. Either way the chance is
        // untouched: it never held the points, and the winner has done nothing wrong.
        harness.Spins.HandleExternalUpdate("r1", "FULFILLED");

        Assert.True(harness.Store.HoldsPending("r1"));
        // Nothing is owed on it any more, but the open gift still keeps the sweep off it.
        Assert.True(harness.Spins.Holds("r1"));
        harness.Store.AddPending(harness.Store.PendingFor("7")! with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        harness.Spins.Tick();
        Assert.Empty(harness.Verdicts);
        Assert.False(harness.Store.HoldsPending("r1"));
    }

    // The one a refund must not leave behind: the pet is already booked, so paying the points back
    // in Twitch's own queue while the reel is still turning would hand the viewer both.
    [Fact]
    public void ARefundInTwitchsOwnQueueTakesTheWinBackAndSilencesTheReel()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();
        harness.Spins.HandleRedemption(Redemption());
        Assert.True(harness.Store.Owns("7", "drake"));

        harness.Spins.HandleExternalUpdate("r1", "CANCELED");

        Assert.False(harness.Store.Owns("7", "drake"));
        // The debt goes with it: there is nothing left to tell Twitch about a closed redemption.
        Assert.False(harness.Spins.Holds("r1"));

        // The reel was already on screen and plays out, but its curtain call has nothing to give.
        harness.Spins.OnSpinShown(harness.Spins.CurrentSpinId!);
        Assert.Empty(harness.Announced);
        Assert.Empty(harness.Registry.Snapshot());
        Assert.Equal(0, harness.Spins.QueueLength);
    }

    [Fact]
    public void ARefundedSpinStillWaitingItsTurnNeverPlays()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        WriteWinnablePet("katt", "Space-Cat");
        Harness harness = Build();
        harness.Spins.HandleRedemption(Redemption(id: "r1", userId: "7", name: "Kajsa"));
        harness.Spins.HandleRedemption(Redemption(id: "r2", userId: "8", name: "Pelle"));
        Assert.Equal(2, harness.Spins.QueueLength);

        harness.Spins.HandleExternalUpdate("r2", "CANCELED");

        // Dropped outright rather than played to a winner who has their points back.
        Assert.Equal(1, harness.Spins.QueueLength);
        Assert.Empty(harness.Store.WinsFor("8"));
        harness.Spins.OnSpinShown(harness.Spins.CurrentSpinId!);
        Assert.Equal("Kajsa", Assert.Single(harness.Announced).Values["viewer"]);
        Assert.Equal(0, harness.Spins.QueueLength);
    }

    // A duplicate books no win, so a refund of its gift must leave the collection alone.
    [Fact]
    public void ARefundedGiftDoesNotTakeTheWinThatCausedIt()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();
        harness.Store.Add(new SpinWin("7", "kajsa", "Kajsa", "drake", DateTimeOffset.UtcNow));
        harness.Spins.HandleRedemption(Redemption());

        harness.Spins.HandleExternalUpdate("r1", "CANCELED");

        Assert.False(harness.Store.HoldsPending("r1"));
        Assert.True(harness.Store.Owns("7", "drake"));
    }

    [Fact]
    public void ATestSpinBooksNothingAndTellsNobody()
    {
        WriteWinnablePet("drake", "Gyllene Draken");
        Harness harness = Build();

        harness.Spins.SpinTest();
        harness.Spins.OnSpinShown(harness.Spins.CurrentSpinId!);

        Assert.Empty(harness.Verdicts);
        Assert.Empty(harness.Announced);
        Assert.Empty(harness.Store.All());
        // The rehearsal still walks onto the lawn, so the streamer sees the whole act.
        Assert.Single(harness.Registry.Snapshot());
    }
}
