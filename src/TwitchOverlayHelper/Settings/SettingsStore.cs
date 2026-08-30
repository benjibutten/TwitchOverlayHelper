using System.Text.Json;
using System.IO;
using TwitchOverlayHelper.Storage;

namespace TwitchOverlayHelper.Settings;

/// <summary>What loading the settings turned out to involve, for the app to tell the user about.</summary>
/// <param name="RecoveredFromBackup">The file on disk was unreadable and a dated copy answered instead.</param>
/// <param name="QuarantinedPath">Where the unreadable file was put, when no copy could answer either.</param>
/// <param name="Migration">Which shape the file was in, and which it is in now.</param>
/// <param name="StartedFresh">Nothing readable was found at all, so these are the defaults.</param>
public sealed record SettingsLoadReport(
    bool RecoveredFromBackup,
    string? QuarantinedPath,
    SettingsMigrationResult Migration,
    bool StartedFresh);

/// <summary>
/// settings.json, which by now holds most of what the user has ever configured – the overlay, the
/// dock, the stream view, two voices, the pets, the bot.
///
/// <para>It used to be written straight over itself and read with a catch that answered any
/// problem with a blank <see cref="AppSettings"/>. That is the shape of every settings file that
/// has ever quietly emptied itself: one unreadable byte, one save afterwards, and the evening's
/// work is the new truth. It now goes through <see cref="BackedUpJsonFile"/> – atomic writes, a
/// dated copy beside every save, a read that falls back through them – and a file nothing can make
/// sense of is moved aside rather than overwritten.</para>
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly BackedUpJsonFile _file;

    /// <summary>
    /// How close together two dated copies may be taken. The settings are written on a timer while
    /// a slider is being dragged, so without a gap an evening's tuning would be the whole history
    /// the folder holds – and the state from before the tuning is the one worth having.
    /// </summary>
    public static readonly TimeSpan DefaultBackupInterval = TimeSpan.FromMinutes(5);

    public SettingsStore(string? path = null, int keepBackups = BackedUpJsonFile.DefaultKeep, TimeSpan? backupInterval = null)
    {
        _file = new BackedUpJsonFile(
            path ?? ProfilePaths.File("settings.json"),
            keepBackups,
            backupInterval ?? DefaultBackupInterval);
    }

    public string FilePath => _file.FilePath;
    public string BackupFolder => _file.BackupFolder;

    /// <summary>What the last <see cref="Load"/> ran into. Null before the first one.</summary>
    public SettingsLoadReport? LastLoad { get; private set; }

    /// <summary>Why the last copy failed, if it did. The save itself still went through.</summary>
    public string? LastBackupError => _file.LastBackupError;

    public AppSettings Load()
    {
        _file.TryRead(JsonOptions, out AppSettings? settings);
        settings ??= new AppSettings();

        // Migrate before normalising: normalisation clamps and fills in defaults, which would tidy
        // away the very old value a step needs to read.
        SettingsMigrationResult migration = SettingsMigrations.Apply(settings);
        settings.Normalize();

        LastLoad = new SettingsLoadReport(
            _file.RecoveredFromBackup,
            _file.QuarantinedPath,
            migration,
            // Whether there was a file, not whether it could be read. A settings file too broken to
            // parse is the opposite of a first run, and calling it one is how the start that most
            // needs a snapshot of the profile ends up being the one that skips it.
            StartedFresh: !_file.FoundOnDisk);
        return settings;
    }

    public void Save(AppSettings settings) => _file.Write(settings, JsonOptions);
}
