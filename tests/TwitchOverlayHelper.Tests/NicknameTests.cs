using System.Text.Json;
using TwitchOverlayHelper.Nicknames;
using TwitchOverlayHelper.Storage;

namespace TwitchOverlayHelper.Tests;

public sealed class NicknameBookTests
{
    [Fact]
    public void FindsANicknameByIdAndByLogin()
    {
        var book = new NicknameBook();
        book.Set("7", "kajsa", "Kajsa från jobbet");

        Assert.Equal("Kajsa från jobbet", book.For("7", "nånannan"));
        Assert.Equal("Kajsa från jobbet", book.For(null, "KAJSA"));
        Assert.Null(book.For("8", "pelle"));
    }

    [Fact]
    public void DoesNotUseARecycledLoginWhenTheIdIsKnown()
    {
        var book = new NicknameBook();
        book.Set("7", "kajsa", "Grannen");

        Assert.Null(book.For("8", "kajsa"));
        Assert.Equal("Grannen", book.For(null, "kajsa"));
    }

    // The id is the half that survives a name change, so a chatter who renames keeps the nickname –
    // and the login they left behind stops answering to it.
    [Fact]
    public void FollowsTheChatterWhenTheirLoginChanges()
    {
        var book = new NicknameBook();
        book.Set("7", "kajsa", "Grannen");

        book.Set("7", "kajsa2", "Grannen");

        Assert.Equal(1, book.Count);
        Assert.Equal("Grannen", book.For("7", null));
        Assert.Equal("Grannen", book.For(null, "kajsa2"));
        Assert.Null(book.For(null, "kajsa"));
    }

    // A line from the sample chat carries no id at all, and a nickname given there still has to work.
    [Fact]
    public void NamesAChatterThatOnlyHasALogin()
    {
        var book = new NicknameBook();
        book.Set(string.Empty, "kajsa_92", "Systern");

        Assert.Equal("Systern", book.For(string.Empty, "kajsa_92"));
        Assert.True(book.Remove(string.Empty, "kajsa_92"));
        Assert.Null(book.For(string.Empty, "kajsa_92"));
    }

    [Fact]
    public void RefusesANicknameWithNobodyToPutItOn()
    {
        var book = new NicknameBook();

        Assert.Null(book.Set(null, null, "Ingen"));
        Assert.Equal(0, book.Count);
    }

    // Blank text is how a nickname is taken back: there is no separate call for it, so a field that
    // was cleared has to mean the same as pressing remove.
    [Fact]
    public void TreatsABlankNicknameAsRemoval()
    {
        var book = new NicknameBook();
        book.Set("7", "kajsa", "Grannen");

        Assert.Null(book.Set("7", "kajsa", "   "));
        Assert.Null(book.For("7", "kajsa"));
        Assert.Equal(0, book.Count);
    }

    [Fact]
    public void CleansLineBreaksAndCapsTheLength()
    {
        Assert.Equal("Kajsa från jobbet", NicknameBook.Clean("  Kajsa\r\n  från\tjobbet  "));
        Assert.Equal(NicknameBook.MaxLength, NicknameBook.Clean(new string('x', 200)).Length);
        Assert.Equal(string.Empty, NicknameBook.Clean("\n\t "));
    }

    // Cutting a name at the limit must not slice an emoji in half and leave a lone surrogate.
    [Fact]
    public void NeverCutsAnEmojiInHalf()
    {
        string cleaned = NicknameBook.Clean(new string('x', NicknameBook.MaxLength - 1) + "🎉");

        Assert.Equal(NicknameBook.MaxLength - 1, cleaned.Length);
        Assert.DoesNotContain(cleaned, character => char.IsSurrogate(character));
    }

    [Fact]
    public void AnnouncesEveryChangeIncludingRemovals()
    {
        var book = new NicknameBook();
        var heard = new List<Nickname>();
        book.Changed += heard.Add;

        book.Set("7", "kajsa", "Grannen");
        book.Remove("7", null);

        Assert.Equal(2, heard.Count);
        Assert.Equal("Grannen", heard[0].Text);
        Assert.True(heard[1].IsRemoval);
        // The removal carries the keys the entry was found under, so a dock can drop it from both
        // of its lookups even though only the id was named.
        Assert.Equal("kajsa", heard[1].Login);
    }

    // Saying the same thing twice is not a change, and a save-and-broadcast per keystroke would be.
    [Fact]
    public void StaysQuietWhenNothingActuallyChanged()
    {
        var book = new NicknameBook();
        book.Set("7", "kajsa", "Grannen");

        int changes = 0;
        book.Changed += _ => changes++;
        book.Set("7", "kajsa", "Grannen");

        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task PublishesConcurrentChangesInMutationOrder()
    {
        var book = new NicknameBook();
        var heard = new List<string>();
        using var firstHandlerEntered = new ManualResetEventSlim();
        using var releaseFirstHandler = new ManualResetEventSlim();
        book.Changed += entry =>
        {
            if (entry.Text == "Först")
            {
                firstHandlerEntered.Set();
                Assert.True(releaseFirstHandler.Wait(TimeSpan.FromSeconds(5)));
            }
            lock (heard) heard.Add(entry.Text);
        };

        Task first = Task.Run(() => book.Set("7", "kajsa", "Först"));
        Assert.True(firstHandlerEntered.Wait(TimeSpan.FromSeconds(5)));
        Task second = Task.Run(() => book.Set("7", "kajsa", "Sedan"));
        await Task.Delay(50);
        releaseFirstHandler.Set();
        await Task.WhenAll(first, second);

        Assert.Equal(["Först", "Sedan"], heard);
        Assert.Equal("Sedan", book.For("7", "kajsa"));
    }
}

public sealed class NicknameStoreTests
{
    private static string TempFolder() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public void RoundTripsNicknames()
    {
        string folder = TempFolder();
        try
        {
            var store = new NicknameStore(Path.Combine(folder, "nicknames.json"));
            var book = new NicknameBook();
            book.Set("7", "kajsa", "Grannen");
            book.Set("8", "pelle", "Pelle med hunden");
            store.Save(book);

            NicknameBook loaded = new NicknameStore(Path.Combine(folder, "nicknames.json")).Load();

            Assert.Equal(2, loaded.Count);
            Assert.Equal("Grannen", loaded.For("7", null));
            Assert.Equal("Pelle med hunden", loaded.For(null, "pelle"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    // The whole point of the extra copy: a save that goes wrong, or a nickname deleted by mistake,
    // must not be the end of the data.
    [Fact]
    public void KeepsACopyOfEverySave()
    {
        string folder = TempFolder();
        try
        {
            var store = new NicknameStore(Path.Combine(folder, "nicknames.json"));
            var book = new NicknameBook();

            book.Set("7", "kajsa", "Grannen");
            store.Save(book);
            book.Set("8", "pelle", "Pelle med hunden");
            store.Save(book);

            Assert.Equal(2, Directory.GetFiles(store.BackupFolder).Length);
            // The newest copy is the state that was just written, so the folder always holds at
            // least one file that can be read back as it stands.
            Assert.Contains("Pelle med hunden", File.ReadAllText(Directory.GetFiles(store.BackupFolder).OrderBy(f => f).Last()));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void DoesNotPileUpCopiesOfAnUnchangedBook()
    {
        string folder = TempFolder();
        try
        {
            var store = new NicknameStore(Path.Combine(folder, "nicknames.json"));
            var book = new NicknameBook();
            book.Set("7", "kajsa", "Grannen");

            for (int i = 0; i < 5; i++) store.Save(book);

            Assert.Single(Directory.GetFiles(store.BackupFolder));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void ForgetsTheOldestCopiesRatherThanGrowingForever()
    {
        string folder = TempFolder();
        try
        {
            var store = new NicknameStore(Path.Combine(folder, "nicknames.json"), keepBackups: 3);
            var book = new NicknameBook();

            for (int i = 0; i < 6; i++)
            {
                book.Set("7", "kajsa", "Grannen " + i);
                store.Save(book);
            }

            string[] backups = Directory.GetFiles(store.BackupFolder).OrderBy(file => file).ToArray();
            Assert.Equal(3, backups.Length);
            Assert.Contains("Grannen 5", File.ReadAllText(backups[^1]));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    // A truncated main file used to mean an empty book. It now means the newest copy that still
    // reads, put back in place so the next save carries on from there rather than from nothing.
    [Fact]
    public void AnswersFromTheNewestCopyWhenTheFileIsUnreadable()
    {
        string folder = TempFolder();
        string path = Path.Combine(folder, "nicknames.json");
        try
        {
            var store = new NicknameStore(path);
            var book = new NicknameBook();
            book.Set("7", "kajsa", "Grannen");
            store.Save(book);

            File.WriteAllText(path, "{inte-json");

            var reopened = new NicknameStore(path);
            NicknameBook loaded = reopened.Load();

            Assert.Equal("Grannen", loaded.For("7", null));
            Assert.True(reopened.RecoveredFromBackup);
            // Restored, not merely read: the fallback is a rescue rather than a state to stay in.
            Assert.Contains("Grannen", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void StartsEmptyWhenNothingHasEverBeenSaved()
    {
        string folder = TempFolder();
        try
        {
            Assert.Equal(0, new NicknameStore(Path.Combine(folder, "nicknames.json")).Load().Count);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

public sealed class BackedUpJsonFileTests
{
    private sealed record Sample(string Text);

    // The rename is what keeps a half-written file from ever being the one on disk; the leftover
    // temporary file from an interrupted save must not be mistaken for the real one either.
    [Fact]
    public void LeavesNoTemporaryFileBehind()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "data.json");
        try
        {
            var file = new BackedUpJsonFile(path);
            file.Write(new Sample("hej"), new JsonSerializerOptions());

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.True(file.TryRead(new JsonSerializerOptions(), out Sample? read));
            Assert.Equal("hej", read!.Text);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void ReportsThatNothingCouldBeRead()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var file = new BackedUpJsonFile(Path.Combine(folder, "data.json"));

        Assert.False(file.TryRead(new JsonSerializerOptions(), out Sample? read));
        Assert.Null(read);
    }
}
