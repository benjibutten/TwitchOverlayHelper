using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using TwitchOverlayHelper.Updates;

namespace TwitchOverlayHelper.Tests;

public sealed class GitHubUpdateServiceTests
{
    [Theory]
    [InlineData("v2026.7.12", 2026, 7, 12)]
    [InlineData("2025.11.3", 2025, 11, 3)]
    public void TryParseVersion_ParsesReleaseTags(string tag, int major, int minor, int build)
    {
        Assert.True(GitHubUpdateService.TryParseVersion(tag, out Version? version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Fact]
    public void GetReleaseZipName_MatchesTheNameTheReleaseWorkflowUploads()
    {
        string result = GitHubUpdateService.GetReleaseZipName(new Version(2026, 7, 12));

        Assert.Equal("TwitchOverlayHelper-2026.7.12-win-x64.zip", result);
    }

    [Fact]
    public void ParseChecksum_AcceptsStandardSha256File()
    {
        string hash = new('a', 64);

        string result = GitHubUpdateService.ParseChecksum($"{hash}  TwitchOverlayHelper-2026.7.12-win-x64.zip");

        Assert.Equal(hash.ToUpperInvariant(), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaz")]
    public void ParseChecksum_RejectsInvalidContent(string value)
    {
        Assert.Throws<InvalidDataException>(() => GitHubUpdateService.ParseChecksum(value));
    }

    [Fact]
    public async Task CheckAsync_UsesLatestReleaseAndSelectsMatchingAssets()
    {
        const string zipUrl = "https://github.com/benjibutten/TwitchOverlayHelper/releases/download/v2026.7.12/TwitchOverlayHelper-2026.7.12-win-x64.zip";
        const string checksumUrl = $"{zipUrl}.sha256";
        string? requestedUrl = null;
        string json = $$"""
            {
              "tag_name": "v2026.7.12",
              "html_url": "https://github.com/benjibutten/TwitchOverlayHelper/releases/tag/v2026.7.12",
              "assets": [
                { "name": "TwitchOverlayHelper-2026.7.11-win-x64.zip", "browser_download_url": "https://example.test/wrong.zip" },
                { "name": "TwitchOverlayHelper-2026.7.12-win-x64.zip", "browser_download_url": "{{zipUrl}}" },
                { "name": "TwitchOverlayHelper-2026.7.12-win-x64.zip.sha256", "browser_download_url": "{{checksumUrl}}" }
              ]
            }
            """;
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requestedUrl = request.RequestUri?.AbsoluteUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }));
        string stateRoot = Path.Combine(Path.GetTempPath(), $"TwitchOverlayHelper-update-check-test-{Guid.NewGuid():N}");

        try
        {
            var service = new GitHubUpdateService(client, Path.Combine(stateRoot, "state.txt"));
            UpdateInfo? update = await service.CheckAsync(new Version(2026, 7, 11), force: true);

            Assert.Equal("https://api.github.com/repos/benjibutten/TwitchOverlayHelper/releases/latest", requestedUrl);
            Assert.NotNull(update);
            Assert.Equal(new Uri(zipUrl), update!.DownloadUri);
            Assert.Equal(new Uri(checksumUrl), update.ChecksumUri);
        }
        finally
        {
            if (Directory.Exists(stateRoot))
                Directory.Delete(stateRoot, recursive: true);
        }
    }

    /// <summary>
    /// The one case where offering an update would be worse than staying quiet: a release the app has
    /// no verified way to install.
    /// </summary>
    [Fact]
    public async Task CheckAsync_IgnoresReleaseWithoutChecksumAsset()
    {
        string json = """
            {
              "tag_name": "v2026.7.12",
              "html_url": "https://github.com/benjibutten/TwitchOverlayHelper/releases/tag/v2026.7.12",
              "assets": [
                { "name": "TwitchOverlayHelper-2026.7.12-win-x64.zip", "browser_download_url": "https://example.test/app.zip" }
              ]
            }
            """;
        using var client = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        string stateRoot = Path.Combine(Path.GetTempPath(), $"TwitchOverlayHelper-update-check-test-{Guid.NewGuid():N}");

        try
        {
            var service = new GitHubUpdateService(client, Path.Combine(stateRoot, "state.txt"));

            Assert.Null(await service.CheckAsync(new Version(2026, 7, 11), force: true));
        }
        finally
        {
            if (Directory.Exists(stateRoot))
                Directory.Delete(stateRoot, recursive: true);
        }
    }

    [Fact]
    public async Task VerifySha256Async_AcceptsMatchAndRejectsMismatch()
    {
        byte[] download = Encoding.UTF8.GetBytes("TwitchOverlayHelper release archive");
        string expectedHash = Convert.ToHexString(SHA256.HashData(download));

        await GitHubUpdateService.VerifySha256Async(new MemoryStream(download), expectedHash);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GitHubUpdateService.VerifySha256Async(new MemoryStream(download), new string('0', 64)));
    }

    [Fact]
    public void InstallFiles_UpdatesReleaseFilesAndPreservesOtherFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), $"TwitchOverlayHelper-update-test-{Guid.NewGuid():N}");
        string staging = Path.Combine(root, "staging");
        string install = Path.Combine(root, "install");
        string backup = Path.Combine(root, "backup");
        try
        {
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(install);
            File.WriteAllText(Path.Combine(staging, "TwitchOverlayHelper.exe"), "new version");
            File.WriteAllText(Path.Combine(staging, "LICENSE.txt"), "new licence");
            File.WriteAllText(Path.Combine(install, "TwitchOverlayHelper.exe"), "old version");
            File.WriteAllText(Path.Combine(install, "user-file.txt"), "keep me");

            UpdateInstaller.InstallFiles(staging, install, backup);

            Assert.Equal("new version", File.ReadAllText(Path.Combine(install, "TwitchOverlayHelper.exe")));
            Assert.Equal("new licence", File.ReadAllText(Path.Combine(install, "LICENSE.txt")));
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(install, "user-file.txt")));
            Assert.Equal("old version", File.ReadAllText(Path.Combine(backup, "TwitchOverlayHelper.exe")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
            _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
