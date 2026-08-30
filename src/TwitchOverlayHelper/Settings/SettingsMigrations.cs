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
    /// <item><description>2 – the lucky spin stopped paying back duplicates, so the two bot lines that
    /// promised the points back are worded afresh where the streamer had left them as they were.</description></item>
    /// </list>
    /// </summary>
    public const int CurrentVersion = 2;

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

            case 1:
                // A duplicate win used to end in a refund when nobody was named in time, and two of
                // the bot's lines said so out loud. It does not any more – the spin buys one chance
                // to place the pet and nothing else – so a line the streamer never touched is put
                // right rather than left promising points that will not come.
                Reword(settings.Bot, BotFlow.SpinDuplicate,
                    "@{viewer} du vann {prize} – men den har du redan! Skriv \"{command} namn\" inom {minutes} min för att skänka den till någon, annars får du tillbaka poängen.");
                Reword(settings.Bot, BotFlow.SpinGiftOwned, "@{viewer} @{target} har redan {prize} – välj någon annan.");
                break;
        }
    }

    /// <summary>
    /// Puts one flow back on the wording this build ships with – but only where it still reads word
    /// for word as the old default did. A template the streamer has made their own is theirs, and a
    /// migration that overwrote it would be taking their words away to fix the app's.
    /// </summary>
    private static void Reword(BotSettings? bot, BotFlow flow, string wasDefault)
    {
        BotMessageRule? rule = bot?.Messages?.FirstOrDefault(saved => saved is not null && saved.Flow == flow);
        if (rule is null || !string.Equals(rule.Template?.Trim(), wasDefault, StringComparison.Ordinal)) return;
        rule.Template = BotSettings.Defaults.First(fallback => fallback.Flow == flow).Template;
    }
}
