namespace TwitchOverlayHelper.Overlay;

/// <summary>
/// Which of the three things lit the edge glow. What ranks them is <see cref="EdgeAlertRank.Of"/>
/// rather than the order they are written in – a call outranks a reading waiting for an answer,
/// which outranks a welcome.
/// </summary>
public enum EdgeAlertKind
{
    /// <summary>Someone wrote in the channel for the first time ever.</summary>
    NewChatter,

    /// <summary>A moderator or the broadcaster wrote the call command.</summary>
    ModCall,

    /// <summary>A paid reading is waiting for the streamer to approve or refuse it.</summary>
    TtsRequest
}

/// <summary>
/// How the three kinds are ordered when they ask for the light at the same time. A higher rank takes
/// the glow off a lower one; the same rank holds the one that is already lit; a lower rank is dropped.
///
/// <para>A reading sits between the other two: it has to be answered, so first-time hellos must not
/// hide it, but a moderator's call is more urgent and the dock's approval bar keeps the reading
/// visible either way.</para>
/// </summary>
internal static class EdgeAlertRank
{
    public static int Of(EdgeAlertKind kind) => kind switch
    {
        EdgeAlertKind.ModCall => 2,
        EdgeAlertKind.TtsRequest => 1,
        _ => 0
    };
}

/// <summary>
/// Decides which edge alerts get to light up. There is only ever one glow – one window, one
/// animation – so triggers arriving close together cannot stack.
///
/// <para>A trigger can only be pushed aside by something <see cref="EdgeAlertRank">ranked</see>
/// above it, so a call is never hidden by a welcome. Welcomes go quiet for
/// <see cref="NewChatterCooldown"/> after one has been shown, so a raid does not relight the edges
/// on every line. A glow that is already lit is extended rather than restarted, up to
/// <see cref="MaxLitFactor"/> times its own length, so it always ends however busy chat is.</para>
///
/// <para>Only decisions live here; the light itself is <see cref="EdgeAlertWindow"/>'s business.</para>
/// </summary>
public sealed class EdgeAlertScheduler
{
    /// <summary>
    /// How long welcomes stay quiet after one has finished: a raid becomes a handful of glows,
    /// while a lone new chatter in a calm chat is still greeted.
    /// </summary>
    public static readonly TimeSpan NewChatterCooldown = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The ceiling on extending: a glow may never be stretched beyond this many times the length it
    /// was configured with. Without it, chat that keeps triggering would keep the edges lit forever.
    /// </summary>
    private const double MaxLitFactor = 2;

    private readonly System.Threading.Lock _gate = new();
    private EdgeAlertKind _kind;
    private DateTimeOffset _endsAt;
    private DateTimeOffset _cap;
    private DateTimeOffset _quietUntil;

    /// <summary>
    /// How long the edges should stay lit for this trigger, or null when it should not light at all.
    /// Covers a fresh glow, an extended one and a takeover alike.
    ///
    /// <para>A glow being held open gets only what is left of its ceiling, which can be shorter
    /// than the configured duration.</para>
    /// </summary>
    public TimeSpan? PlayFor(EdgeAlertKind kind, double durationSeconds, DateTimeOffset now)
    {
        TimeSpan duration = TimeSpan.FromSeconds(Math.Clamp(durationSeconds, 2, 20));
        lock (_gate)
        {
            bool lit = now < _endsAt;
            if (lit)
            {
                int asking = EdgeAlertRank.Of(kind);
                int showing = EdgeAlertRank.Of(_kind);
                // Something more urgent is already up. This one is dropped rather than queued.
                if (asking < showing) return null;
                // The same rank holds the light open, up to the ceiling; a higher one takes the
                // glow outright.
                if (asking == showing) return Extend(duration, now);
                return Start(kind, duration, now);
            }

            // Nothing is lit. Only welcomes have a cooldown, so only they can be turned away here.
            return now >= _quietUntil || kind != EdgeAlertKind.NewChatter ? Start(kind, duration, now) : null;
        }
    }

    /// <summary>Forgets what is lit, so a reconnect or a channel change starts from silence.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _endsAt = default;
            _cap = default;
            _quietUntil = default;
        }
    }

    private TimeSpan Start(EdgeAlertKind kind, TimeSpan duration, DateTimeOffset now)
    {
        _kind = kind;
        _endsAt = now + duration;
        _cap = now + duration * MaxLitFactor;
        if (kind == EdgeAlertKind.NewChatter) _quietUntil = _endsAt + NewChatterCooldown;
        return duration;
    }

    /// <summary>
    /// Holds the current glow open instead of restarting it, and answers with the time left up to
    /// the ceiling. Null once the ceiling is reached.
    /// </summary>
    private TimeSpan? Extend(TimeSpan duration, DateTimeOffset now)
    {
        if (_endsAt >= _cap) return null;
        _endsAt = now + duration > _cap ? _cap : now + duration;
        if (_kind == EdgeAlertKind.NewChatter) _quietUntil = _endsAt + NewChatterCooldown;
        return _endsAt - now;
    }
}
