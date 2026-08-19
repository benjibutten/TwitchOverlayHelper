using System.IO;
using System.IO.Compression;

namespace TwitchOverlayHelper.Storage;

/// <summary>
/// A copy of the whole profile folder in one zip.
///
/// <para>The per-file backups next to each store answer the everyday mishap – a settings file
/// truncated by a power cut, a nickname list emptied by mistake. They cannot answer the one that
/// happens at a new version: a build that reads the old file, understands half of it, and writes
/// back what it understood. By the time that is noticed the dated copies are all post-upgrade too.
/// So the moment the app first runs under a version it has not run under before, everything is
/// copied aside exactly as the previous version left it – before a single migration or save
/// touches it.</para>
///
/// <para>A plain zip on purpose: getting the old settings back has to be possible from Utforskaren
/// on a bad evening, without the app that wrote it being able to start.</para>
/// </summary>
public static class ProfileArchive
{
    /// <summary>Snapshots to keep. Roughly a year of releases at the rate this app ships.</summary>
    public const int DefaultKeep = 8;

    /// <summary>
    /// What is not worth copying: things the app refetches or rewrites by itself. Caches are the
    /// bulk of the folder and the least of the loss, and the logs are already dated files that a
    /// new version does not rewrite.
    /// </summary>
    private static readonly string[] SkippedFolders = ["snapshots", "logs", "ttscache", "namecache"];

    /// <summary>
    /// Ceiling per file. Nothing the app writes comes close; this is here so a spritesheet folder
    /// somebody filled with video cannot turn every upgrade into a minutes-long copy.
    /// </summary>
    private const long MaxFileBytes = 32L * 1024 * 1024;

    /// <summary>The snapshots on disk, newest first. Names start with a timestamp, so they sort by age.</summary>
    public static IReadOnlyList<string> List(string? snapshotFolder = null)
    {
        string folder = snapshotFolder ?? ProfilePaths.Snapshots;
        try
        {
            if (!Directory.Exists(folder)) return [];
            string[] files = Directory.GetFiles(folder, "profile-*.zip");
            Array.Sort(files, StringComparer.Ordinal);
            Array.Reverse(files);
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Copies the profile folder into a new zip and prunes the oldest ones away.
    /// </summary>
    /// <param name="label">
    /// What the snapshot was taken for – a version string, or "manuell". Ends up in the file name,
    /// which is the only thing anyone reads when picking which one to unpack.
    /// </param>
    /// <returns>The path written, or null when there was nothing to copy or it could not be done.</returns>
    public static string? Create(string label, string? profileFolder = null, string? snapshotFolder = null, int keep = DefaultKeep)
    {
        string root = profileFolder ?? ProfilePaths.Root;
        string folder = snapshotFolder ?? (profileFolder is null ? ProfilePaths.Snapshots : Path.Combine(profileFolder, "snapshots"));
        try
        {
            if (!Directory.Exists(root)) return null;
            List<string> files = Contents(root);
            if (files.Count == 0) return null;

            Directory.CreateDirectory(folder);
            string path = NextPath(folder, label);
            // Built beside the target and moved in: a snapshot interrupted halfway is not a zip
            // anyone should later find and trust.
            string temporary = path + ".tmp";
            Write(temporary, root, files);
            File.Move(temporary, path, true);
            Prune(folder, keep);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static void Write(string zipPath, string root, List<string> files)
    {
        using var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (string file in files)
        {
            try
            {
                zip.CreateEntryFromFile(file, Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.Optimal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One file being held open – a log the app itself has, a pet open in an editor –
                // is no reason to lose the other forty.
            }
        }
    }

    private static List<string> Contents(string root)
    {
        var files = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            string top = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (SkippedFolders.Contains(top, StringComparer.OrdinalIgnoreCase)) continue;
            if (relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            try { if (new FileInfo(file).Length > MaxFileBytes) continue; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            files.Add(file);
        }
        return files;
    }

    private static string NextPath(string folder, string label)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string safe = Sanitize(label);
        string candidate = Path.Combine(folder, $"profile-{stamp}-{safe}.zip");
        for (int i = 1; File.Exists(candidate) && i < 100; i++)
            candidate = Path.Combine(folder, $"profile-{stamp}_{i:00}-{safe}.zip");
        return candidate;
    }

    private static string Sanitize(string label)
    {
        string trimmed = string.Concat(label.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'));
        return trimmed.Length == 0 ? "okand" : trimmed[..Math.Min(trimmed.Length, 32)];
    }

    private static void Prune(string folder, int keep)
    {
        IReadOnlyList<string> snapshots = List(folder);
        for (int i = Math.Max(1, keep); i < snapshots.Count; i++)
        {
            try { File.Delete(snapshots[i]); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
