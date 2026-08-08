using System.Text;

namespace TwitchOverlayHelper.Nicknames;

/// <summary>
/// A name the streamer gave a chatter. Both keys are kept: the numeric id is the one that survives
/// a name change, and the login is the only thing there is to go on for a line that arrived without
/// an id – the sample messages, and anything typed before we knew who sent it.
/// </summary>
public sealed record Nickname(string UserId, string Login, string Text, long UpdatedAt)
{
    /// <summary>Empty text is how a removal travels, so one frame shape covers both.</summary>
    public bool IsRemoval => Text.Length == 0;
}

/// <summary>The file on disk. Versioned so a later shape can be told apart from this one.</summary>
public sealed record NicknameFile(int Version, IReadOnlyList<Nickname> Entries)
{
    public const int CurrentVersion = 1;

    public static NicknameFile Of(IReadOnlyList<Nickname> entries) => new(CurrentVersion, entries);
}

/// <summary>
/// Every nickname that has been given, looked up by whichever key a chat line happens to carry.
/// Lives in the app rather than in the browser: the overlay draws the same names, a dock that
/// reloads must not lose them, and only the app can write them to disk.
/// </summary>
public sealed class NicknameBook
{
    /// <summary>
    /// Long enough for a real nickname, short enough that it cannot push the message out of a
    /// narrow dock column. It sits next to a name, not instead of one.
    /// </summary>
    public const int MaxLength = 24;

    private readonly Lock _lock = new();
    // Mutations include their notification. This keeps two callers from publishing change B before
    // change A has finished saving and publishing, without holding the book's data lock during I/O.
    private readonly Lock _changeLock = new();
    private readonly List<Nickname> _entries = [];
    private Dictionary<string, Nickname> _byId = new(StringComparer.Ordinal);
    private Dictionary<string, Nickname> _byLogin = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One nickname was set, changed or removed. Raised outside the data lock so a handler is free
    /// to take a snapshot, but inside the change lock so concurrent edits are observed in order.
    /// </summary>
    public event Action<Nickname>? Changed;

    public int Count { get { lock (_lock) return _entries.Count; } }

    /// <summary>The nickname for a chatter, or null. The id wins: a login can change hands.</summary>
    public string? For(string? userId, string? login)
    {
        lock (_lock)
        {
            // A known id is authoritative. Falling back to the login here could give a recycled
            // Twitch name the previous owner's nickname.
            if (userId is { Length: > 0 } id)
                return _byId.TryGetValue(id, out Nickname? byId) ? byId.Text : null;
            if (login is { Length: > 0 } name && _byLogin.TryGetValue(name, out Nickname? byLogin)) return byLogin.Text;
            return null;
        }
    }

    /// <summary>Every nickname, sorted the way a list of them is read: by the name shown.</summary>
    public IReadOnlyList<Nickname> Snapshot()
    {
        lock (_lock)
            return _entries.OrderBy(entry => entry.Text, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>
    /// Names a chatter. Returns the stored entry, or null when the text was empty after cleaning –
    /// which is a removal rather than a nickname made of spaces.
    /// </summary>
    public Nickname? Set(string? userId, string? login, string? text)
    {
        string id = userId?.Trim() ?? string.Empty;
        string name = login?.Trim().ToLowerInvariant() ?? string.Empty;
        string cleaned = Clean(text);
        if (id.Length == 0 && name.Length == 0) return null;
        // A nickname of nothing but spaces is not a nickname; the only honest reading of it is that
        // the one that was there should go.
        if (cleaned.Length == 0) { Remove(id, name); return null; }

        lock (_changeLock)
        {
            var entry = new Nickname(id, name, cleaned, Now());
            lock (_lock)
            {
                int existing = IndexOf(id, name);
                if (existing >= 0)
                {
                    // The login is refreshed along with the text: the same person can be behind a new
                    // one, and a stale login would keep matching a name they no longer hold.
                    if (_entries[existing] is { } previous && previous.Text == cleaned && previous.Login == name && previous.UserId == id)
                        return previous;
                    _entries[existing] = entry;
                }
                else _entries.Add(entry);
                Reindex();
            }
            Changed?.Invoke(entry);
            return entry;
        }
    }

    /// <summary>Takes a nickname away. False when there was none to take.</summary>
    public bool Remove(string? userId, string? login)
    {
        string id = userId?.Trim() ?? string.Empty;
        string name = login?.Trim().ToLowerInvariant() ?? string.Empty;
        lock (_changeLock)
        {
            Nickname removed;
            lock (_lock)
            {
                int existing = IndexOf(id, name);
                if (existing < 0) return false;
                removed = _entries[existing];
                _entries.RemoveAt(existing);
                Reindex();
            }
            // Carries the keys it was found under as well as the ones asked for, so a dock can drop it
            // from both of its lookups even when the caller only knew one of them.
            Changed?.Invoke(new Nickname(removed.UserId, removed.Login, string.Empty, Now()));
            return true;
        }
    }

    /// <summary>Loads a whole book from disk. Silent by design: nothing has changed for a reader yet.</summary>
    public void Replace(IEnumerable<Nickname> entries)
    {
        lock (_lock)
        {
            _entries.Clear();
            foreach (Nickname entry in entries)
            {
                string id = entry.UserId?.Trim() ?? string.Empty;
                string login = entry.Login?.Trim().ToLowerInvariant() ?? string.Empty;
                string text = Clean(entry.Text);
                if (text.Length == 0 || (id.Length == 0 && login.Length == 0)) continue;
                int existing = IndexOf(id, login);
                var cleaned = new Nickname(id, login, text, entry.UpdatedAt);
                if (existing >= 0) _entries[existing] = cleaned;
                else _entries.Add(cleaned);
            }
            Reindex();
        }
    }

    /// <summary>
    /// What a nickname is allowed to be. Line breaks and control characters would break the row it
    /// is drawn on, and a run of spaces is not a name; the length cap keeps it beside the chatter's
    /// own name rather than in place of the message.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char character in text)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
            builder.Append(character);
        }

        string cleaned = builder.ToString();
        if (cleaned.Length <= MaxLength) return cleaned;
        cleaned = cleaned[..MaxLength];
        // Cutting between the halves of an emoji would leave a lone surrogate behind.
        if (char.IsHighSurrogate(cleaned[^1])) cleaned = cleaned[..^1];
        return cleaned.TrimEnd();
    }

    /// <summary>
    /// Which entry is this chatter's. The id decides when both sides have one – a login that has
    /// been passed on to someone else must not inherit the nickname with it.
    /// </summary>
    private int IndexOf(string userId, string login)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            Nickname entry = _entries[i];
            if (userId.Length > 0 && entry.UserId.Length > 0)
            {
                if (string.Equals(entry.UserId, userId, StringComparison.Ordinal)) return i;
                continue;
            }
            if (login.Length > 0 && string.Equals(entry.Login, login, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>
    /// Rebuilt whole rather than patched. The list is tens of entries at most, and a lookup that
    /// drifts out of step with it would show a nickname for a chatter who no longer has one.
    /// </summary>
    private void Reindex()
    {
        _byId = new Dictionary<string, Nickname>(StringComparer.Ordinal);
        _byLogin = new Dictionary<string, Nickname>(StringComparer.OrdinalIgnoreCase);
        foreach (Nickname entry in _entries)
        {
            if (entry.UserId.Length > 0) _byId[entry.UserId] = entry;
            if (entry.Login.Length > 0) _byLogin[entry.Login] = entry;
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
