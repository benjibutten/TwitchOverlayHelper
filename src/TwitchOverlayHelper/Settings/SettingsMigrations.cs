namespace TwitchOverlayHelper.Settings;

/// <summary>What reading an existing settings file turned out to involve.</summary>
/// <param name="From">The version the file was written in. 0 is anything from before this stamp existed.</param>
/// <param name="To">The version it is in now.</param>
/// <param name="FromFuture">
/// The file was written by a newer build than this one. Nothing is migrated then, and the settings
/// this build has no property for are carried through untouched.
/// </param>
public sealed record SettingsMigrationResult(int From, int To, bool FromFuture)
{
    public bool Changed => From != To;
}

/// <summary>
/// Brings a settings file written by an older build up to the shape this one expects.
///
/// <para><see cref="System.Text.Json"/> ignores a property it does not recognise and the next save
/// writes the default over it, so renaming or splitting a setting needs a step here that reads the
/// old shape and carries it over.</para>
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
    /// <item><description>0 – no stamp on the file.</description></item>
    /// <item><description>1 – the stamp itself.</description></item>
    /// <item><description>2 – the lucky spin does not pay back duplicates; the two bot lines that
    /// promised the points back are reworded where they were left at the default.</description></item>
    /// <item><description>3 – the two lines that announce a spin win carry <c>{rarity}</c> where
    /// they were left at the default.</description></item>
    /// <item><description>4 – a duplicate can be released onto the lawn; the line that explains
    /// the choice names the <c>{release}</c> command where it was left at the default.</description></item>
    /// </list>
    /// </summary>
    public const int CurrentVersion = 4;

    public static SettingsMigrationResult Apply(AppSettings settings)
    {
        int from = settings.SchemaVersion;
        if (from > CurrentVersion) return new SettingsMigrationResult(from, from, FromFuture: true);

        // Single steps in a loop, so a file three versions old walks the same path as a file one
        // version old.
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
                // Nothing to reshape: every property an unstamped file can hold is read by the same name.
                break;

            case 1:
                // A duplicate win is not refunded, so a default line promising the points back is replaced.
                Reword(settings.Bot, BotFlow.SpinDuplicate,
                    "@{viewer} du vann {prize} – men den har du redan! Skriv \"{command} namn\" inom {minutes} min för att skänka den till någon, annars får du tillbaka poängen.");
                Reword(settings.Bot, BotFlow.SpinGiftOwned, "@{viewer} @{target} har redan {prize} – välj någon annan.");
                break;

            case 2:
                // The default win lines gain {rarity}.
                Reword(settings.Bot, BotFlow.SpinWin, "🎉 @{viewer} vann {prize} i lyckosnurren!");
                Reword(settings.Bot, BotFlow.SpinDuplicate,
                    "@{viewer} du vann {prize} – men den har du redan! Skriv \"{command} namn\" inom {minutes} min för att skänka den till någon, annars är chansen borta.");
                break;

            case 3:
                Reword(settings.Bot, BotFlow.SpinDuplicate,
                    "@{viewer} du vann {prize} ({rarity}) – men den har du redan! Skriv \"{command} namn\" inom {minutes} min för att skänka den till någon, annars är chansen borta.");
                break;
        }
    }

    /// <summary>
    /// Puts one flow on the wording this build ships with, but only where it reads word for word
    /// as <paramref name="wasDefault"/>. A template the streamer has made their own is left alone.
    /// </summary>
    private static void Reword(BotSettings? bot, BotFlow flow, string wasDefault)
    {
        BotMessageRule? rule = bot?.Messages?.FirstOrDefault(saved => saved is not null && saved.Flow == flow);
        if (rule is null || !string.Equals(rule.Template?.Trim(), wasDefault, StringComparison.Ordinal)) return;
        rule.Template = BotSettings.Defaults.First(fallback => fallback.Flow == flow).Template;
    }
}
