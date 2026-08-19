namespace TwitchOverlayHelper.Settings;

/// <summary>What reading an existing settings file turned out to involve.</summary>
/// <param name="From">The version the file was written in. 0 is anything from before this stamp existed.</param>
/// <param name="To">The version it is in now.</param>
/// <param name="FromFuture">
/// The file was written by a newer build than this one. Nothing is migrated then – a step that
/// only knows how to go forwards cannot be run backwards – and the settings this build has no
/// property for are carried through untouched instead of being dropped on the next save.
/// </param>
public sealed record SettingsMigrationResult(int From, int To, bool FromFuture)
{
    public bool Changed => From != To;
}

/// <summary>
/// Brings a settings file written by an older build up to the shape this one expects.
///
/// <para>The point is not the steps – so far there are none worth the name – it is having the
/// place they go. Renaming a property or splitting one setting into two is otherwise a change that
/// silently loses whatever the user had set, because <see cref="System.Text.Json"/> answers a
/// property it does not recognise by ignoring it and the next save then writes the default back
/// over it. With a version on the file, the old shape can be read once and carried over.</para>
///
/// <para>Every step must be safe to run on a file it has already run on: a save that failed after
/// migrating, or a snapshot restored by hand, both bring the same file round a second time.</para>
/// </summary>
public static class SettingsMigrations
{
    /// <summary>
    /// The shape this build writes. Raise it in the same commit that adds the step for it.
    ///
    /// <list type="bullet">
    /// <item><description>0 – everything up to and including the first builds with a bot: no stamp on the file.</description></item>
    /// <item><description>1 – the stamp itself, so later changes have something to migrate from.</description></item>
    /// </list>
    /// </summary>
    public const int CurrentVersion = 1;

    public static SettingsMigrationResult Apply(AppSettings settings)
    {
        int from = settings.SchemaVersion;
        if (from > CurrentVersion) return new SettingsMigrationResult(from, from, FromFuture: true);

        // Deliberately a loop over single steps rather than one big if: a file three versions old
        // has to walk the same path a file one version old does, or the rarely-taken jump is the
        // one nobody ever tries.
        for (int version = from; version < CurrentVersion; version++)
            Step(settings, version);

        settings.SchemaVersion = CurrentVersion;
        return new SettingsMigrationResult(from, CurrentVersion, FromFuture: false);
    }

    private static void Step(AppSettings settings, int from)
    {
        switch (from)
        {
            case 0:
                // Nothing to reshape: every property an unstamped file can hold is still read by
                // the same name. The step exists so that version 1 means "seen and checked" rather
                // than "never looked at".
                break;
        }
    }
}
