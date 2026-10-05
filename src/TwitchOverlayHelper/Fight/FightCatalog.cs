using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using TwitchOverlayHelper.Storage;

namespace TwitchOverlayHelper.Fight;

/// <summary>
/// One fighter for the wait screen. The sprite is a strip of eight frames in a fixed order and a
/// fixed cell size – see <see cref="FightCatalog"/> – so placing a fighter is one point whatever
/// the pose, and any two fighters can face each other.
/// </summary>
public sealed record FighterDefinition(
    string Id,
    string Name,
    string Description,
    string SpriteFile,
    double Scale,
    bool IsDefault);

/// <summary>
/// One place to fight in. The picture is drawn to cover the whole scene; <see cref="Floor"/> is
/// where in it the fighters' feet go, and <see cref="Left"/> and <see cref="Right"/> how far
/// they may walk – all as fractions of the picture, so they hold at any resolution. Parts of the
/// picture left transparent show the stream through.
/// </summary>
public sealed record ArenaDefinition(
    string Id,
    string Name,
    string Description,
    string? ImageFile,
    double Floor,
    double Left,
    double Right,
    bool IsDefault);

/// <summary>
/// The fighters and arenas the wait screen can use, read from the user's fight folder. Built like
/// the pets: what ships with the app is written out on first start and is then the streamer's own,
/// and a new fighter is a folder dropped in next to the others – <c>fighters/&lt;id&gt;/fighter.json</c>
/// with a <c>sprite.webp</c>, or <c>arenas/&lt;id&gt;/arena.json</c> with a picture.
/// </summary>
public sealed class FightCatalog
{
    /// <summary>The arena that is no arena: nothing behind the fighters, the stream shows through.</summary>
    public const string TransparentArenaId = "none";

    /// <summary>Every fighter strip is this many frames in this order, cells of this size.</summary>
    public const int Frames = 8, CellWidth = 640, CellHeight = 560, Baseline = 550;

    private const string SeedMarkerFile = ".defaults";

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Where a transparent scene puts the feet and the walls – the old garage's numbers.</summary>
    public static ArenaDefinition Transparent { get; } =
        new(TransparentArenaId, "Transparent – ingen bakgrund", "Bara fajten, streamen syns bakom.", null, 0.815, 0.09, 0.91, IsDefault: true);

    private readonly Lock _lock = new();
    private IReadOnlyList<FighterDefinition> _fighters = [];
    private IReadOnlyList<ArenaDefinition> _arenas = [];
    private IReadOnlyList<string> _warnings = [];

    public FightCatalog(string? folder = null)
    {
        Folder = folder ?? ProfilePaths.Folder("fight");
        Reload();
    }

    /// <summary>The fight folder, holding <c>fighters</c> and <c>arenas</c>.</summary>
    public string Folder { get; }

    public string FightersFolder => Path.Combine(Folder, "fighters");
    public string ArenasFolder => Path.Combine(Folder, "arenas");

    public IReadOnlyList<FighterDefinition> Fighters { get { lock (_lock) return _fighters; } }

    /// <summary>Every arena, the transparent one first.</summary>
    public IReadOnlyList<ArenaDefinition> Arenas { get { lock (_lock) return _arenas; } }

    /// <summary>Human-readable notes about folders that could not be loaded.</summary>
    public IReadOnlyList<string> Warnings { get { lock (_lock) return _warnings; } }

    /// <summary>Re-reads the folder, so a new fighter shows up without a restart.</summary>
    public void Reload()
    {
        var warnings = new List<string>();
        Seed(warnings);
        IReadOnlyList<FighterDefinition> fighters = LoadAll(FightersFolder, "fighter.json", LoadFighter, warnings);
        IReadOnlyList<ArenaDefinition> arenas = [Transparent, .. LoadAll(ArenasFolder, "arena.json", LoadArena, warnings)];
        lock (_lock)
        {
            _fighters = fighters;
            _arenas = arenas;
            _warnings = warnings;
        }
    }

    public FighterDefinition? FindFighter(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return Fighters.FirstOrDefault(f => string.Equals(f.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public ArenaDefinition? FindArena(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return Arenas.FirstOrDefault(a => string.Equals(a.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The fighter in a slot: the one chosen, else the one at <paramref name="fallbackIndex"/> in
    /// the list, so a deleted fighter never leaves an empty corner of the ring.
    /// </summary>
    public FighterDefinition? FighterOrFallback(string? id, int fallbackIndex)
    {
        FighterDefinition? chosen = FindFighter(id);
        if (chosen is not null) return chosen;
        IReadOnlyList<FighterDefinition> all = Fighters;
        return all.Count == 0 ? null : all[Math.Min(fallbackIndex, all.Count - 1)];
    }

    public ArenaDefinition ArenaOrFallback(string? id) => FindArena(id) ?? Arenas.FirstOrDefault(a => a.ImageFile is not null) ?? Transparent;

    /// <summary>Only files the catalog itself resolved are served, so a URL cannot name any other.</summary>
    public bool TryGetFighterSprite(string id, out string path)
    {
        path = FindFighter(id)?.SpriteFile ?? string.Empty;
        return path.Length > 0 && File.Exists(path);
    }

    public bool TryGetArenaImage(string id, out string path)
    {
        path = FindArena(id)?.ImageFile ?? string.Empty;
        return path.Length > 0 && File.Exists(path);
    }

    /// <summary>The id is used in URLs, so anything but simple characters is dropped.</summary>
    public static string SanitizeId(string raw) =>
        new(raw.Trim().ToLowerInvariant().Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').ToArray());

    private static IReadOnlyList<T> LoadAll<T>(string root, string manifestName,
        Func<string, string, string, List<string>, T?> load, List<string> warnings) where T : class
    {
        if (!Directory.Exists(root)) return [];
        string[] folders;
        try { folders = Directory.GetDirectories(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

        var loaded = new List<(T Item, string Id)>();
        foreach (string folder in folders.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            string manifest = Path.Combine(folder, manifestName);
            if (!File.Exists(manifest)) continue;
            string folderName = Path.GetFileName(folder);
            string json;
            try { json = File.ReadAllText(manifest); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{folderName}: {manifestName} gick inte att läsa ({ex.Message}).");
                continue;
            }
            T? item = load(folder, folderName, json, warnings);
            if (item is null) continue;
            string id = item switch { FighterDefinition f => f.Id, ArenaDefinition a => a.Id, _ => folderName };
            if (loaded.Any(other => string.Equals(other.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"{folderName}: id:t \"{id}\" används redan av en annan mapp.");
                continue;
            }
            loaded.Add((item, id));
        }
        return loaded.Select(entry => entry.Item).ToArray();
    }

    private static FighterDefinition? LoadFighter(string folder, string folderName, string json, List<string> warnings)
    {
        FighterManifest? manifest = Parse<FighterManifest>(json, folderName, "fighter.json", warnings);
        if (manifest is null) return null;

        string id = SanitizeId(manifest.Id is { Length: > 0 } ? manifest.Id : folderName);
        if (id.Length == 0)
        {
            warnings.Add($"{folderName}: id saknas.");
            return null;
        }
        string? sprite = Resolve(folder, manifest.SpritePath ?? "sprite.webp", folderName, "spritePath", warnings);
        if (sprite is null)
        {
            warnings.Add($"{folderName}: hittar ingen sprite ({manifest.SpritePath ?? "sprite.webp"}).");
            return null;
        }

        return new FighterDefinition(
            id,
            manifest.DisplayName is { Length: > 0 } name ? name.Trim() : id,
            manifest.Description?.Trim() ?? string.Empty,
            sprite,
            manifest.Scale is >= 0.3 and <= 3 ? manifest.Scale.Value : 1,
            FightDefaults.IsDefault("fighters", id));
    }

    private static ArenaDefinition? LoadArena(string folder, string folderName, string json, List<string> warnings)
    {
        ArenaManifest? manifest = Parse<ArenaManifest>(json, folderName, "arena.json", warnings);
        if (manifest is null) return null;

        string id = SanitizeId(manifest.Id is { Length: > 0 } ? manifest.Id : folderName);
        if (id.Length == 0 || id == TransparentArenaId)
        {
            warnings.Add($"{folderName}: id saknas eller är det reserverade \"{TransparentArenaId}\".");
            return null;
        }
        string? image = Resolve(folder, manifest.ImagePath ?? "arena.webp", folderName, "imagePath", warnings);
        if (image is null)
        {
            warnings.Add($"{folderName}: hittar ingen bild ({manifest.ImagePath ?? "arena.webp"}).");
            return null;
        }

        double floor = manifest.Floor is > 0.3 and < 1 ? manifest.Floor.Value : Transparent.Floor;
        double left = manifest.Left is >= 0 and < 0.45 ? manifest.Left.Value : Transparent.Left;
        double right = manifest.Right is > 0.55 and <= 1 ? manifest.Right.Value : Transparent.Right;
        return new ArenaDefinition(
            id,
            manifest.DisplayName is { Length: > 0 } name ? name.Trim() : id,
            manifest.Description?.Trim() ?? string.Empty,
            image, floor, left, right,
            FightDefaults.IsDefault("arenas", id));
    }

    private static T? Parse<T>(string json, string folderName, string file, List<string> warnings) where T : class
    {
        try
        {
            T? value = JsonSerializer.Deserialize<T>(json, ManifestJson);
            if (value is null) warnings.Add($"{folderName}: {file} är tom.");
            return value;
        }
        catch (JsonException ex)
        {
            warnings.Add($"{folderName}: {file} gick inte att läsa ({ex.Message}).");
            return null;
        }
    }

    /// <summary>
    /// A path out of a manifest, as an absolute file inside that folder. The id ends up in a URL,
    /// so a manifest must not be able to point the server at a file anywhere else.
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
            warnings.Add($"{folderName}: {field} pekar utanför mappen.");
            return null;
        }
        return File.Exists(full) ? full : null;
    }

    /// <summary>
    /// Writes what ships with the app to the folder, once each. The marker is what makes it once: a
    /// fighter the streamer deleted on purpose stays deleted.
    /// </summary>
    private void Seed(List<string> warnings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string marker = Path.Combine(Folder, SeedMarkerFile);
            var seeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(marker))
                foreach (string line in File.ReadAllLines(marker))
                    if (line.Trim() is { Length: > 0 } key) seeded.Add(key);

            var written = new List<string>();
            try
            {
                foreach (FightSeed seed in FightDefaults.All)
                {
                    string key = $"{seed.Kind}/{seed.Id}";
                    if (!seeded.Add(key)) continue;
                    string target = Path.Combine(Folder, seed.Kind, seed.Id);
                    Directory.CreateDirectory(target);
                    foreach ((string name, byte[] content) in seed.Files)
                        File.WriteAllBytes(Path.Combine(target, name), content);
                    written.Add(key);
                }
            }
            finally
            {
                if (written.Count > 0) File.AppendAllLines(marker, written);
            }

            string readme = Path.Combine(Folder, "LÄS MIG.txt");
            if (!File.Exists(readme)) File.WriteAllText(readme, FightDefaults.Readme);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            warnings.Add($"Kunde inte skriva fajtmappen {Folder} ({ex.Message}).");
        }
    }

    private sealed record FighterManifest(
        string? Id,
        string? DisplayName,
        string? Description,
        [property: JsonPropertyName("spritePath")] string? SpritePath,
        double? Scale);

    private sealed record ArenaManifest(
        string? Id,
        string? DisplayName,
        string? Description,
        [property: JsonPropertyName("imagePath")] string? ImagePath,
        double? Floor,
        double? Left,
        double? Right);
}

/// <summary>One fighter or arena that ships with the app, as the files it becomes on disk.</summary>
internal sealed record FightSeed(string Kind, string Id, IReadOnlyList<(string Name, byte[] Content)> Files);

/// <summary>
/// What the wait screen is born with. Inside the exe only until the first start, then ordinary
/// files the streamer can change or delete like anything they added themselves.
/// </summary>
internal static class FightDefaults
{
    private const string Prefix = "TwitchOverlayHelper.Fight.Defaults.";
    private static readonly Assembly Assembly = typeof(FightDefaults).Assembly;

    public static IReadOnlyList<FightSeed> All { get; } = LoadAll();

    public static string Readme { get; } = System.Text.Encoding.UTF8.GetString(Read(Prefix + "README.txt"));

    public static bool IsDefault(string kind, string id) =>
        All.Any(seed => seed.Kind == kind && string.Equals(seed.Id, id, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<FightSeed> LoadAll()
    {
        // Resource names flatten folders to dots: "fighters.silver.sprite.webp".
        var groups = new Dictionary<(string Kind, string Id), List<(string, byte[])>>();
        foreach (string resource in Assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            string[] parts = resource[Prefix.Length..].Split('.', 3);
            if (parts.Length < 3 || parts[0] is not ("fighters" or "arenas")) continue;
            var key = (parts[0], parts[1]);
            if (!groups.TryGetValue(key, out List<(string, byte[])>? files)) groups[key] = files = [];
            files.Add((parts[2], Read(resource)));
        }
        return groups
            .OrderBy(pair => pair.Key.Kind).ThenBy(pair => pair.Key.Id, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new FightSeed(pair.Key.Kind, pair.Key.Id, pair.Value))
            .ToArray();
    }

    private static byte[] Read(string resource)
    {
        using Stream? stream = Assembly.GetManifestResourceStream(resource);
        if (stream is null) return [];
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
