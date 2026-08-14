using TwitchOverlayHelper.Diagnostics;

namespace TwitchOverlayHelper.Bot;

/// <summary>
/// Twitch's write allowance, counted the way Twitch counts it: a rolling thirty seconds. Kept apart
/// from the sending so the arithmetic can be tested without waiting for real seconds to pass.
/// </summary>
internal sealed class BotRateLimiter
{
    /// <summary>Twitch's own window. Not a setting – it is their number, not ours.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private readonly Queue<DateTimeOffset> _sent = new();

    /// <summary>How long before another message may go out, or zero when one may go now.</summary>
    public TimeSpan TimeUntilSlot(int allowedPerWindow, DateTimeOffset now)
    {
        while (_sent.Count > 0 && now - _sent.Peek() >= Window) _sent.Dequeue();
        if (_sent.Count < Math.Max(1, allowedPerWindow)) return TimeSpan.Zero;
        TimeSpan wait = Window - (now - _sent.Peek());
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    public void Record(DateTimeOffset now) => _sent.Enqueue(now);
}

/// <summary>
/// Remembers what was said recently, because Twitch drops a message identical to one the same
/// account sent within thirty seconds – silently, with no notice and no error. A bot that repeated
/// itself would look like it had stopped working rather than like it was being ignored.
/// </summary>
internal sealed class BotDuplicateGuard
{
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);

    public bool IsDuplicate(string text, DateTimeOffset now)
    {
        Prune(now);
        return _recent.TryGetValue(text, out DateTimeOffset at) && now - at < BotRateLimiter.Window;
    }

    public void Record(string text, DateTimeOffset now)
    {
        Prune(now);
        _recent[text] = now;
    }

    /// <summary>Forgets everything – the room changed, and Twitch counts duplicates per room.</summary>
    public void Forget() => _recent.Clear();

    private void Prune(DateTimeOffset now)
    {
        if (_recent.Count < 32) return;
        foreach ((string text, DateTimeOffset at) in _recent.ToArray())
            if (now - at >= BotRateLimiter.Window) _recent.Remove(text);
    }
}

/// <summary>
/// The one way a bot line reaches chat: queued, spaced out to stay inside Twitch's allowance, and
/// dropped when it would be a duplicate Twitch is going to swallow anyway.
///
/// <para><b>Why it queues rather than sends.</b> The moments the bot has something to say arrive in
/// clumps – a lawn that empties settles half a dozen redemptions in the same second – and a straight
/// send per event would spend the whole allowance in a burst. What is on the other side of that
/// ceiling is not a rejected message but a global write ban on the account, so the queue is the
/// feature rather than an optimisation.</para>
/// </summary>
public sealed class BotSender : IAsyncDisposable
{
    /// <summary>
    /// How many lines may wait at once. A queue longer than this is one that has stopped being
    /// answers and started being a backlog; the newest are dropped, because the ones already waiting
    /// belong to viewers who have been waiting longer.
    /// </summary>
    private const int MaxQueued = 20;

    private readonly Func<string, CancellationToken, Task> _send;
    private readonly Func<int> _allowedPerWindow;
    private readonly BotRateLimiter _rate = new();
    private readonly BotDuplicateGuard _duplicates = new();
    private readonly Lock _gate = new();
    private readonly Queue<string> _queue = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _pump;

    /// <summary>
    /// Which pump <see cref="_pump"/> refers to. A pump that finds the queue empty clears the field
    /// under the lock, so the next <see cref="Enqueue"/> starts a fresh one – and that new pump would
    /// otherwise be cleared again a moment later by the old one's finally, leaving two of them
    /// running over the same queue. Counted so each pump only ever lets go of its own.
    /// </summary>
    private int _pumpGeneration;
    private bool _disposed;

    public BotSender(Func<string, CancellationToken, Task> send, Func<int> allowedPerWindow)
    {
        _send = send;
        _allowedPerWindow = allowedPerWindow;
    }

    /// <summary>Lines actually written, for the app's own status line and for the tests.</summary>
    public int SentCount { get; private set; }

    /// <summary>Raised after a line has gone out, so the app can show what the bot last said.</summary>
    public event Action<string>? Sent;

    /// <summary>Raised when a line could not be written, with Twitch's reason.</summary>
    public event Action<string>? Failed;

    /// <summary>
    /// Puts a line in the queue. Never waits for it: every caller is an event handler on the chat
    /// thread, the EventSub thread or a timer, and none of them has half a minute to spare.
    /// </summary>
    public void Enqueue(string text)
    {
        string line = text.Trim();
        if (line.Length == 0 || _disposed) return;

        lock (_gate)
        {
            if (_queue.Count >= MaxQueued)
            {
                AppLog.Warn($"Bot: kön är full ({MaxQueued}), meddelandet skrevs aldrig: {line}");
                return;
            }
            // Already waiting to be said. Worth catching here as well as at the send: the queue can
            // hold a line for half a minute, which is exactly the span in which the same event
            // happening twice would produce it again.
            if (_queue.Contains(line, StringComparer.Ordinal)) return;
            _queue.Enqueue(line);
            if (_pump is null)
            {
                int generation = ++_pumpGeneration;
                _pump = Task.Run(() => PumpAsync(generation));
            }
        }
    }

    /// <summary>
    /// Throws away everything still waiting – the channel changed, or the bot was switched off.
    ///
    /// <para>What was recently said goes with it. Twitch counts duplicates per channel, so a line
    /// already said in the channel we have left is a new line in the one we are joining, and
    /// remembering it would silence the first thing the bot has to say there. The rate allowance is
    /// deliberately kept: it belongs to the account rather than to the room, and carrying it over is
    /// the safe direction to be wrong in.</para>
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _queue.Clear();
            _duplicates.Forget();
        }
    }

    private async Task PumpAsync(int generation)
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                string line;
                TimeSpan wait;
                lock (_gate)
                {
                    // Finding the queue empty and letting go of it are one step under the lock, so an
                    // Enqueue arriving now either lands before this and is taken, or after it and
                    // starts a pump of its own. A gap here would leave a line with nobody to send it.
                    if (_queue.Count == 0) { _pump = null; return; }
                    line = _queue.Peek();
                    wait = _rate.TimeUntilSlot(_allowedPerWindow(), DateTimeOffset.UtcNow);
                }

                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _lifetime.Token).ConfigureAwait(false);
                    continue;
                }

                lock (_gate)
                {
                    // Taken now rather than at the peek: the wait above can be half a minute long,
                    // and Clear() during it means this line is no longer wanted.
                    if (_queue.Count == 0 || !string.Equals(_queue.Peek(), line, StringComparison.Ordinal)) continue;
                    _queue.Dequeue();
                    if (_duplicates.IsDuplicate(line, DateTimeOffset.UtcNow))
                    {
                        AppLog.Info($"Bot: hoppade över en upprepning Twitch ändå hade svalt: {line}");
                        continue;
                    }
                    // The allowance is spent on the attempt rather than on the success: a run of
                    // sends that fail fast – the bot is not connected – would otherwise cost nothing
                    // to make, and the moment the connection came back the whole backlog would go
                    // out at once into the ceiling this exists to stay under.
                    _rate.Record(DateTimeOffset.UtcNow);
                }

                try
                {
                    await _send(line, _lifetime.Token).ConfigureAwait(false);
                    SentCount++;
                    // Written down only now. A line Twitch never received is not one Twitch will
                    // swallow as a repeat, and remembering a failed send would block the same
                    // perfectly good sentence for the next thirty seconds.
                    lock (_gate) _duplicates.Record(line, DateTimeOffset.UtcNow);
                    AppLog.Info($"Bot: {line}");
                    Sent?.Invoke(line);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    // Not connected, not logged in, refused by Twitch. The line is gone either way –
                    // retrying a greeting or a refund notice minutes later is worse than dropping it.
                    AppLog.Warn($"Bot: kunde inte skriva i chatten: {ex.Message}");
                    Failed?.Invoke(ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Error("Bot: sändningskön stannade", ex);
        }
        finally
        {
            // Whatever happened, the pump must not stay marked as running – nothing would ever start
            // it again and the bot would go quiet until the app restarted. Only its own, though: a
            // successor started after this one gave the queue up is the live pump now.
            lock (_gate)
            {
                if (_pumpGeneration == generation) _pump = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        Task? pump;
        lock (_gate) pump = _pump;
        if (pump is not null)
        {
            try { await pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
    }
}
