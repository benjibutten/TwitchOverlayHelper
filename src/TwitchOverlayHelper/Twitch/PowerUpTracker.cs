using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Twitch;

/// <summary>
/// Pairs a Gigantify an Emote power-up with the chat line it enlarged.
///
/// The message arrives over IRC and the power-up over EventSub, in either order, and Twitch sends
/// nothing that ties them together: no message id on channel.bits.use, no power-up tag on the
/// PRIVMSG. They are matched on sender, emote and text; whichever arrives first waits in a
/// bounded buffer and expires unmatched.
/// </summary>
public sealed class PowerUpTracker
{
    // How long the two halves may be apart. Far longer than Twitch takes: too short loses the
    // marker, while too long only risks a later message that repeats the same text and emote.
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    private const int Limit = 64;

    private readonly List<GigantifiedEmote> _pending = [];
    private readonly List<Seen> _recent = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// Marks a chat line if a power-up is already waiting for it, and remembers it in case one is
    /// still on its way.
    /// </summary>
    public ChatMessage Enrich(ChatMessage message)
    {
        // Only an emote can be gigantified.
        if (message.Emotes.Count == 0) return message;

        DateTimeOffset now = DateTimeOffset.Now;
        lock (_gate)
        {
            Expire(now);

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (!Matches(_pending[i], message)) continue;
                string emoteId = _pending[i].EmoteId;
                _pending.RemoveAt(i);
                return message with { GigantifiedEmoteId = emoteId };
            }

            Remember(new Seen(message, now));
        }
        return message;
    }

    /// <summary>
    /// Takes a power-up that has just arrived. Returns the message it belongs to, marked, when that
    /// message has already gone out to the views, so the caller can publish it again. Returns null
    /// when the message has not been seen yet; the power-up then waits for <see cref="Enrich"/>.
    /// </summary>
    public ChatMessage? Match(GigantifiedEmote powerUp)
    {
        if (powerUp.UserId.Length == 0) return null;

        DateTimeOffset now = DateTimeOffset.Now;
        lock (_gate)
        {
            Expire(now);

            for (int i = _recent.Count - 1; i >= 0; i--)
            {
                ChatMessage candidate = _recent[i].Message;
                if (!Matches(powerUp, candidate)) continue;
                // Consumed: a second power-up from the same viewer belongs to a second message.
                _recent.RemoveAt(i);
                return candidate with { GigantifiedEmoteId = powerUp.EmoteId };
            }

            Remember(powerUp with { At = now });
        }
        return null;
    }

    /// <summary>Leaving a channel drops both halves: neither belongs to the chat we are joining.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            _recent.Clear();
        }
    }

    // Same viewer, same emote, same words. Emote and text are checked independently because
    // Twitch marks message optional on channel.bits.use; a field missing on either side is
    // skipped and the other check decides.
    private static bool Matches(GigantifiedEmote powerUp, ChatMessage message)
    {
        if (message.UserId.Length == 0 || !string.Equals(powerUp.UserId, message.UserId, StringComparison.Ordinal))
            return false;

        if (powerUp.EmoteId.Length > 0 && !Contains(message.Emotes, powerUp.EmoteId)) return false;

        return powerUp.Text.Length == 0 || message.Text.Length == 0
            || string.Equals(powerUp.Text.Trim(), message.Text.Trim(), StringComparison.Ordinal);
    }

    private static bool Contains(IReadOnlyList<EmoteSpan> emotes, string emoteId)
    {
        for (int i = 0; i < emotes.Count; i++)
            if (string.Equals(emotes[i].EmoteId, emoteId, StringComparison.Ordinal)) return true;
        return false;
    }

    private void Expire(DateTimeOffset now)
    {
        _pending.RemoveAll(item => now - item.At > Window);
        _recent.RemoveAll(item => now - item.At > Window);
    }

    private void Remember(GigantifiedEmote powerUp)
    {
        _pending.Add(powerUp);
        if (_pending.Count > Limit) _pending.RemoveAt(0);
    }

    private void Remember(Seen message)
    {
        _recent.Add(message);
        if (_recent.Count > Limit) _recent.RemoveAt(0);
    }

    // A chat line that could still turn out to have been gigantified. The whole message is kept,
    // not just its id, because a late power-up republishes the line in full.
    private readonly record struct Seen(ChatMessage Message, DateTimeOffset At);
}
