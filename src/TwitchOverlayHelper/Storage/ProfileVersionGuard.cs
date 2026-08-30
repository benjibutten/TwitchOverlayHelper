namespace TwitchOverlayHelper.Storage;

/// <summary>What the version check found on this start.</summary>
/// <param name="PreviousVersion">The build that last wrote the profile. Empty from before this was recorded.</param>
/// <param name="Changed">This build has not run against this profile before.</param>
/// <param name="Snapshot">How the copy of the previous build's profile went.</param>
public sealed record ProfileVersionResult(string PreviousVersion, bool Changed, ProfileSnapshot Snapshot)
{
    /// <summary>
    /// Whether the version on the profile can be moved forward. False after a copy that could not
    /// be completed: the stamp is what stops the next start from trying, so writing it on a failed
    /// attempt is the same as deciding never to have a copy of this upgrade at all.
    /// </summary>
    public bool MayStamp => !Changed || Snapshot.Complete;
}

/// <summary>
/// Notices that the app is running against a profile last written by a different build, and copies
/// the profile aside before anything in it is touched.
///
/// <para>Deliberately not hung off the updater. The app can also be updated by unpacking a release
/// over the folder, by a rollback, or by starting an older build that is still lying around – and
/// those are the cases where a settings file gets read by something that does not fully understand
/// it. What they all have in common is not the updater running; it is the version on disk not
/// matching the version in the process.</para>
///
/// <para>Whether there is a profile to copy is left to <see cref="ProfileArchive"/>, which answers
/// it by looking in the folder. It was once decided from whether the settings file had loaded, and
/// that got it exactly backwards: a settings file too broken to read was read as "no profile yet"
/// and skipped the snapshot – on the one start where the copy mattered most.</para>
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

        // An empty previous version is a profile from before the stamp existed – which makes it the
        // oldest file the app will ever meet, and the one most worth having a copy of. A first run
        // reaches here too, and comes back as Nothing because the folder has nothing in it yet.
        string label = previousVersion.Length == 0 ? "fore-stampeln" : previousVersion;
        return new ProfileVersionResult(
            previousVersion,
            Changed: true,
            ProfileArchive.Create(label, profileFolder, snapshotFolder, keep));
    }
}
