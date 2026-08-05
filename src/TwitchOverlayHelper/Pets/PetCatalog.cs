using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchOverlayHelper.Pets;

/// <summary>
/// One pet species viewers can ask for by name. Every pet is a folder on disk: an SVG the overlay
/// draws, or a spritesheet in the hatch-pet format.
/// </summary>
public sealed record PetDefinition(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Emoji,
    bool IsDefault,
    string? BodyFile = null,
    string? SpriteFile = null,
    double Fps = 10,
    int SpriteVersion = 1);

/// <summary>
/// Every pet species the overlay can show, read from the user's pets folder. The pets that ship
/// with the app are written there on first start rather than kept in code, so a streamer can
/// recolour Blaze or give Owly a new name with a text editor. The folder layout is Codex's
/// hatch-pet layout – <c>&lt;pet-id&gt;/pet.json</c> plus a drawing – so a pet hatched there can be
/// dropped in unchanged.
/// </summary>
public sealed class PetCatalog
{
    /// <summary>Pratbubblor for a pet whose manifest names none.</summary>
    private static readonly IReadOnlyList<string> DefaultEmoji = ["✨", "💬"];

    /// <summary>Last resort when even the shipped pets are unreadable, so a spawn never crashes.</summary>
    private static readonly PetDefinition Placeholder = new("robo", "Robo", string.Empty, [], DefaultEmoji, IsDefault: true);

    /// <summary>Records which shipped pets have been written out, so a deleted pet stays deleted.</summary>
    private const string SeedMarkerFile = ".defaults";

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly Lock _lock = new();
    private IReadOnlyList<PetDefinition> _pets;
    private Dictionary<string, PetDefinition> _byAlias;
    private IReadOnlyList<string> _warnings = [];

    public PetCatalog(string? petsFolder = null)
    {
        PetsFolder = petsFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TwitchOverlayHelper", "pets");
        (_pets, _byAlias, _warnings) = Load();
    }

    /// <summary>Where every pet lives, the shipped ones included. Seeded on first read.</summary>
    public string PetsFolder { get; }

    public IReadOnlyList<PetDefinition> Pets { get { lock (_lock) return _pets; } }

    /// <summary>Human-readable notes about pets that could not be loaded.</summary>
    public IReadOnlyList<string> Warnings { get { lock (_lock) return _warnings; } }

    /// <summary>Re-reads the folder, so an edited or newly dropped-in pet shows up without a restart.</summary>
    public void Reload()
    {
        (IReadOnlyList<PetDefinition> pets, Dictionary<string, PetDefinition> byAlias, IReadOnlyList<string> warnings) = Load();
        lock (_lock)
        {
            _pets = pets;
            _byAlias = byAlias;
            _warnings = warnings;
        }
    }

    /// <summary>Looks up one species by id, display name or alias.</summary>
    public PetDefinition? Find(string? nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId)) return null;
        lock (_lock) return _byAlias.GetValueOrDefault(nameOrId.Trim());
    }

    /// <summary>
    /// Finds the species named somewhere in a redemption message. The viewer writes free text
    /// ("en blaze tack!"), so every name is looked for inside the line rather than compared to it.
    /// Names are matched whole, which is what lets a "space-cat" or a "Rostiga Rolf" be asked for.
    /// </summary>
    public PetDefinition? ResolveFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        lock (_lock)
        {
            PetDefinition? best = null;
            int bestAt = int.MaxValue;
            int bestLength = 0;
            foreach ((string alias, PetDefinition pet) in _byAlias)
            {
                int at = IndexOfWord(text, alias);
                // The first species the viewer names is the one they get; where two names start in
                // the same place the longer one wins, so "space cat" beats a plain "space".
                if (at < 0 || at > bestAt || (at == bestAt && alias.Length <= bestLength)) continue;
                (best, bestAt, bestLength) = (pet, at, alias.Length);
            }
            return best;
        }
    }

    /// <summary>
    /// The full pick order for a spawn: the species the viewer asked for, else the streamer's
    /// default, else a random one so nobody ever gets an empty hand.
    /// </summary>
    public PetDefinition Choose(string? text, string? defaultPetId)
    {
        PetDefinition? chosen = ResolveFromText(text) ?? Find(defaultPetId);
        if (chosen is not null) return chosen;
        IReadOnlyList<PetDefinition> pets = Pets;
        return pets.Count > 0 ? pets[Random.Shared.Next(pets.Count)] : Placeholder;
    }

    /// <summary>Absolute spritesheet path for a spritesheet pet, for the server's sprite endpoint.</summary>
    public bool TryGetSpriteFile(string id, out string path)
    {
        path = string.Empty;
        PetDefinition? pet = Find(id);
        if (pet?.SpriteFile is not { Length: > 0 } file || !File.Exists(file)) return false;
        path = file;
        return true;
    }

    /// <summary>
    /// The drawing for a pet, as the overlay asks for it. Read on every request rather than cached,
    /// so "Ladda om pets" after an edit is enough to see the change.
    /// </summary>
    public bool TryGetBody(string id, out string svg)
    {
        svg = string.Empty;
        PetDefinition? pet = Find(id);
        if (pet is null || pet.SpriteFile is { Length: > 0 }) return false;

        if (pet.BodyFile is { Length: > 0 } file)
        {
            try
            {
                svg = File.ReadAllText(file);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* fall back below */ }
        }

        // The folder could not be written or was pulled out from under us; the copy inside the exe
        // keeps the overlay from showing an empty patch of ground.
        svg = PetDefaults.Body(pet.Id);
        return svg.Length > 0;
    }

    /// <summary>
    /// Where <paramref name="word"/> stands on its own in the text, or -1. Matching whole words is
    /// what keeps "blazer" from spawning a Blaze, while still finding one in "en BLAZE, tack!".
    /// </summary>
    private static int IndexOfWord(string text, string word)
    {
        if (word.Length == 0) return -1;
        for (int from = 0; from <= text.Length - word.Length;)
        {
            int at = text.IndexOf(word, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return -1;
            if (IsEdge(text, at - 1) && IsEdge(text, at + word.Length)) return at;
            from = at + 1;
        }
        return -1;
    }

    /// <summary>Outside the text, or a character no name can run into: punctuation, space, emoji.</summary>
    private static bool IsEdge(string text, int index) =>
        index < 0 || index >= text.Length || !char.IsLetterOrDigit(text[index]);

    private (IReadOnlyList<PetDefinition>, Dictionary<string, PetDefinition>, IReadOnlyList<string>) Load()
    {
        var warnings = new List<string>();
        Seed(warnings);

        var pets = LoadFolder(warnings).ToList();
        if (pets.Count == 0) pets.AddRange(Shipped());

        // The pets that ship with the app keep first claim on their names, so a pet added later
        // cannot quietly take over the "robo" every viewer already knows.
        pets = pets.OrderByDescending(pet => pet.IsDefault).ThenBy(pet => pet.Id, StringComparer.OrdinalIgnoreCase).ToList();

        var byAlias = new Dictionary<string, PetDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (PetDefinition pet in pets)
        {
            byAlias.TryAdd(pet.Id, pet);
            byAlias.TryAdd(pet.Name, pet);
            foreach (string alias in pet.Aliases) byAlias.TryAdd(alias, pet);
        }
        return (pets, byAlias, warnings);
    }

    /// <summary>
    /// Writes the shipped pets to the folder, once each. The marker file is what makes it once: a
    /// pet the streamer deleted on purpose should not come back at the next start.
    /// </summary>
    private void Seed(List<string> warnings)
    {
        try
        {
            Directory.CreateDirectory(PetsFolder);

            string marker = Path.Combine(PetsFolder, SeedMarkerFile);
            var seeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(marker))
                foreach (string line in File.ReadAllLines(marker))
                    if (line.Trim() is { Length: > 0 } id) seeded.Add(id);

            var written = new List<string>();
            try
            {
                foreach (PetSeed seed in PetDefaults.All)
                {
                    if (!seeded.Add(seed.Id)) continue;
                    string folder = Path.Combine(PetsFolder, seed.Id);
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, "pet.json"), seed.Manifest);
                    File.WriteAllText(Path.Combine(folder, "body.svg"), seed.Body);
                    written.Add(seed.Id);
                }
            }
            finally
            {
                // Written before anything is allowed to go wrong further down the list: a pet that
                // did reach the disk must be marked, or the next start writes over the edits the
                // streamer has made to it in the meantime.
                if (written.Count > 0) File.AppendAllLines(marker, written);
            }

            string readme = Path.Combine(PetsFolder, "LÄS MIG.txt");
            if (!File.Exists(readme)) File.WriteAllText(readme, PetDefaults.Readme);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            warnings.Add($"Kunde inte skriva pets till {PetsFolder} ({ex.Message}). Standardpetsen används från appen så länge.");
        }
    }

    private IEnumerable<PetDefinition> LoadFolder(List<string> warnings)
    {
        if (!Directory.Exists(PetsFolder)) yield break;

        string[] folders;
        try { folders = Directory.GetDirectories(PetsFolder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        foreach (string folder in folders.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            PetDefinition? pet = LoadOne(folder, warnings);
            if (pet is not null) yield return pet;
        }
    }

    /// <summary>The shipped pets, straight from the exe, for when the folder is unusable.</summary>
    private static IEnumerable<PetDefinition> Shipped()
    {
        foreach (PetSeed seed in PetDefaults.All)
        {
            PetManifest? manifest;
            try { manifest = JsonSerializer.Deserialize<PetManifest>(seed.Manifest, ManifestJson); }
            catch (JsonException) { continue; }
            if (manifest is not null) yield return Describe(manifest, seed.Id, isDefault: true, body: null, sprite: null);
        }
    }

    /// <summary>Reads one pet folder. A broken pet becomes a warning, never a crash.</summary>
    private static PetDefinition? LoadOne(string folder, List<string> warnings)
    {
        string folderName = Path.GetFileName(folder);
        string manifestPath = Path.Combine(folder, "pet.json");
        if (!File.Exists(manifestPath)) return null;

        PetManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<PetManifest>(File.ReadAllText(manifestPath), ManifestJson); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{folderName}: pet.json gick inte att läsa ({ex.Message}).");
            return null;
        }
        if (manifest is null)
        {
            warnings.Add($"{folderName}: pet.json är tom.");
            return null;
        }

        string id = SanitizeId(manifest.Id is { Length: > 0 } ? manifest.Id : folderName);
        if (id.Length == 0)
        {
            warnings.Add($"{folderName}: id saknas.");
            return null;
        }

        // A spritesheet wins over a drawing, so a hatch-pet folder behaves exactly as it did before
        // pets were SVG files at all.
        string? sprite = Resolve(folder, manifest.SpritesheetPath ?? "spritesheet.webp", folderName, "spritesheetPath", warnings);
        string? body = sprite is null ? Resolve(folder, manifest.BodyPath ?? "body.svg", folderName, "bodyPath", warnings) : null;
        if (sprite is null && body is null)
        {
            warnings.Add($"{folderName}: hittar varken body.svg eller spritesheet.webp.");
            return null;
        }

        return Describe(manifest, id, PetDefaults.IsDefault(id), body, sprite);
    }

    /// <summary>
    /// Turns a path out of pet.json into an absolute file inside the pet's own folder. The id ends
    /// up in a URL, so the manifest must not be able to point the server at an arbitrary file.
    /// </summary>
    private static string? Resolve(string folder, string relative, string folderName, string field, List<string> warnings)
    {
        string full;
        try { full = Path.GetFullPath(Path.Combine(folder, relative)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            warnings.Add($"{folderName}: {field} går inte att tolka.");
            return null;
        }

        if (!full.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"{folderName}: {field} pekar utanför petens mapp.");
            return null;
        }
        return File.Exists(full) ? full : null;
    }

    private static PetDefinition Describe(PetManifest manifest, string id, bool isDefault, string? body, string? sprite) => new(
        id,
        manifest.DisplayName is { Length: > 0 } ? manifest.DisplayName.Trim() : id,
        manifest.Description?.Trim() ?? string.Empty,
        Clean(manifest.Aliases),
        Clean(manifest.Emoji) is { Count: > 0 } emoji ? emoji : DefaultEmoji,
        isDefault,
        body,
        sprite,
        manifest.Fps is > 0 and <= 30 ? manifest.Fps.Value : 10,
        // Version 2 is the extended sheet with the two look-direction rows. Anything newer is
        // read as a 2: later versions extend the sheet downwards, so the rows a 2 knows are safe.
        manifest.SpriteVersionNumber is >= 2 ? 2 : 1);

    private static IReadOnlyList<string> Clean(string[]? values) =>
        (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray();

    /// <summary>The id is used in URLs and DOM lookups, so anything but simple characters is dropped.</summary>
    private static string SanitizeId(string raw) =>
        new(raw.Trim().ToLowerInvariant().Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').ToArray());

    /// <summary>The subset of Codex's pet.json this app reads; unknown fields are ignored.</summary>
    private sealed record PetManifest(
        string? Id,
        string? DisplayName,
        string? Description,
        [property: JsonPropertyName("spritesheetPath")] string? SpritesheetPath,
        [property: JsonPropertyName("bodyPath")] string? BodyPath,
        string[]? Aliases,
        string[]? Emoji,
        double? Fps,
        [property: JsonPropertyName("spriteVersionNumber")] int? SpriteVersionNumber);
}
