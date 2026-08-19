using System.IO;
using System.IO.Compression;

namespace TwitchOverlayHelper.Storage;

/// <summary>How a snapshot went. The caller stamps the version only when there is nothing left to try.</summary>
public enum ProfileSnapshotOutcome
{
    /// <summary>There was nothing to copy – a first run, before the profile holds anything.</summary>
    Nothing,

    /// <summary>Every file went in.</summary>
    Written,

    /// <summary>A zip was written, but a file could not be read into it and is missing from the copy.</summary>
    Partial,

    /// <summary>No zip was written at all.</summary>
    Failed
}

/// <param name="Copied">Files in the zip.</param>
/// <param name="Skipped">Files that should have been in it and are not.</param>
public sealed record ProfileSnapshot(ProfileSnapshotOutcome Outcome, string? Path, int Copied, int Skipped)
{
    public static readonly ProfileSnapshot Nothing = new(ProfileSnapshotOutcome.Nothing, null, 0, 0);

    /// <summary>
    /// Whether this counts as done. An incomplete copy does not: the point of the snapshot is that
    /// it is the whole profile, so a start that could not manage that should try again at the next
    /// one rather than record the version as safely copied.
    /// </summary>
    public bool Complete => Outcome is ProfileSnapshotOutcome.Written or ProfileSnapshotOutcome.Nothing;
}

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
    /// Marks a zip that is missing a file it should have had. In the name rather than in a note
    /// beside it, so that the one thing anybody reads – the file listing – says which copies can be
    /// trusted whole, and so a retry can find its own earlier attempt and replace it.
    /// </summary>
    private const string PartialSuffix = "-delvis";

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
    public static ProfileSnapshot Create(string label, string? profileFolder = null, string? snapshotFolder = null, int keep = DefaultKeep)
    {
        string root = profileFolder ?? ProfilePaths.Root;
        string folder = snapshotFolder ?? (profileFolder is null ? ProfilePaths.Snapshots : Path.Combine(profileFolder, "snapshots"));
        try
        {
            if (!Directory.Exists(root)) return ProfileSnapshot.Nothing;
            List<string> files = Contents(root);
            if (files.Count == 0) return ProfileSnapshot.Nothing;

            Directory.CreateDirectory(folder);
            // Built under a name of its own and moved in: a snapshot interrupted halfway is not a
            // zip anyone should later find and trust, and the name it ends up with is not known
            // until the copying has said how much of the profile it managed.
            string building = Path.Combine(folder, "profile.building");
            int skipped = Write(building, root, files);
            string path = NextPath(folder, label, partial: skipped > 0);

            // An earlier incomplete attempt at this same version is replaced rather than added to.
            // Every start retries until one copy is whole, and without this the retries would fill
            // the folder and push out the snapshots from older versions – which are the ones with
            // anything left to say.
            RemovePartial(folder, label);
            File.Move(building, path, true);
            Prune(folder, keep);
            return new ProfileSnapshot(
                skipped > 0 ? ProfileSnapshotOutcome.Partial : ProfileSnapshotOutcome.Written,
                path,
                files.Count - skipped,
                skipped);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ProfileSnapshot(ProfileSnapshotOutcome.Failed, null, 0, 0);
        }
    }

    /// <summary>Copies the files into a new zip, and answers how many of them it could not read.</summary>
    private static int Write(string zipPath, string root, List<string> files)
    {
        int skipped = 0;
        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (string file in files)
            {
                try
                {
                    zip.CreateEntryFromFile(file, Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.Optimal);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // One file being held open elsewhere is no reason to lose the other forty – but
                    // it does mean this copy is not the whole profile, and saying so is what makes
                    // the next start try again instead of trusting it.
                    skipped++;
                }
            }
        }
        return skipped;
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

    private static string NextPath(string folder, string label, bool partial)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string tail = Sanitize(label) + (partial ? PartialSuffix : string.Empty);
        string candidate = Path.Combine(folder, $"profile-{stamp}-{tail}.zip");
        for (int i = 1; File.Exists(candidate) && i < 100; i++)
            candidate = Path.Combine(folder, $"profile-{stamp}_{i:00}-{tail}.zip");
        return candidate;
    }

    private static void RemovePartial(string folder, string label)
    {
        try
        {
            foreach (string stale in Directory.GetFiles(folder, $"profile-*-{Sanitize(label)}{PartialSuffix}.zip"))
            {
                try { File.Delete(stale); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
