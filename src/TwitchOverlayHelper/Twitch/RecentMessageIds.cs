namespace TwitchOverlayHelper.Twitch;

/// <summary>
/// Remembers the message ids seen recently, so a notification delivered twice is only acted on once.
///
/// During an EventSub reconnect Twitch delivers on both the old and the replacement socket until
/// the handover completes, so every event in that overlap arrives twice. The overlap lasts
/// seconds, so only the last <c>limit</c> ids are kept, oldest dropped first.
/// </summary>
public sealed class RecentMessageIds(int limit = 512)
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Lock _gate = new();

    /// <summary>
    /// True the first time an id is offered, false every time after. A null or empty id is always
    /// new. Thread-safe.
    /// </summary>
    public bool IsNew(string? messageId)
    {
        // A frame without an id cannot be tracked; showing it twice beats never showing it.
        if (string.IsNullOrEmpty(messageId)) return true;

        // The two sockets of a reconnect are read by two tasks at once.
        lock (_gate)
        {
            if (!_seen.Add(messageId)) return false;
            _order.Enqueue(messageId);
            while (_order.Count > limit) _seen.Remove(_order.Dequeue());
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _seen.Clear();
            _order.Clear();
        }
    }
}
