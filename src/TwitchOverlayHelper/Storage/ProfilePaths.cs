using System.IO;

namespace TwitchOverlayHelper.Storage;

/// <summary>
/// Where the user's own data lives. Every store used to spell the same three path segments out for
/// itself, which was fine while there were two of them and is how a folder ends up half-moved: one
/// place that answers the question is what lets a snapshot know what it is copying.
/// </summary>
public static class ProfilePaths
{
    /// <summary>The folder holding settings, nicknames, credentials, pets, caches and logs.</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TwitchOverlayHelper");

    /// <summary>Full-profile copies taken when the version changes. Never itself copied.</summary>
    public static string Snapshots => Path.Combine(Root, "snapshots");

    /// <summary>A file directly in the profile folder.</summary>
    public static string File(string name) => Path.Combine(Root, name);

    /// <summary>A folder directly in the profile folder.</summary>
    public static string Folder(string name) => Path.Combine(Root, name);
}
