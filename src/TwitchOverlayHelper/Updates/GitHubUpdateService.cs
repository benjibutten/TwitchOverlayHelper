using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace TwitchOverlayHelper.Updates;

internal sealed record UpdateInfo(
    Version Version,
    string TagName,
    Uri DownloadUri,
    Uri ChecksumUri,
    Uri ReleasePageUri);

internal sealed record UpdateProgress(string Status, double? Percentage = null);

/// <summary>
/// Asks GitHub whether a newer release exists and, when the user says yes, hands the work over to a
/// copy of this executable running in a temp folder. The running app cannot overwrite its own files
/// while it is running, so the copy is what waits for this process to exit and swaps the install.
/// </summary>
internal sealed class GitHubUpdateService
{
    /// <summary>How long an automatic check waits before hitting GitHub again.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    private readonly HttpClient _httpClient;
    private readonly string _statePath;

    public GitHubUpdateService(HttpClient? httpClient = null, string? statePath = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TwitchOverlayHelper-Updater/1.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _statePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TwitchOverlayHelper",
            "update-check.txt");
    }

    public async Task<UpdateInfo?> CheckAsync(Version currentVersion, bool force, CancellationToken cancellationToken = default)
    {
        if (!force && !IsCheckDue())
            return null;

        using var response = await _httpClient.GetAsync(
            "https://api.github.com/repos/benjibutten/TwitchOverlayHelper/releases/latest",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        SaveCheckTime();

        JsonElement root = document.RootElement;
        string tagName = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!TryParseVersion(tagName, out Version? releaseVersion) || releaseVersion <= currentVersion)
            return null;

        string zipName = GetReleaseZipName(releaseVersion!);
        string checksumName = $"{zipName}.sha256";
        Uri? zipUri = null;
        Uri? checksumUri = null;

        foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
        {
            string? name = asset.GetProperty("name").GetString();
            string? url = asset.GetProperty("browser_download_url").GetString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                continue;

            if (string.Equals(name, zipName, StringComparison.OrdinalIgnoreCase))
                zipUri = uri;
            else if (string.Equals(name, checksumName, StringComparison.OrdinalIgnoreCase))
                checksumUri = uri;
        }

        // A release without both assets is not installable. Saying "no update" is the honest answer:
        // the alternative is offering an update that would fail halfway through the download.
        if (zipUri is null || checksumUri is null)
            return null;

        string pageUrl = root.GetProperty("html_url").GetString()
            ?? "https://github.com/benjibutten/TwitchOverlayHelper/releases/latest";
        return new UpdateInfo(releaseVersion!, tagName, zipUri, checksumUri, new Uri(pageUrl));
    }

    public async Task LaunchInstallerAsync(
        UpdateInfo update,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string workDirectory = Path.Combine(Path.GetTempPath(), $"TwitchOverlayHelper-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        string zipPath = Path.Combine(workDirectory, "update.zip");
        string updaterPath = Path.Combine(workDirectory, "TwitchOverlayHelper.Update.exe");

        try
        {
            progress?.Report(new UpdateProgress("Hämtar uppdateringen…", 0));
            await DownloadFileAsync(update.DownloadUri, zipPath, progress, cancellationToken);
            progress?.Report(new UpdateProgress("Kontrollerar hämtningen…"));
            string checksumText = await _httpClient.GetStringAsync(update.ChecksumUri, cancellationToken);
            string expectedHash = ParseChecksum(checksumText);
            await using (var zipStream = File.OpenRead(zipPath))
            {
                await VerifySha256Async(zipStream, expectedHash, cancellationToken);
            }

            string executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Kunde inte avgöra var appens exe-fil ligger.");
            string installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

            // Installs under Program Files need an elevated updater. Asking for it only when the probe
            // says the folder is read-only keeps the common case (a folder in the user's profile) free
            // of UAC prompts.
            bool requiresElevation = !CanWriteToDirectory(installDirectory);

            progress?.Report(new UpdateProgress("Förbereder installationen…"));
            File.Copy(executablePath, updaterPath);

            var startInfo = new ProcessStartInfo(updaterPath)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = workDirectory
            };
            if (requiresElevation)
                startInfo.Verb = "runas";

            startInfo.ArgumentList.Add("--apply-update");
            startInfo.ArgumentList.Add("--process-id");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("--zip-path");
            startInfo.ArgumentList.Add(zipPath);
            startInfo.ArgumentList.Add("--expected-hash");
            startInfo.ArgumentList.Add(expectedHash);
            startInfo.ArgumentList.Add("--install-directory");
            startInfo.ArgumentList.Add(installDirectory);
            startInfo.ArgumentList.Add("--executable-path");
            startInfo.ArgumentList.Add(executablePath);
            progress?.Report(new UpdateProgress(
                requiresElevation ? "Väntar på godkännande från Windows…" : "Startar installationen…"));
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows kunde inte starta uppdateringsprogrammet.");
        }
        catch
        {
            try { Directory.Delete(workDirectory, recursive: true); } catch { }
            throw;
        }
    }

    internal static bool TryParseVersion(string tagName, out Version? version) =>
        Version.TryParse(tagName.Trim().TrimStart('v', 'V'), out version);

    internal static string GetReleaseZipName(Version version) =>
        $"TwitchOverlayHelper-{version}-win-x64.zip";

    internal static string ParseChecksum(string value)
    {
        string hash = value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? string.Empty;
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Släppets checksumfil är ogiltig.");
        return hash.ToUpperInvariant();
    }

    internal static async Task VerifySha256Async(
        Stream stream,
        string expectedHash,
        CancellationToken cancellationToken = default)
    {
        string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Den hämtade uppdateringen klarade inte SHA-256-kontrollen.");
    }

    private async Task DownloadFileAsync(
        Uri uri,
        string path,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(path);
        long? totalBytes = response.Content.Headers.ContentLength;
        var buffer = new byte[81920];
        long downloadedBytes = 0;
        int lastReportedPercentage = -1;
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            downloadedBytes += bytesRead;
            if (totalBytes > 0)
            {
                int percentage = (int)Math.Min(100, downloadedBytes * 100 / totalBytes.Value);
                if (percentage != lastReportedPercentage)
                {
                    lastReportedPercentage = percentage;
                    progress?.Report(new UpdateProgress("Hämtar uppdateringen…", percentage));
                }
            }
        }
    }

    private bool IsCheckDue()
    {
        try
        {
            return !File.Exists(_statePath)
                || !DateTimeOffset.TryParse(File.ReadAllText(_statePath), out var lastCheck)
                || DateTimeOffset.UtcNow - lastCheck >= CheckInterval;
        }
        catch { return true; }
    }

    private void SaveCheckTime()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch { }
    }

    private static bool CanWriteToDirectory(string directory)
    {
        string probe = Path.Combine(directory, $".update-write-test-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch { return false; }
        finally { try { File.Delete(probe); } catch { } }
    }
}
