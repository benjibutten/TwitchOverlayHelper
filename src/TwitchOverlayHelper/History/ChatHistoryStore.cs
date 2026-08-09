using System.IO;
using System.Text.Json;
using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.History;

/// <summary>
/// What was on screen last time, written to disk so restarting the app mid-stream does not leave the
/// overlay staring at an empty column. Twitch itself offers nothing here – IRC sends no history on
/// join and there is no Helix endpoint for chat – so the only lines we can ever put back are the
/// ones we saw ourselves.
/// </summary>
/// <param name="Channel">
/// Which room these lines are from. Chat from another channel is worse than no chat at all, so a
/// snapshot is only ever restored into the channel it was taken in.
/// </param>
public sealed record ChatHistorySnapshot(string Channel, DateTimeOffset SavedAt, IReadOnlyList<ChatTimelineItem> Items);

public sealed class ChatHistoryStore
{
    /// <summary>
    /// How old a line may be and still be worth putting back. Measured per line rather than by the
    /// calendar on purpose: "today's chat" would empty itself at midnight, which is the middle of
    /// the evening for a stream that started at nine – and a restart at 00:05 is exactly when
    /// getting the chat back matters most.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    /// <summary>
    /// Ceiling on what is written and read back. Matches the hub's own history limit: putting back
    /// more than the dock would ever have kept only makes the first paint slower.
    /// </summary>
    public const int MaxItems = 200;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;

    public ChatHistoryStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TwitchOverlayHelper", "chat-history.json");
    }

    /// <summary>
    /// The lines worth showing again: this channel's, young enough to still be the same sitting, and
    /// no more than <see cref="MaxItems"/> of them. Anything unreadable, from elsewhere or from
    /// yesterday comes back as an empty list – a chat that starts empty is the old behaviour and
    /// costs nothing, while chat from the wrong room or the wrong day is actively misleading.
    /// </summary>
    public IReadOnlyList<ChatTimelineItem> Load(string channel, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(channel)) return [];
        try
        {
            if (!File.Exists(_path)) return [];
            ChatHistorySnapshot? snapshot = JsonSerializer.Deserialize<ChatHistorySnapshot>(File.ReadAllText(_path), JsonOptions);
            if (snapshot is null || !string.Equals(snapshot.Channel, channel, StringComparison.OrdinalIgnoreCase)) return [];

            DateTimeOffset cutoff = now - MaxAge;
            List<ChatTimelineItem> fresh = snapshot.Items.Where(item => TimeOf(item) is { } at && at >= cutoff).ToList();
            return fresh.Count > MaxItems ? fresh[^MaxItems..] : fresh;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>
    /// Writes the snapshot, newest <see cref="MaxItems"/> lines only. Written through a temp file so
    /// a crash mid-write cannot leave a half-written file behind – which would then be the thing
    /// that fails to load on the very restart it exists to help.
    /// </summary>
    /// <returns>
    /// Whether the file was written. Answered rather than thrown, and answered rather than swallowed:
    /// the caller only skips the next write because this one is already on disk, so a locked file or
    /// a full disk has to be something it can hear about and try again after.
    /// </returns>
    public bool Save(string channel, IReadOnlyList<ChatTimelineItem> items, DateTimeOffset now)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            IReadOnlyList<ChatTimelineItem> kept = items.Count > MaxItems ? items.Skip(items.Count - MaxItems).ToList() : items;
            string json = JsonSerializer.Serialize(new ChatHistorySnapshot(channel, now, kept), JsonOptions);
            string tempPath = _path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _path, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Losing the history costs one empty column after a restart. Never worth an exception on
            // a timer tick, and never worth taking the app down for.
            return false;
        }
    }

    /// <summary>Throws the saved lines away – used when the channel changes, so they cannot come back.</summary>
    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>When a timeline item happened, whichever of the two kinds it is.</summary>
    internal static DateTimeOffset? TimeOf(ChatTimelineItem item) =>
        item.Message?.SentAt ?? item.Event?.At;
}
