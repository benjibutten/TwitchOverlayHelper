using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace TwitchOverlayHelper.Updates;

/// <summary>
/// The half of the update that runs in the copied executable, outside the install folder. It waits
/// for the real app to exit, swaps the files, and starts the new build. Every file it replaces is
/// backed up first, so a failure partway through puts the previous install back rather than leaving
/// half of one version and half of another.
/// </summary>
internal static class UpdateInstaller
{
    private const int FileOperationAttempts = 20;
    private static readonly TimeSpan FileOperationDelay = TimeSpan.FromMilliseconds(250);

    public static bool IsUpdateMode(string[] args) =>
        args.Contains("--apply-update", StringComparer.OrdinalIgnoreCase);

    public static bool IsCleanupMode(string[] args) =>
        args.Contains("--cleanup-update", StringComparer.OrdinalIgnoreCase);

    public static async Task RunCleanupAsync(string[] args)
    {
        try
        {
            int processId = int.Parse(GetRequiredArgument(args, "--process-id"));
            string workDirectory = GetValidatedWorkDirectory(Path.Combine(
                GetRequiredArgument(args, "--work-directory"),
                "update.zip"));
            await Task.Run(() => WaitForProcessToExit(processId));
            await DeleteWorkDirectoryAsync(workDirectory);
        }
        catch
        {
            // Cleanup is best effort and must never start the normal application.
        }
    }

    public static async Task RunAsync(string[] args)
    {
        var progressWindow = new UpdateProgressWindow(owner: null);
        progressWindow.Show();
        var progress = new Progress<UpdateProgress>(progressWindow.Report);
        try
        {
            await Task.Run(() => Apply(args, progress));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Uppdateringen kunde inte slutföras.\n\n{ex.Message}",
                "Twitch Overlay Helper – uppdatering",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            LaunchCleanupProcess(args);
        }
        finally
        {
            progressWindow.Close();
        }
    }

    private static void Apply(string[] args, IProgress<UpdateProgress> progress)
    {
        int processId = int.Parse(GetRequiredArgument(args, "--process-id"));
        string zipPath = Path.GetFullPath(GetRequiredArgument(args, "--zip-path"));
        string expectedHash = GetRequiredArgument(args, "--expected-hash");
        string installDirectory = Path.GetFullPath(GetRequiredArgument(args, "--install-directory"));
        string executablePath = Path.GetFullPath(GetRequiredArgument(args, "--executable-path"));
        string workDirectory = GetValidatedWorkDirectory(zipPath);

        progress.Report(new UpdateProgress("Väntar på att appen ska stängas…"));
        WaitForProcessToExit(processId);
        // Checked a second time, here rather than only before launch: between the two runs the file has
        // been sitting in a world-writable temp folder.
        progress.Report(new UpdateProgress("Kontrollerar uppdateringen…"));
        VerifyArchive(zipPath, expectedHash);

        string stagingDirectory = Path.Combine(workDirectory, "staging");
        string backupDirectory = Path.Combine(workDirectory, "backup");
        progress.Report(new UpdateProgress("Packar upp uppdateringen…"));
        ZipFile.ExtractToDirectory(zipPath, stagingDirectory, overwriteFiles: true);

        string executableName = Path.GetFileName(executablePath);
        if (!File.Exists(Path.Combine(stagingDirectory, executableName)))
            throw new InvalidDataException($"Uppdateringsarkivet innehåller inte {executableName}.");

        progress.Report(new UpdateProgress("Installerar uppdateringen…"));
        InstallFiles(stagingDirectory, installDirectory, backupDirectory);

        progress.Report(new UpdateProgress("Startar Twitch Overlay Helper igen…"));
        var restart = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = installDirectory
        };
        restart.ArgumentList.Add("--update-cleanup");
        restart.ArgumentList.Add(workDirectory);
        _ = Process.Start(restart)
            ?? throw new InvalidOperationException("Den uppdaterade appen kunde inte startas igen.");
    }

    internal static void InstallFiles(string stagingDirectory, string installDirectory, string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var installedFiles = new List<(string Destination, string? Backup)>();

        try
        {
            foreach (string sourcePath in Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(stagingDirectory, sourcePath);
                string destinationPath = Path.Combine(installDirectory, relativePath);
                string? destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (destinationDirectory is not null)
                    Directory.CreateDirectory(destinationDirectory);

                string? backupPath = null;
                if (File.Exists(destinationPath))
                {
                    backupPath = Path.Combine(backupDirectory, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    Retry(() => File.Copy(destinationPath, backupPath, overwrite: true));
                }

                ReplaceFile(sourcePath, destinationPath);
                installedFiles.Add((destinationPath, backupPath));
            }
        }
        catch
        {
            // Unwound newest first, so a file that existed in two versions ends up as the older one.
            for (int index = installedFiles.Count - 1; index >= 0; index--)
            {
                (string destinationPath, string? backupPath) = installedFiles[index];
                try
                {
                    if (backupPath is not null)
                        ReplaceFile(backupPath, destinationPath);
                    else
                        Retry(() => File.Delete(destinationPath));
                }
                catch { }
            }
            throw;
        }
    }

    /// <summary>
    /// Copies next to the destination and then moves over it. The move is the only step that touches
    /// the live file, so a half-written copy can never end up as the installed one.
    /// </summary>
    private static void ReplaceFile(string sourcePath, string destinationPath)
    {
        string incomingPath = Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $".{Path.GetFileName(destinationPath)}.update-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourcePath, incomingPath, overwrite: true);
            Retry(() => File.Move(incomingPath, destinationPath, overwrite: true));
        }
        finally
        {
            try { File.Delete(incomingPath); } catch { }
        }
    }

    /// <summary>
    /// Windows keeps files locked for a moment after a process exits – antivirus and Explorer both do
    /// it – so a lock is a reason to wait rather than to give up.
    /// </summary>
    private static void Retry(Action operation)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (IOException) when (attempt < FileOperationAttempts)
            {
                Thread.Sleep(FileOperationDelay);
            }
            catch (UnauthorizedAccessException) when (attempt < FileOperationAttempts)
            {
                Thread.Sleep(FileOperationDelay);
            }
        }
    }

    private static void WaitForProcessToExit(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.WaitForExit();
        }
        catch (ArgumentException)
        {
            // It already exited before the updater started waiting.
        }
    }

    private static void VerifyArchive(string zipPath, string expectedHash)
    {
        using FileStream stream = File.OpenRead(zipPath);
        string actualHash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Uppdateringen klarade inte SHA-256-kontrollen.");
    }

    private static string GetRequiredArgument(string[] args, string name)
    {
        int index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            throw new ArgumentException($"Uppdateringsargumentet {name} saknas.");
        return args[index + 1];
    }

    /// <summary>
    /// The arguments arrive on a command line, and the install step deletes and overwrites whatever
    /// they point at. Only a folder this app itself created directly under %TEMP% is accepted.
    /// </summary>
    private static string GetValidatedWorkDirectory(string zipPath)
    {
        string workDirectory = Path.GetDirectoryName(Path.GetFullPath(zipPath))
            ?? throw new InvalidOperationException("Uppdateringens arbetsmapp är ogiltig.");
        string tempDirectory = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string expectedPrefix = Path.Combine(tempDirectory, "TwitchOverlayHelper-update-");
        if (!workDirectory.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetDirectoryName(workDirectory)?.TrimEnd(Path.DirectorySeparatorChar),
                tempDirectory.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Uppdateringens arbetsmapp går inte att lita på.");
        }
        return workDirectory;
    }

    /// <summary>
    /// The failed updater cannot delete the folder it is itself running from, so the installed app is
    /// asked to do it once this process is gone.
    /// </summary>
    private static void LaunchCleanupProcess(string[] args)
    {
        try
        {
            string executablePath = Path.GetFullPath(GetRequiredArgument(args, "--executable-path"));
            string zipPath = Path.GetFullPath(GetRequiredArgument(args, "--zip-path"));
            string workDirectory = GetValidatedWorkDirectory(zipPath);
            var cleanup = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!
            };
            cleanup.ArgumentList.Add("--cleanup-update");
            cleanup.ArgumentList.Add("--process-id");
            cleanup.ArgumentList.Add(Environment.ProcessId.ToString());
            cleanup.ArgumentList.Add("--work-directory");
            cleanup.ArgumentList.Add(workDirectory);
            _ = Process.Start(cleanup);
        }
        catch
        {
            // The update failure has already been reported; cleanup is best effort.
        }
    }

    private static async Task DeleteWorkDirectoryAsync(string workDirectory)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            try
            {
                Directory.Delete(workDirectory, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch { }
        }
    }

    /// <summary>
    /// Called by the freshly started app: the updater that unpacked it is still exiting, so its temp
    /// folder is deleted in the background rather than on the startup path.
    /// </summary>
    public static void ScheduleCleanup(string[] args)
    {
        int index = Array.FindIndex(args, value => string.Equals(value, "--update-cleanup", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
            return;

        string workDirectory;
        try
        {
            workDirectory = GetValidatedWorkDirectory(Path.Combine(args[index + 1], "update.zip"));
        }
        catch
        {
            return;
        }
        _ = DeleteWorkDirectoryAsync(workDirectory);
    }
}
