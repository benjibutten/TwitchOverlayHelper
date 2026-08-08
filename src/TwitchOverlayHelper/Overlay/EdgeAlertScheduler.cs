namespace TwitchOverlayHelper.Overlay;

/// <summary>Which of the two things lit the edge glow. The order matters: a call outranks a welcome.</summary>
public enum EdgeAlertKind
{
    /// <summary>Someone wrote in the channel for the first time ever.</summary>
    NewChatter,

    /// <summary>A moderator or the broadcaster wrote the call command.</summary>
    ModCall
}

/// <summary>
/// Decides which edge alerts actually get to light up. There is only ever one glow – one window,
/// one animation – so triggers arriving close together do not stack; without a policy the last one
/// simply overwrites the rest. That is the wrong answer twice over: a raid full of first-time
/// chatters would restart the light on every line and keep the edges lit for as long as the raid
/// lasts, and a welcome landing a second after a moderator's call would quietly replace the more
/// urgent of the two.
///
/// So: a call always gets through and can never be pushed aside by a welcome, welcomes go quiet for
/// <see cref="NewChatterCooldown"/> after one has been shown, and a glow that is already lit is
/// extended rather than restarted – up to <see cref="MaxLitFactor"/> times its own length, so it
/// always ends eventually no matter how busy chat is.
///
/// Only decisions live here; the light itself is <see cref="EdgeAlertWindow"/>'s business.
/// </summary>
public sealed class EdgeAlertScheduler
{
    /// <summary>
    /// How long welcomes stay quiet after one has finished. Long enough that a raid becomes a
    /// handful of glows rather than one per arrival, short enough that a lone new chatter arriving
    /// in a calm chat is still greeted.
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
    /// Covers both a fresh glow and one being extended or taken over – the window fades from wherever
    /// it currently is, so the two look the same from here.
    ///
    /// <para>A length rather than a yes, because the two are not the same answer. A glow being held
    /// open has only what is left of its ceiling to run; handing back the configured duration and
    /// letting the window play that in full is exactly how the light would outlast the limit this
    /// class exists to keep – by up to a whole alert, every time chat keeps triggering.</para>
    /// </summary>
    public TimeSpan? PlayFor(EdgeAlertKind kind, double durationSeconds, DateTimeOffset now)
    {
        TimeSpan duration = TimeSpan.FromSeconds(Math.Clamp(durationSeconds, 2, 20));
        lock (_gate)
        {
            bool lit = now < _endsAt;

            if (kind == EdgeAlertKind.ModCall)
            {
                // A second call while the first is still lit says the same thing louder; it holds the
                // light rather than starting a new one. Anything else – nothing lit, or a welcome
                // lit – the call takes over outright.
                if (lit && _kind == EdgeAlertKind.ModCall) return Extend(duration, now);
                return Start(kind, duration, now);
            }

            // The streamer is being called; a first-time chatter is not the thing to interrupt it with.
            if (lit && _kind == EdgeAlertKind.ModCall) return null;
            if (lit) return Extend(duration, now);
            return now >= _quietUntil ? Start(kind, duration, now) : null;
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
    /// Holds the current glow open a little longer instead of restarting it, and answers with what
    /// is left rather than with the full length – the ceiling is only a ceiling if the window is
    /// told to stop there. Once it is reached the answer is nothing: the light has already been on
    /// for twice its length and further triggers have nothing left to say.
    /// </summary>
    private TimeSpan? Extend(TimeSpan duration, DateTimeOffset now)
    {
        if (_endsAt >= _cap) return null;
        _endsAt = now + duration > _cap ? _cap : now + duration;
        if (_kind == EdgeAlertKind.NewChatter) _quietUntil = _endsAt + NewChatterCooldown;
        return _endsAt - now;
    }
}
