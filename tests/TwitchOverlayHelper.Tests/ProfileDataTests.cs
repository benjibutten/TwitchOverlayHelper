using System.IO.Compression;
using TwitchOverlayHelper.Storage;

namespace TwitchOverlayHelper.Tests;

public sealed class ProfileArchiveTests
{
    [Fact]
    public void CopiesTheProfileAndLeavesTheCachesOut()
    {
        using var profile = new TempFolder();
        profile.Write("settings.json", """{"Channel":"demo"}""");
        profile.Write("nicknames.json", "{}");
        profile.Write(Path.Combine("pets", "owly", "pet.json"), "{}");
        profile.Write(Path.Combine("logs", "app-2026-08-19.log"), "hej");
        profile.Write(Path.Combine("ttscache", "clip.mp3"), "ljud");

        string? zipPath = ProfileArchive.Create("v2026.8.1", profile.Path);

        Assert.NotNull(zipPath);
        using ZipArchive zip = ZipFile.OpenRead(zipPath!);
        string[] entries = zip.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("settings.json", entries);
        Assert.Contains("nicknames.json", entries);
        Assert.Contains("pets/owly/pet.json", entries);
        // Refetchable and rewritten by the app itself; copying them would make every upgrade wait
        // on the largest folder for the least valuable bytes.
        Assert.DoesNotContain(entries, name => name.StartsWith("logs/", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, name => name.StartsWith("ttscache/", StringComparison.Ordinal));
    }

    [Fact]
    public void KeepsTheNewestSnapshotsOnly()
    {
        using var profile = new TempFolder();
        profile.Write("settings.json", """{"Channel":"demo"}""");

        for (int i = 0; i < 4; i++) ProfileArchive.Create($"v2026.8.{i}", profile.Path, keep: 2);

        IReadOnlyList<string> snapshots = ProfileArchive.List(Path.Combine(profile.Path, "snapshots"));
        Assert.Equal(2, snapshots.Count);
        // Newest first, and the newest is the last one taken.
        Assert.Contains("v2026.8.3", snapshots[0]);
    }

    [Fact]
    public void SnapshotsAreNotThemselvesSnapshotted()
    {
        using var profile = new TempFolder();
        profile.Write("settings.json", """{"Channel":"demo"}""");

        ProfileArchive.Create("v1", profile.Path);
        string? second = ProfileArchive.Create("v2", profile.Path);

        using ZipArchive zip = ZipFile.OpenRead(second!);
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.StartsWith("snapshots/", StringComparison.Ordinal));
    }

    [Fact]
    public void SaysNothingWhenThereIsNoProfileYet()
    {
        using var parent = new TempFolder();
        Assert.Null(ProfileArchive.Create("v1", Path.Combine(parent.Path, "aldrig-skapad")));
    }
}

public sealed class ProfileVersionGuardTests
{
    [Fact]
    public void TakesACopyWhenTheVersionChanged()
    {
        using var profile = new TempFolder();
        profile.Write("settings.json", """{"Channel":"demo"}""");

        ProfileVersionResult result = ProfileVersionGuard.Check("v2026.7.1", "v2026.8.1", profileExisted: true, profile.Path);

        Assert.True(result.Changed);
        Assert.NotNull(result.SnapshotPath);
        // Named after the version whose work is being preserved, not the one doing the preserving.
        Assert.Contains("v2026.7.1", result.SnapshotPath!);
    }

    [Fact]
    public void TakesACopyOfAProfileFromBeforeTheStampExisted()
    {
        using var profile = new TempFolder();
        profile.Write("settings.json", """{"Channel":"demo"}""");

        ProfileVersionResult result = ProfileVersionGuard.Check("", "v2026.8.1", profileExisted: true, profile.Path);

        Assert.True(result.Changed);
        Assert.NotNull(result.SnapshotPath);
    }

    [Fact]
    public void LeavesTheProfileAloneOnAnOrdinaryStart()
    {
        using var profile = new TempFolder();
        profile.Write("settings.json", """{"Channel":"demo"}""");

        ProfileVersionResult result = ProfileVersionGuard.Check("v2026.8.1", "v2026.8.1", profileExisted: true, profile.Path);

        Assert.False(result.Changed);
        Assert.Null(result.SnapshotPath);
        Assert.False(Directory.Exists(Path.Combine(profile.Path, "snapshots")));
    }

    [Fact]
    public void TakesNoCopyOfAProfileThatDoesNotExistYet()
    {
        using var profile = new TempFolder();

        ProfileVersionResult result = ProfileVersionGuard.Check("", "v2026.8.1", profileExisted: false, profile.Path);

        Assert.False(result.Changed);
        Assert.Null(result.SnapshotPath);
    }
}

/// <summary>A folder of its own per test, removed afterwards however the test ends.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string Write(string relative, string content)
    {
        string full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
