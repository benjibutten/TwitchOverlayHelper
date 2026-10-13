using System.IO;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json;
using TwitchOverlayHelper.Storage;

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
    int SpriteVersion = 1,
    bool WinOnly = false,
    string Rarity = PetRarity.Common);

/// <summary>
/// Every pet species the overlay can show, read from the user's pets folder. The pets that ship
/// with the app are written there on first start, so the streamer can edit them. The folder layout
/// is Codex's hatch-pet layout – <c>&lt;pet-id&gt;/pet.json</c> plus a drawing – so a pet hatched
/// there can be dropped in unchanged.
/// </summary>
public sealed class PetCatalog
{
    /// <summary>Speech-bubble emoji for a pet whose manifest names none.</summary>
    private static readonly IReadOnlyList<string> DefaultEmoji = ["✨", "💬"];

    /// <summary>Last resort when even the shipped pets are unreadable.</summary>
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
    private Dictionary<string, string> _manifestPaths = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _warnings = [];

    public PetCatalog(string? petsFolder = null)
    {
        PetsFolder = petsFolder ?? ProfilePaths.Folder("pets");
        (_pets, _byAlias, _manifestPaths, _warnings) = Load();
    }

    /// <summary>Where every pet lives, the shipped ones included. Seeded on first read.</summary>
    public string PetsFolder { get; }

    public IReadOnlyList<PetDefinition> Pets { get { lock (_lock) return _pets; } }

    /// <summary>Human-readable notes about pets that could not be loaded.</summary>
    public IReadOnlyList<string> Warnings { get { lock (_lock) return _warnings; } }

    /// <summary>Re-reads the folder.</summary>
    public void Reload()
    {
        (IReadOnlyList<PetDefinition> pets, Dictionary<string, PetDefinition> byAlias,
            Dictionary<string, string> manifestPaths, IReadOnlyList<string> warnings) = Load();
        lock (_lock)
        {
            _pets = pets;
            _byAlias = byAlias;
            _manifestPaths = manifestPaths;
            _warnings = warnings;
        }
    }

    /// <summary>The pets the lucky spin can hand out.</summary>
    public IReadOnlyList<PetDefinition> Winnable => Pets.Where(pet => pet.WinOnly).ToArray();

    /// <summary>
    /// Writes a pet's spin settings into its own pet.json and reloads the catalog. Returns false
    /// with <paramref name="error"/> set when the file cannot be edited.
    /// </summary>
    public bool TrySetWinnable(string id, bool winOnly, string rarity, out string error) =>
        TryEditManifest(id, manifest =>
        {
            manifest["winOnly"] = winOnly;
            manifest["rarity"] = PetRarity.Normalize(rarity);
        }, out error);

    /// <summary>
    /// Gives a pet a new id and a new display name in its own pet.json, and reloads the catalog. The
    /// folder keeps its name. Wins are booked against the id, so a caller that changes it has to
    /// move them along (<c>SpinWinStore.RenamePet</c>).
    ///
    /// <para>Refused when the new id or name already belongs to another pet, alias included.</para>
    /// </summary>
    public bool TryRename(string id, string newId, string newName, out string error)
    {
        error = string.Empty;
        newId = SanitizeId(newId);
        newName = (newName ?? string.Empty).Trim();

        if (newId.Length == 0)
        {
            error = "Id:t behöver minst en bokstav eller siffra (a–z, 0–9, - och _).";
            return false;
        }
        if (newName.Length == 0)
        {
            error = "Namnet får inte vara tomt.";
            return false;
        }

        // Names share one lookup table where the first pet wins, so a clash would make one of the
        // two pets unreachable by name. An unchanged id or name is not a clash with itself.
        string[] wanted = string.Equals(newId, id, StringComparison.OrdinalIgnoreCase)
            ? [newName]
            : [newId, newName];

        lock (_lock)
        {
            foreach (string taken in wanted)
                if (_byAlias.TryGetValue(taken, out PetDefinition? owner)
                    && !string.Equals(owner.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"\"{taken}\" är redan upptaget av {owner.Name} ({owner.Id}).";
                    return false;
                }
        }

        return TryEditManifest(id, manifest =>
        {
            manifest["id"] = newId;
            manifest["displayName"] = newName;
        }, out error);
    }

    /// <summary>Changes one pet's pet.json and reloads the catalog.</summary>
    private bool TryEditManifest(string id, Action<JsonObject> edit, out string error)
    {
        error = string.Empty;
        string? manifestPath;
        lock (_lock) manifestPath = _manifestPaths.GetValueOrDefault(id);
        if (manifestPath is null || !File.Exists(manifestPath))
        {
            // Only the built-in fallback pets, used when the folder is unreadable, lack a file.
            error = $"Peten \"{id}\" har ingen pet.json att spara i.";
            return false;
        }

        try
        {
            // Edited as a JSON document: pet.json is Codex's format, and a round-trip through
            // PetManifest would drop every field this app has no property for.
            JsonNode document = JsonNode.Parse(File.ReadAllText(manifestPath),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                ?? new JsonObject();
            if (document is not JsonObject manifest)
            {
                error = $"{Path.GetFileName(Path.GetDirectoryName(manifestPath))}: pet.json är inte ett objekt.";
                return false;
            }

            edit(manifest);
            File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"Kunde inte skriva {manifestPath}: {ex.Message}";
            return false;
        }

        Reload();
        return true;
    }

    /// <summary>Looks up one species by id, display name or alias.</summary>
    public PetDefinition? Find(string? nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId)) return null;
        lock (_lock) return _byAlias.GetValueOrDefault(nameOrId.Trim());
    }

    /// <summary>
    /// Finds the species named somewhere in free text ("en blaze tack!"). Names match as whole
    /// words and may themselves contain spaces or hyphens ("space-cat", "Rostiga Rolf"). The name
    /// that starts first wins; of two starting in the same place, the longer.
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
                // Earliest start wins; at the same start the longer name does, so "space cat" beats
                // "space".
                if (at < 0 || at > bestAt || (at == bestAt && alias.Length <= bestLength)) continue;
                (best, bestAt, bestLength) = (pet, at, alias.Length);
            }
            return best;
        }
    }

    /// <summary>
    /// The full pick order for a spawn: the species the viewer asked for, else the streamer's
    /// default, else a random one.
    ///
    /// <para><paramref name="mayUse"/> filters which species this viewer may be handed, at every
    /// step. Left out, no win-only pet is ever chosen.</para>
    /// </summary>
    public PetDefinition Choose(string? text, string? defaultPetId, Func<PetDefinition, bool>? mayUse = null)
    {
        mayUse ??= pet => !pet.WinOnly;
        PetDefinition? chosen = ResolveFromText(text);
        if (chosen is not null && mayUse(chosen)) return chosen;
        chosen = Find(defaultPetId);
        if (chosen is not null && mayUse(chosen)) return chosen;
        PetDefinition[] pets = Pets.Where(mayUse).ToArray();
        return pets.Length > 0 ? pets[Random.Shared.Next(pets.Length)] : Placeholder;
    }

    /// <summary>Absolute spritesheet path for a spritesheet pet.</summary>
    public bool TryGetSpriteFile(string id, out string path)
    {
        path = string.Empty;
        PetDefinition? pet = Find(id);
        if (pet?.SpriteFile is not { Length: > 0 } file || !File.Exists(file)) return false;
        path = file;
        return true;
    }

    /// <summary>
    /// The SVG drawing for a pet, read from disk on every call. False for a spritesheet pet.
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

        // The file is missing or unreadable; fall back to the copy embedded in the exe.
        svg = PetDefaults.Body(pet.Id);
        return svg.Length > 0;
    }

    /// <summary>
    /// Where <paramref name="word"/> stands on its own in the text, or -1: "blazer" does not match
    /// "blaze", "en BLAZE, tack!" does.
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

    /// <summary>True when the index is outside the text or at a character that is not a letter or digit.</summary>
    private static bool IsEdge(string text, int index) =>
        index < 0 || index >= text.Length || !char.IsLetterOrDigit(text[index]);

    private (IReadOnlyList<PetDefinition>, Dictionary<string, PetDefinition>, Dictionary<string, string>, IReadOnlyList<string>) Load()
    {
        var warnings = new List<string>();
        Seed(warnings);

        var manifestPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pets = new List<PetDefinition>();
        foreach ((PetDefinition pet, string manifestPath) in LoadFolder(warnings))
        {
            pets.Add(pet);
            manifestPaths.TryAdd(pet.Id, manifestPath);
        }
        if (pets.Count == 0) pets.AddRange(Shipped());

        // The shipped pets sort first, which gives them first claim on their names in byAlias.
        pets = pets.OrderByDescending(pet => pet.IsDefault).ThenBy(pet => pet.Id, StringComparer.OrdinalIgnoreCase).ToList();

        var byAlias = new Dictionary<string, PetDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (PetDefinition pet in pets)
        {
            byAlias.TryAdd(pet.Id, pet);
            byAlias.TryAdd(pet.Name, pet);
            foreach (string alias in pet.Aliases) byAlias.TryAdd(alias, pet);
        }
        return (pets, byAlias, manifestPaths, warnings);
    }

    /// <summary>
    /// Writes the shipped pets to the folder, once each: the marker file records which have been
    /// written, so a pet the streamer deleted does not come back.
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
                // In a finally so a failure further down the list still marks the pets that reached
                // the disk; unmarked, the next start would overwrite the streamer's edits to them.
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

    private IEnumerable<(PetDefinition Pet, string ManifestPath)> LoadFolder(List<string> warnings)
    {
        if (!Directory.Exists(PetsFolder)) yield break;

        string[] folders;
        try { folders = Directory.GetDirectories(PetsFolder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        foreach (string folder in folders.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            PetDefinition? pet = LoadOne(folder, warnings);
            if (pet is not null) yield return (pet, Path.Combine(folder, "pet.json"));
        }
    }

    /// <summary>The shipped pets as embedded in the exe, for when the folder is unusable.</summary>
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

    /// <summary>Reads one pet folder. A broken pet returns null and adds a warning.</summary>
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

        // A spritesheet wins over a drawing.
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
    /// Turns a path out of pet.json into an absolute file inside the pet's own folder, or null. A
    /// path that escapes the folder is refused: the server serves this file, so the manifest must
    /// not be able to point it at an arbitrary one.
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
        manifest.SpriteVersionNumber is >= 2 ? 2 : 1,
        manifest.WinOnly == true,
        PetRarity.Normalize(manifest.Rarity));

    private static IReadOnlyList<string> Clean(string[]? values) =>
        (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray();

    /// <summary>
    /// Lower-cases the id and drops everything but a–z, 0–9, '-' and '_': it is used in URLs and DOM
    /// lookups.
    /// </summary>
    public static string SanitizeId(string raw) =>
        new(raw.Trim().ToLowerInvariant().Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').ToArray());

    /// <summary>
    /// The subset of Codex's pet.json this app reads; unknown fields are ignored. <c>winOnly</c>
    /// and <c>rarity</c> are this app's own additions: a pet marked winOnly can only be won in the
    /// lucky spin and only used by whoever won it.
    /// </summary>
    private sealed record PetManifest(
        string? Id,
        string? DisplayName,
        string? Description,
        [property: JsonPropertyName("spritesheetPath")] string? SpritesheetPath,
        [property: JsonPropertyName("bodyPath")] string? BodyPath,
        string[]? Aliases,
        string[]? Emoji,
        double? Fps,
        [property: JsonPropertyName("spriteVersionNumber")] int? SpriteVersionNumber,
        [property: JsonPropertyName("winOnly")] bool? WinOnly,
        [property: JsonPropertyName("rarity")] string? Rarity);
}
