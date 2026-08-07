namespace TwitchOverlayHelper.Twitch;

/// <summary>
/// Remembers the message ids seen recently, so a notification delivered twice is only acted on once.
///
/// This is not a precaution against a rare Twitch quirk: handling a reconnect the way Twitch
/// requires means keeping the old socket open until the replacement has welcomed, and Twitch keeps
/// delivering on both for that moment. Every event in the overlap arrives twice by design.
///
/// Bounded, and oldest-first: an overlap lasts seconds, so remembering the last few hundred ids is
/// plenty and the buffer must never grow without limit on a stream that runs for hours.
/// </summary>
public sealed class RecentMessageIds(int limit = 512)
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Lock _gate = new();

    /// <summary>
    /// True the first time an id is offered, false every time after. Locked because the two sockets
    /// of a reconnect are read by two tasks at once, which is the whole point of the overlap.
    /// </summary>
    public bool IsNew(string? messageId)
    {
        // A frame carrying no id cannot be tracked. Letting it through risks showing something
        // twice; dropping it risks never showing it at all, and that is the worse of the two.
        if (string.IsNullOrEmpty(messageId)) return true;

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
