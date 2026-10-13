namespace TwitchOverlayHelper.Pets;

/// <summary>One live pet. Timestamps are unix milliseconds so the overlay can compare directly.</summary>
public sealed record PetState(string Id, string Name, string? Color, string Species, long SpawnedAt, long ExpiresAt);

/// <summary>Outcome of a spawn: the pet as it now stands, and the pet that was evicted to fit it.</summary>
public sealed record PetSpawnResult(PetState Pet, string? RemovedId, bool Extended);

/// <summary>
/// The pets currently on screen. Kept on the server so an overlay that reloads (OBS restart, scene
/// change) gets its pets back.
/// </summary>
public sealed class PetRegistry
{
    private readonly Lock _lock = new();
    private readonly List<PetState> _pets = new();

    /// <summary>Live pets, oldest first. Also removes the expired ones.</summary>
    public IReadOnlyList<PetState> Snapshot()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_lock)
        {
            _pets.RemoveAll(pet => pet.ExpiresAt <= now);
            return _pets.ToArray();
        }
    }

    /// <summary>
    /// Spawns a pet, or when the same viewer redeems again extends the one they have, changing it
    /// to the newly named species.
    /// </summary>
    /// <param name="evictWhenFull">
    /// What a full lawn means. True sends the oldest pet home to make room; false refuses the
    /// spawn, which is what a reward that can pay back asks for.
    /// </param>
    /// <returns>The spawn, or null when the lawn was full and eviction was not allowed.</returns>
    public PetSpawnResult? Spawn(string id, string name, string? color, string species, TimeSpan lifetime, int maxPets, bool evictWhenFull = true)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long expires = now + (long)lifetime.TotalMilliseconds;
        lock (_lock)
        {
            _pets.RemoveAll(pet => pet.ExpiresAt <= now);

            int existing = _pets.FindIndex(pet => string.Equals(pet.Id, id, StringComparison.Ordinal));
            if (existing >= 0)
            {
                PetState extended = _pets[existing] with { Name = name, Color = color, Species = species, ExpiresAt = Math.Max(_pets[existing].ExpiresAt, expires) };
                _pets[existing] = extended;
                return new PetSpawnResult(extended, null, Extended: true);
            }

            string? removedId = null;
            if (_pets.Count >= Math.Max(1, maxPets))
            {
                if (!evictWhenFull) return null;
                removedId = _pets[0].Id;
                _pets.RemoveAt(0);
            }

            var pet = new PetState(id, name, color, species, now, expires);
            _pets.Add(pet);
            return new PetSpawnResult(pet, removedId, Extended: false);
        }
    }

    /// <summary>
    /// Takes one pet off the lawn early, as when its redemption is paid back. Returns whether there
    /// was one to take.
    /// </summary>
    public bool Remove(string id)
    {
        lock (_lock) return _pets.RemoveAll(pet => string.Equals(pet.Id, id, StringComparison.Ordinal)) > 0;
    }

    public void Clear()
    {
        lock (_lock) _pets.Clear();
    }
}
