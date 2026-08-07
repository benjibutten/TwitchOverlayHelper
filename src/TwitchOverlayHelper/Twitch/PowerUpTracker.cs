using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Twitch;

/// <summary>
/// Pairs a Gigantify an Emote power-up with the chat line it enlarged.
///
/// The two arrive on different connections – the message over IRC, the power-up over EventSub – and
/// Twitch sends nothing that ties them together: no message id on channel.bits.use, no power-up tag
/// on the PRIVMSG. So they are matched on who wrote them and on the text, and either one may turn up
/// first. Whichever arrives first waits for the other, which is why this holds two short buffers
/// rather than one.
///
/// Both are bounded and short-lived. A power-up that never finds its message simply expires, and a
/// message that is never gigantified is forgotten a few seconds later – neither is worth a card, an
/// error, or a growing buffer on a stream that runs for hours.
/// </summary>
public sealed class PowerUpTracker
{
    /// <summary>
    /// How long the two halves may be apart. Generous compared to the fraction of a second Twitch
    /// actually takes, because the cost of being wrong is asymmetric: too short loses the marker,
    /// too long only risks matching a viewer's *next* message – and that one has to repeat the same
    /// text with the same emote to get through the match at all.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    private const int Limit = 64;

    private readonly List<GigantifiedEmote> _pending = [];
    private readonly List<Seen> _recent = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// Marks a chat line if a power-up is already waiting for it, and remembers it in case one is
    /// still on its way. Every message goes through here, on the IRC read path.
    /// </summary>
    public ChatMessage Enrich(ChatMessage message)
    {
        // Only an emote can be gigantified, so a line without one can neither claim a power-up nor
        // ever be claimed by a later one.
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
    /// message has already gone out to the views – the caller then has to send the marker after the
    /// fact – and null when the message has not been seen yet, in which case the power-up waits here
    /// for <see cref="Enrich"/> to pick it up.
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
                // Consumed either way: a second power-up from the same viewer belongs to a second
                // message, not to this one all over again.
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

    /// <summary>
    /// Same viewer, same emote, same words. Two independent checks rather than one, because Twitch
    /// marks <c>message</c> optional on channel.bits.use: when the words are missing the emote is
    /// what still has to line up, so the viewer's *next* message only matches if it happens to use
    /// the same emote as well. An absent field on either side is the absence of evidence for a
    /// match, not evidence against one – the other check carries it.
    /// </summary>
    private static bool Matches(GigantifiedEmote powerUp, ChatMessage message)
    {
        if (message.UserId.Length == 0 || !string.Equals(powerUp.UserId, message.UserId, StringComparison.Ordinal))
            return false;

        // The emote Twitch enlarged has to be one of the emotes the line actually contains.
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

    /// <summary>
    /// A chat line that could still turn out to have been gigantified. The whole message is kept,
    /// not just its id: a late power-up has to be published as the same line over again, and
    /// rebuilding it from an id would mean the views each keeping their own copy to look it up in.
    /// </summary>
    private readonly record struct Seen(ChatMessage Message, DateTimeOffset At);
}
