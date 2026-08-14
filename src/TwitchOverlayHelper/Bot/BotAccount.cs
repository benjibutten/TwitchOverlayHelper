using System.IO;
using System.Net.Http;
using TwitchOverlayHelper.Diagnostics;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Bot;

/// <summary>
/// The second Twitch account: its own login, its own token on disk, its own connection to the same
/// chat.
///
/// <para><b>Why a whole second everything.</b> A Twitch token belongs to one account, and the point
/// of a bot is that it is not the streamer. So it gets its own <see cref="TwitchSession"/> with its
/// own credentials file, and its own IRC connection – Twitch will not let one connection speak as two
/// accounts, and there is no shortcut around that.</para>
///
/// <para><b>Why the read events are left unwired.</b> This connection joins the channel and receives
/// every line in it, exactly like the streamer's does. Listening to both would put every message into
/// the app twice. The bot's own lines are not lost by ignoring them either: Twitch sends them down
/// every connection except the one that wrote them, so the streamer's connection carries them and
/// they reach the dock and the overlay the ordinary way.</para>
/// </summary>
public sealed class BotAccount : IAsyncDisposable
{
    private readonly TwitchSession _session;
    private readonly TwitchChatClient _chat = new();

    /// <summary>
    /// Serialises the joining and the leaving. Both are asked for from UI handlers that do not wait
    /// for them, so without this a disconnect can run straight through the middle of a connect.
    /// </summary>
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    /// <summary>
    /// Bumped by everything that changes what the bot should be doing. Fetching a token takes a
    /// network round trip, and a connect paused in the middle of one has no idea that the user has
    /// since disconnected or switched channel – it would come back and join a channel the app has
    /// left. Checked after every await, so the stale attempt gives up instead.
    /// </summary>
    private int _generation;
    private string _channel = string.Empty;
    private bool _disposed;

    public BotAccount(HttpClient httpClient, string? tokenPath = null)
    {
        // Its own file, beside the streamer's. Sharing one would mean logging the bot in signed the
        // streamer out, which is the one thing this feature must never do.
        _session = new TwitchSession(
            httpClient,
            new TokenStore(tokenPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TwitchOverlayHelper", "credentials.bot.bin")),
            TwitchAuth.BotScopes);
        _session.StateChanged += () => StateChanged?.Invoke();
    }

    /// <summary>Raised when the login changed – signed in, signed out, or a device flow moved on.</summary>
    public event Action? StateChanged;

    /// <summary>Raised with the connection's own words, for the settings window's status line.</summary>
    public event Action<string>? StatusChanged;

    public bool IsLoggedIn => _session.IsLoggedIn;

    public string Login => _session.Login;

    /// <summary>True when the bot could write a line right now.</summary>
    public bool CanSend => _chat.CanSend;

    public SessionState Snapshot() => _session.Snapshot();

    public Task<DeviceCodePrompt> BeginLoginAsync(string clientId, CancellationToken cancellationToken = default) =>
        _session.BeginLoginAsync(clientId, cancellationToken);

    public void CancelPendingLogin() => _session.CancelPendingLogin();

    public async Task LogoutAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        await _session.LogoutAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Joins the channel, or leaves it again when there is nothing to join it with. Safe to call
    /// whenever anything changed – the channel, the mode, the login – because it works out for
    /// itself whether it has anything to do.
    /// </summary>
    public async Task ApplyAsync(string channel, bool wanted)
    {
        if (_disposed) return;
        string normalized = TwitchChatClient.NormalizeChannel(channel ?? string.Empty);
        int generation = Interlocked.Increment(ref _generation);

        await _applyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Overtaken while waiting for the lock: a later call knows better than this one what the
            // bot is supposed to be doing.
            if (_disposed || Volatile.Read(ref _generation) != generation) return;

            bool shouldRun = wanted && _session.IsLoggedIn && normalized.Length > 0;
            bool sameChannel = string.Equals(_channel, normalized, StringComparison.Ordinal);
            if (shouldRun && _chat.IsRunning && sameChannel) return;

            if (_chat.IsRunning) await LeaveAsync().ConfigureAwait(false);
            if (!shouldRun) return;

            // Asked for before connecting: a bot that cannot get a token joins anonymously otherwise,
            // which looks connected and can never write a word.
            string? token = await _session.TryGetIrcTokenAsync().ConfigureAwait(false);
            // The round trip above is the long one, and the window this check closes is the whole
            // reason the generation exists: without it, a disconnect during it is followed by this
            // attempt cheerfully joining the channel the user just left.
            if (Volatile.Read(ref _generation) != generation) return;
            if (token is null)
            {
                StatusChanged?.Invoke("Boten kunde inte hämta en token från Twitch – logga in igen.");
                return;
            }

            try
            {
                _channel = normalized;
                await _chat.ConnectAsync(normalized, _session.Login, _session.TryGetIrcTokenAsync, _session.UserId)
                    .ConfigureAwait(false);
                StatusChanged?.Invoke($"Boten är ansluten som {_session.Login} i #{normalized}.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TwitchAuthException)
            {
                _channel = string.Empty;
                AppLog.Warn($"Bot: kunde inte ansluta som {_session.Login}: {ex.Message}");
                StatusChanged?.Invoke("Boten kunde inte ansluta: " + ex.Message);
            }
        }
        finally { _applyLock.Release(); }
    }

    /// <summary>
    /// Leaves the channel. The generation is bumped before the lock is taken rather than after, so a
    /// connect that is currently paused on a network call is already stale by the time it wakes –
    /// otherwise it would sit behind this waiting for the lock and then join again.
    /// </summary>
    public async Task DisconnectAsync()
    {
        Interlocked.Increment(ref _generation);
        await _applyLock.WaitAsync().ConfigureAwait(false);
        try { await LeaveAsync().ConfigureAwait(false); }
        finally { _applyLock.Release(); }
    }

    private async Task LeaveAsync()
    {
        _channel = string.Empty;
        if (_chat.IsRunning) await _chat.DisconnectAsync().ConfigureAwait(false);
    }

    /// <summary>Writes one line as the bot. Throws when it is not connected, which the sender logs.</summary>
    public Task SendAsync(string text, CancellationToken cancellationToken) =>
        _chat.SendMessageAsync(text, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // Stales whatever apply is in flight, so nothing connects on the way out.
        Interlocked.Increment(ref _generation);
        await _chat.DisposeAsync().ConfigureAwait(false);
        _session.Dispose();
        _applyLock.Dispose();
    }
}
