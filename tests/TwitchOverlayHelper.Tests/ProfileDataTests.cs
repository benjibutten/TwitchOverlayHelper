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

        ProfileSnapshot snapshot = ProfileArchive.Create("v2026.8.1", profile.Path);

        Assert.Equal(ProfileSnapshotOutcome.Written, snapshot.Outcome);
        using ZipArchive zip = ZipFile.OpenRead(snapshot.Path!);
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
        ProfileSnapshot second = ProfileArchive.Create("v2", profile.Path);

        using ZipArchive zip = ZipFile.OpenRead(second.Path!);
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.StartsWith("snapshots/", StringComparison.Ordinal));
    }

    [Fact]
    public void SaysNothingWhenThereIsNoProfileYet()
    {
        using var parent = new TempFolder();

        ProfileSnapshot snapshot = ProfileArchive.Create("v1", Path.Combine(parent.Path, "aldrig-skapad"));

        Assert.Equal(ProfileSnapshotOutcome.Nothing, snapshot.Outcome);
        // Nothing to copy is not a failure – there is no reason for the next start to try again.
        Assert.True(snapshot.Complete);
    }

    // A file somebody else has open cannot go into the zip. The copy that comes out is not the
    // profile, and calling it one is how an upgrade quietly ends up with no usable snapshot.
    [Fact]
    public void MarksACopyThatCouldNotTakeEveryFile()
    {
        using var profile = new TempFolder();
        profile.Write("settings.json", """{"Channel":"demo"}""");
        string locked = profile.Write("nicknames.json", """{"Entries":[]}""");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ProfileSnapshot snapshot = ProfileArchive.Create("v2026.8.1", profile.Path);

            Assert.Equal(ProfileSnapshotOutcome.Partial, snapshot.Outcome);
            Assert.Equal(1, snapshot.Skipped);
            Assert.Equal(1, snapshot.Copied);
            // Said in the file name, because that is the only place anyone looks.
            Assert.Contains("delvis", snapshot.Path!);
            // And it is not counted as done, so the next start tries again.
            Assert.False(snapshot.Complete);
        }
    }

    [Fact]
    public void ARetryReplacesItsOwnIncompleteAttempt()
    {
        using var profile = new TempFolder();
        profile.Write("settings.json", """{"Channel":"demo"}""");
        string locked = profile.Write("nicknames.json", """{"Entries":[]}""");
        string folder = Path.Combine(profile.Path, "snapshots");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            ProfileArchive.Create("v2026.8.1", profile.Path);
        // The file is free now, the way it would be on the next start.
        ProfileSnapshot retry = ProfileArchive.Create("v2026.8.1", profile.Path);

        Assert.Equal(ProfileSnapshotOutcome.Written, retry.Outcome);
        // One snapshot, not two: retrying until the copy is whole must not spend the folder on
        // this one version and push out the snapshots from older ones.
        Assert.Single(ProfileArchive.List(folder));
        Assert.DoesNotContain("delvis", ProfileArchive.List(folder)[0]);
    }

    [Fact]
    public void SaysSoWhenNoCopyCouldBeWrittenAtAll()
    {
        using var profile = new TempFolder();
        profile.Write("settings.json", """{"Channel":"demo"}""");
        // A file where the snapshot folder should go: nothing can be written inside it.
        profile.Write("snapshots", "inte en mapp");

        ProfileSnapshot snapshot = ProfileArchive.Create("v2026.8.1", profile.Path);

        Assert.Equal(ProfileSnapshotOutcome.Failed, snapshot.Outcome);
        Assert.Null(snapshot.Path);
        Assert.False(snapshot.Complete);
    }
}

public sealed class ProfileVersionGuardTests
{
    [Fact]
    public void TakesACopyWhenTheVersionChanged()
    {
        using var profile = new TempFolder();
        profile.Write("settings.json", """{"Channel":"demo"}""");

        ProfileVersionResult result = ProfileVersionGuard.Check("v2026.7.1", "v2026.8.1", profile.Path);

        Assert.True(result.Changed);
        Assert.Equal(ProfileSnapshotOutcome.Written, result.Snapshot.Outcome);
        // Named after the version whose work is being preserved, not the one doing the preserving.
        Assert.Contains("v2026.7.1", result.Snapshot.Path!);
        Assert.True(result.MayStamp);
    }

    [Fact]
    public void TakesACopyOfAProfileFromBeforeTheStampExisted()
    {
        using var profile = new TempFolder();
        profile.Write("settings.json", """{"Channel":"demo"}""");

        ProfileVersionResult result = ProfileVersionGuard.Check("", "v2026.8.1", profile.Path);

        Assert.True(result.Changed);
        Assert.Equal(ProfileSnapshotOutcome.Written, result.Snapshot.Outcome);
    }

    // The regression this guard exists for: whether there is a profile worth copying is a question
    // about the folder, and answering it from "did settings.json load" skipped the snapshot on
    // exactly the start where the settings had gone missing.
    [Fact]
    public void TakesACopyEvenWhenTheSettingsFileIsBeyondReading()
    {
        using var profile = new TempFolder();
        profile.Write("settings.json", "{inte-json");
        profile.Write("nicknames.json", """{"Entries":[{"Login":"kompis","Name":"Kompis"}]}""");

        ProfileVersionResult result = ProfileVersionGuard.Check("v2026.7.1", "v2026.8.1", profile.Path);

        Assert.Equal(ProfileSnapshotOutcome.Written, result.Snapshot.Outcome);
        using ZipArchive zip = ZipFile.OpenRead(result.Snapshot.Path!);
        // Both of them: the broken file is evidence, and the nicknames are the thing worth saving.
        Assert.Contains(zip.Entries, entry => entry.FullName == "settings.json");
        Assert.Contains(zip.Entries, entry => entry.FullName == "nicknames.json");
    }

    [Fact]
    public void LeavesTheProfileAloneOnAnOrdinaryStart()
    {
        using var profile = new TempFolder();
        profile.Write("settings.json", """{"Channel":"demo"}""");

        ProfileVersionResult result = ProfileVersionGuard.Check("v2026.8.1", "v2026.8.1", profile.Path);

        Assert.False(result.Changed);
        Assert.Null(result.Snapshot.Path);
        Assert.False(Directory.Exists(Path.Combine(profile.Path, "snapshots")));
    }

    [Fact]
    public void TakesNoCopyOfAProfileThatDoesNotExistYet()
    {
        using var parent = new TempFolder();

        ProfileVersionResult result = ProfileVersionGuard.Check("", "v2026.8.1", Path.Combine(parent.Path, "ny"));

        Assert.Equal(ProfileSnapshotOutcome.Nothing, result.Snapshot.Outcome);
        // A first run has nothing to lose, so the version may be recorded straight away.
        Assert.True(result.MayStamp);
    }

    [Fact]
    public void RefusesToRecordTheVersionWhenTheCopyDidNotHappen()
    {
        using var profile = new TempFolder();
        profile.Write("settings.json", """{"Channel":"demo"}""");
        profile.Write("snapshots", "inte en mapp");

        ProfileVersionResult result = ProfileVersionGuard.Check("v2026.7.1", "v2026.8.1", profile.Path);

        Assert.Equal(ProfileSnapshotOutcome.Failed, result.Snapshot.Outcome);
        // Stamping here would mean this upgrade never gets a snapshot at all: the next start would
        // see the versions matching and never try again.
        Assert.False(result.MayStamp);
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
