using TwitchOverlayHelper.Diagnostics;

namespace TwitchOverlayHelper.Bot;

/// <summary>
/// Twitch's write allowance, counted the way Twitch counts it: a rolling thirty seconds.
/// </summary>
internal sealed class BotRateLimiter
{
    /// <summary>Twitch's own window, so not a setting.</summary>
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
/// Remembers what was said recently: Twitch silently drops a message identical to one the same
/// account sent within thirty seconds.
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

    /// <summary>Forgets everything. Twitch counts duplicates per room, so call it when the room changes.</summary>
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
/// <para>Bot lines arrive in bursts, and exceeding Twitch's allowance costs the account a global
/// write ban rather than one rejected message.</para>
/// </summary>
public sealed class BotSender : IAsyncDisposable
{
    /// <summary>How many lines may wait at once; when the queue is full the newest line is dropped.</summary>
    private const int MaxQueued = 20;

    /// <summary>The longest message Twitch accepts; a longer one is refused whole.</summary>
    private const int MaxLength = 500;

    private readonly Func<string, CancellationToken, Task> _send;
    private readonly Func<int> _allowedPerWindow;
    private readonly BotRateLimiter _rate = new();
    private readonly BotDuplicateGuard _duplicates = new();
    private readonly Lock _gate = new();
    private readonly Queue<string> _queue = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _pump;

    // Identifies the pump _pump refers to, so a finishing pump's finally cannot clear the field after
    // Enqueue has started a successor, which would leave two pumps on the same queue.
    private int _pumpGeneration;
    private bool _disposed;

    public BotSender(Func<string, CancellationToken, Task> send, Func<int> allowedPerWindow)
    {
        _send = send;
        _allowedPerWindow = allowedPerWindow;
    }

    /// <summary>Lines actually written.</summary>
    public int SentCount { get; private set; }

    /// <summary>Raised with the line after it has gone out.</summary>
    public event Action<string>? Sent;

    /// <summary>Raised when a line could not be written, with Twitch's reason.</summary>
    public event Action<string>? Failed;

    /// <summary>
    /// Puts a line in the queue and returns at once. A line longer than Twitch accepts is truncated;
    /// one that is already waiting, or that arrives when the queue is full, is dropped.
    /// </summary>
    public void Enqueue(string text)
    {
        string line = text.Trim();
        if (line.Length == 0 || _disposed) return;
        if (line.Length > MaxLength) line = line[..(MaxLength - 1)] + "…";

        lock (_gate)
        {
            if (_queue.Count >= MaxQueued)
            {
                AppLog.Warn($"Bot: kön är full ({MaxQueued}), meddelandet skrevs aldrig: {line}");
                return;
            }
            // Already waiting to be said. The send-time duplicate check only knows lines that have gone
            // out, and a line can wait here for half a minute.
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
    /// Throws away everything still waiting, and the memory of what was recently said (Twitch counts
    /// duplicates per channel). The rate allowance is kept: it belongs to the account, not the room.
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
                    // Finding the queue empty and clearing _pump are one step under the lock, so a
                    // concurrent Enqueue is either taken here or starts a pump of its own.
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
                    // Dequeued here rather than at the peek: Clear() may have run during the wait.
                    if (_queue.Count == 0 || !string.Equals(_queue.Peek(), line, StringComparison.Ordinal)) continue;
                    _queue.Dequeue();
                    if (_duplicates.IsDuplicate(line, DateTimeOffset.UtcNow))
                    {
                        AppLog.Info($"Bot: hoppade över en upprepning Twitch ändå hade svalt: {line}");
                        continue;
                    }
                    // The allowance is spent on the attempt, not the success: sends that fail fast
                    // while disconnected must not let the whole backlog out at once on reconnect.
                    _rate.Record(DateTimeOffset.UtcNow);
                }

                try
                {
                    await _send(line, _lifetime.Token).ConfigureAwait(false);
                    SentCount++;
                    // Recorded only after a successful send: a line Twitch never received is not one
                    // it will swallow as a repeat.
                    lock (_gate) _duplicates.Record(line, DateTimeOffset.UtcNow);
                    AppLog.Info($"Bot: {line}");
                    Sent?.Invoke(line);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    // Not connected, not logged in or refused by Twitch. The line is dropped: a
                    // greeting or refund notice retried minutes later is worse than none.
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
            // A pump left marked as running would never be restarted. Only its own mark is cleared: a
            // successor may already be the live pump.
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
