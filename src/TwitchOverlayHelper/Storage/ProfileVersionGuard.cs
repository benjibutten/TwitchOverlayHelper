namespace TwitchOverlayHelper.Storage;

/// <summary>What the version check found on this start.</summary>
/// <param name="PreviousVersion">The build that last wrote the profile. Empty from before this was recorded.</param>
/// <param name="Changed">This build has not run against this profile before.</param>
/// <param name="SnapshotPath">The copy taken of the profile as the previous build left it, if one could be.</param>
public sealed record ProfileVersionResult(string PreviousVersion, bool Changed, string? SnapshotPath);

/// <summary>
/// Notices that the app is running against a profile last written by a different build, and copies
/// the profile aside before anything in it is touched.
///
/// <para>Deliberately not hung off the updater. The app can also be updated by unpacking a release
/// over the folder, by a rollback, or by starting an older build that is still lying around – and
/// those are the cases where a settings file gets read by something that does not fully understand
/// it. What they all have in common is not the updater running; it is the version on disk not
/// matching the version in the process.</para>
/// </summary>
public static class ProfileVersionGuard
{
    public static ProfileVersionResult Check(
        string previousVersion,
        string currentVersion,
        bool profileExisted,
        string? profileFolder = null,
        string? snapshotFolder = null,
        int keep = ProfileArchive.DefaultKeep)
    {
        previousVersion ??= string.Empty;

        // A first run has nothing worth copying, and copying the defaults it is about to write would
        // only push a real snapshot out of the folder later.
        if (!profileExisted || string.Equals(previousVersion, currentVersion, StringComparison.Ordinal))
            return new ProfileVersionResult(previousVersion, Changed: false, SnapshotPath: null);

        // An empty previous version is a profile from before the stamp existed – which makes it the
        // oldest file the app will ever meet, and the one most worth having a copy of.
        string label = previousVersion.Length == 0 ? "fore-stampeln" : previousVersion;
        return new ProfileVersionResult(previousVersion, Changed: true, ProfileArchive.Create(label, profileFolder, snapshotFolder, keep));
    }
}
