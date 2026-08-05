using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Web;

namespace TwitchOverlayHelper.Pets;

/// <summary>
/// Decides when a chat line earns a pet in the overlay, and which species it becomes. Redemptions
/// are the real trigger; the "!pet" command exists so the streamer can rehearse without spending
/// anyone's channel points.
/// </summary>
public sealed class PetService(AppSettings settings, PetCatalog catalog, PetRegistry registry, ChatHub hub)
{
    private int _testCounter;

    public void HandleMessage(ChatMessage message)
    {
        PetSettings pets = settings.Pets;
        if (!pets.Enabled) return;

        // Which reward was redeemed decides how long the pet stays, so a channel can sell five and
        // ten minutes as separate rewards.
        int? minutes = message.RewardId is { Length: > 0 } ? pets.LifetimeMinutesFor(message.RewardId) : null;

        string trimmed = message.Text.Trim();
        bool testCommand = pets.AllowModTestCommand
            && (message.IsBroadcaster || message.IsModerator)
            && (string.Equals(trimmed, "!pet", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("!pet ", StringComparison.OrdinalIgnoreCase));
        if (minutes is null && !testCommand) return;

        // The user id keys the pet so re-redeeming extends rather than duplicates; the login is
        // the fallback for locally generated messages that never saw Twitch.
        string id = message.UserId.Length > 0 ? message.UserId : message.UserLogin;
        if (id.Length == 0) return;

        // "!pet boo" should not spawn whatever species happens to be called from the rest of the
        // sentence, so only the words after the command are searched.
        string wishText = minutes is null ? trimmed["!pet".Length..] : message.Text;
        Spawn(id, message.DisplayName, message.NameColor, wishText, minutes ?? pets.LifetimeMinutes);
    }

    /// <summary>Spawns a throwaway pet from the app's test button, cycling species so each click shows a new one.</summary>
    public void SpawnTest()
    {
        int number = Interlocked.Increment(ref _testCounter);
        IReadOnlyList<PetDefinition> species = catalog.Pets;
        PetDefinition chosen = species[(number - 1) % species.Count];
        // The label normally carries the name of the viewer who redeemed. On a test spawn it carries
        // the species instead, so the streamer can tell which pet they are looking at.
        Spawn($"test-{number}", $"{chosen.Name} (test)", null, chosen.Id, settings.Pets.LifetimeMinutes);
    }

    private void Spawn(string id, string name, string? color, string? wishText, int minutes)
    {
        PetSettings pets = settings.Pets;
        PetDefinition species = catalog.Choose(wishText, pets.DefaultPet);
        PetSpawnResult result = registry.Spawn(id, name, color, species.Id, TimeSpan.FromMinutes(minutes), pets.MaxPets);
        hub.PublishPetSpawn(result);
    }
}
