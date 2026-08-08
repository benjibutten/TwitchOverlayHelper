using System.IO;
using System.Text.Json;
using TwitchOverlayHelper.Storage;

namespace TwitchOverlayHelper.Nicknames;

/// <summary>
/// Nicknames on disk. A file of their own rather than a corner of settings.json: this is the one
/// thing in the app the user typed by hand and cannot get back from Twitch, so it is worth keeping
/// where a rewritten settings file cannot take it with it – and it is written on every edit rather
/// than on a timer, with a dated copy kept beside each save.
/// </summary>
public sealed class NicknameStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly BackedUpJsonFile _file;

    public NicknameStore(string? path = null, int keepBackups = BackedUpJsonFile.DefaultKeep)
    {
        _file = new BackedUpJsonFile(
            path ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TwitchOverlayHelper", "nicknames.json"),
            keepBackups);
    }

    public string FilePath => _file.FilePath;
    public string BackupFolder => _file.BackupFolder;

    /// <summary>True when the last load had to be answered from a copy, so the app can say so.</summary>
    public bool RecoveredFromBackup { get; private set; }

    public NicknameBook Load()
    {
        var book = new NicknameBook();
        if (_file.TryRead(JsonOptions, out NicknameFile? saved) && saved is not null)
            book.Replace(saved.Entries ?? []);
        RecoveredFromBackup = _file.RecoveredFromBackup;
        return book;
    }

    public void Save(NicknameBook book) => _file.Write(NicknameFile.Of(book.Snapshot()), JsonOptions);

    /// <summary>Why the last copy failed, if it did. The save itself still went through.</summary>
    public string? LastBackupError => _file.LastBackupError;
}
