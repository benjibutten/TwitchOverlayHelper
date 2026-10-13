using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;

namespace TwitchOverlayHelper.Storage;

/// <summary>
/// A JSON file that keeps its own history. Every save is atomic and leaves a dated copy behind, and
/// a main file that does not parse is answered from the newest copy that does rather than with an
/// empty document.
///
/// <para>The copy is of the state that was just written, not of the one it replaced, so the folder
/// holds a readable version from the very first save.</para>
/// </summary>
/// <param name="minimumBackupInterval">
/// How close together two copies may be taken. Zero – the default – copies on every save, which
/// suits a file written once per deliberate edit. A file written on a timer while a slider is being
/// dragged needs a gap, or a minute of tuning pushes out every copy from before it.
/// </param>
public sealed class BackedUpJsonFile(
    string filePath,
    int keepBackups = BackedUpJsonFile.DefaultKeep,
    TimeSpan minimumBackupInterval = default)
{
    /// <summary>Enough copies to reach back past a mistake that took a few saves to notice.</summary>
    public const int DefaultKeep = 20;

    private readonly Lock _lock = new();
    private readonly int _keep = Math.Max(1, keepBackups);
    private readonly TimeSpan _minimumInterval = minimumBackupInterval;

    public string FilePath { get; } = filePath;

    /// <summary>Where the copies live. A folder of its own so the file itself stays easy to find.</summary>
    public string BackupFolder { get; } =
        Path.Combine(Path.GetDirectoryName(filePath) ?? ".", "backups");

    /// <summary>
    /// Why the last copy could not be written, if it could not. A failed backup never fails the
    /// save.
    /// </summary>
    public string? LastBackupError { get; private set; }

    /// <summary>True when the last read had to fall back to a copy, so the caller can say so.</summary>
    public bool RecoveredFromBackup { get; private set; }

    /// <summary>
    /// Where an unreadable file was put out of the way, if the last read could make nothing of one.
    /// Null the rest of the time.
    /// </summary>
    public string? QuarantinedPath { get; private set; }

    /// <summary>Where files that parse as nothing are kept, clear of the copies.</summary>
    public string QuarantineFolder => Path.Combine(BackupFolder, "unreadable");

    /// <summary>
    /// Whether the last read found anything on disk at all, readable or not. No file means a first
    /// run; a file that would not parse means the user had something this app could not read.
    /// </summary>
    public bool FoundOnDisk { get; private set; }

    /// <summary>The copies on disk, newest first. The names are timestamps, so they sort by age.</summary>
    public IReadOnlyList<string> Backups()
    {
        try
        {
            if (!Directory.Exists(BackupFolder)) return [];
            string[] files = Directory.GetFiles(BackupFolder, BackupPattern);
            Array.Sort(files, StringComparer.Ordinal);
            Array.Reverse(files);
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Reads the document, falling back to the newest copy that still parses. A recovered document
    /// is written straight back to the main file: the fallback is a rescue, not a state to stay in.
    /// </summary>
    public bool TryRead<T>(JsonSerializerOptions options, [MaybeNullWhen(false)] out T value)
    {
        lock (_lock)
        {
            RecoveredFromBackup = false;
            QuarantinedPath = null;
            // Settled before the read, because the read is about to move the file it is asking about.
            FoundOnDisk = File.Exists(FilePath) || Backups().Count > 0;
            if (TryReadFile(FilePath, options, out value)) return true;

            foreach (string backup in Backups())
            {
                if (!TryReadFile(backup, options, out value)) continue;
                RecoveredFromBackup = true;
                TryRestore(backup);
                return true;
            }

            // Nothing on disk parsed, so the caller carries on with defaults and its first save
            // overwrites this file. It is the last trace of what the user had, so it is moved aside.
            TryQuarantine();
            value = default;
            return false;
        }
    }

    /// <summary>
    /// Saves the document and leaves a copy of it behind. A save that would change nothing is
    /// skipped, so a window that writes on every keystroke cannot push the useful copies out with
    /// identical ones.
    /// </summary>
    public void Write<T>(T value, JsonSerializerOptions options)
    {
        string json = JsonSerializer.Serialize(value, options);
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (Unchanged(json)) return;

            // The rename is what makes this atomic: a reader either sees the whole old file or the
            // whole new one, never the half that had been flushed when the power went.
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, FilePath, true);

            WriteBackup(json);
        }
    }

    private bool Unchanged(string json)
    {
        try { return File.Exists(FilePath) && File.ReadAllText(FilePath) == json; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Whether a copy would be too close on the heels of the last one. Judged by the newest copy's
    /// own timestamp rather than by a clock kept in memory, so the gap survives a restart.
    /// </summary>
    private bool TooSoonForBackup()
    {
        if (_minimumInterval <= TimeSpan.Zero) return false;
        try
        {
            IReadOnlyList<string> backups = Backups();
            if (backups.Count == 0) return false;
            return DateTime.Now - File.GetLastWriteTime(backups[0]) < _minimumInterval;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private void WriteBackup(string json)
    {
        if (TooSoonForBackup()) return;
        try
        {
            Directory.CreateDirectory(BackupFolder);
            File.WriteAllText(NextBackupPath(), json);
            Prune();
            LastBackupError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The save itself went through, so this is reported and never thrown: failing the save
            // because the copy failed would lose the very data being protected.
            LastBackupError = ex.Message;
        }
    }

    private string Stem => Path.GetFileNameWithoutExtension(FilePath);
    private string Extension => Path.GetExtension(FilePath);
    private string BackupPattern => $"{Stem}-*{Extension}";

    private string NextBackupPath()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        string candidate = Path.Combine(BackupFolder, $"{Stem}-{stamp}{Extension}");
        // Two saves inside the same millisecond would otherwise overwrite each other's copy. The
        // suffix is underscored and padded so the names still sort by age: a dash would sort the
        // second copy *before* the first, and the newest-first order is what recovery reads.
        for (int i = 1; File.Exists(candidate) && i < 100; i++)
            candidate = Path.Combine(BackupFolder, $"{Stem}-{stamp}_{i:00}{Extension}");
        return candidate;
    }

    private void Prune()
    {
        IReadOnlyList<string> backups = Backups();
        for (int i = _keep; i < backups.Count; i++)
        {
            try { File.Delete(backups[i]); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool TryReadFile<T>(string path, JsonSerializerOptions options, [MaybeNullWhen(false)] out T value)
    {
        value = default;
        try
        {
            if (!File.Exists(path)) return false;
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), options);
            return value is not null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void TryQuarantine()
    {
        try
        {
            // An empty file is moved aside like any other: a file truncated to zero bytes is what a
            // crash leaves behind, and left in place the load would report no trouble while the app
            // reset itself.
            if (!File.Exists(FilePath)) return;
            Directory.CreateDirectory(QuarantineFolder);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string path = Path.Combine(QuarantineFolder, $"{Stem}-{stamp}{Extension}");
            File.Move(FilePath, path, true);
            QuarantinedPath = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void TryRestore(string backup)
    {
        try { File.Copy(backup, FilePath, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
