using System.IO;
using System.Reflection;

namespace TwitchOverlayHelper.Pets;

/// <summary>One pet that ships with the app, as the two files it becomes in the user's pets folder.</summary>
internal sealed record PetSeed(string Id, string Manifest, string Body);

/// <summary>
/// The pets the app is born with. They live inside the exe only until the first start, when they
/// are written out as ordinary files – a streamer who wants Blaze in another colour edits the same
/// kind of folder as for a pet they made themselves.
/// </summary>
internal static class PetDefaults
{
    private const string Prefix = "TwitchOverlayHelper.Pets.Defaults.";
    private static readonly Assembly Assembly = typeof(PetDefaults).Assembly;
    private static readonly Dictionary<string, string> Bodies = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<PetSeed> All { get; } = LoadAll();

    /// <summary>The note that explains the format, written next to the pets.</summary>
    public static string Readme { get; } = Read(Prefix + "README.txt");

    public static bool IsDefault(string id) => Bodies.ContainsKey(id);

    /// <summary>The shipped drawing for a pet, for when the folder on disk cannot be used.</summary>
    public static string Body(string id) => Bodies.GetValueOrDefault(id, string.Empty);

    private static IReadOnlyList<PetSeed> LoadAll()
    {
        var manifests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string resource in Assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(Prefix, StringComparison.Ordinal)) continue;

            // Resource names flatten folders to dots, so a pet arrives as "<id>.pet.json" and
            // "<id>.body.svg"; anything else in the folder (the readme) is not a pet.
            string rest = resource[Prefix.Length..];
            int dot = rest.IndexOf('.');
            if (dot <= 0) continue;
            string id = rest[..dot];
            switch (rest[(dot + 1)..])
            {
                case "pet.json": manifests[id] = Read(resource); break;
                case "body.svg": Bodies[id] = Read(resource); break;
            }
        }

        return manifests
            .Where(pair => Bodies.ContainsKey(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new PetSeed(pair.Key, pair.Value, Bodies[pair.Key]))
            .ToArray();
    }

    private static string Read(string resource)
    {
        using Stream? stream = Assembly.GetManifestResourceStream(resource);
        if (stream is null) return string.Empty;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
