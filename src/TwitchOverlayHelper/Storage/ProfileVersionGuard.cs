namespace TwitchOverlayHelper.Storage;

/// <summary>What the version check found on this start.</summary>
/// <param name="PreviousVersion">The build that last wrote the profile. Empty from before this was recorded.</param>
/// <param name="Changed">This build has not run against this profile before.</param>
/// <param name="Snapshot">How the copy of the previous build's profile went.</param>
public sealed record ProfileVersionResult(string PreviousVersion, bool Changed, ProfileSnapshot Snapshot)
{
    /// <summary>
    /// Whether the version on the profile can be moved forward. False after a copy that could not
    /// be completed: the stamp is what stops the next start from trying again.
    /// </summary>
    public bool MayStamp => !Changed || Snapshot.Complete;
}

/// <summary>
/// Notices that the app is running against a profile last written by a different build, and copies
/// the profile aside before anything in it is touched.
///
/// <para>Not hung off the updater: the app can also be updated by unpacking a release over the
/// folder, by a rollback, or by starting an older build that is still lying around. What those have
/// in common is the version on disk not matching the version in the process.</para>
///
/// <para>Whether there is a profile to copy is left to <see cref="ProfileArchive"/>, which answers
/// it by looking in the folder. Whether the settings file loaded is no guide: a file too broken to
/// read is not a missing profile, and that start is the one where the copy matters most.</para>
/// </summary>
public static class ProfileVersionGuard
{
    public static ProfileVersionResult Check(
        string previousVersion,
        string currentVersion,
        string? profileFolder = null,
        string? snapshotFolder = null,
        int keep = ProfileArchive.DefaultKeep)
    {
        previousVersion ??= string.Empty;

        if (string.Equals(previousVersion, currentVersion, StringComparison.Ordinal))
            return new ProfileVersionResult(previousVersion, Changed: false, ProfileSnapshot.Nothing);

        // An empty previous version is a profile from before the stamp existed: the oldest the app
        // will meet, and the one most worth copying. A first run reaches here too, and comes back
        // as Nothing because the folder is empty.
        string label = previousVersion.Length == 0 ? "fore-stampeln" : previousVersion;
        return new ProfileVersionResult(
            previousVersion,
            Changed: true,
            ProfileArchive.Create(label, profileFolder, snapshotFolder, keep));
    }
}
