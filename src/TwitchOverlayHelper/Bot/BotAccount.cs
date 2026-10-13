using System.IO;
using System.Net.Http;
using TwitchOverlayHelper.Diagnostics;
using TwitchOverlayHelper.Storage;
using TwitchOverlayHelper.Twitch;

namespace TwitchOverlayHelper.Bot;

/// <summary>
/// The second Twitch account: its own login, its own token on disk and its own IRC connection to the
/// same chat, since one connection cannot speak as two accounts.
///
/// <para>The connection's read events are left unwired: the streamer's connection already receives
/// every line, the bot's own included.</para>
/// </summary>
public sealed class BotAccount : IAsyncDisposable
{
    private readonly TwitchSession _session;
    private readonly TwitchChatClient _chat = new();

    // Serialises joining and leaving: both come from UI handlers that do not await them.
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    // Bumped by everything that changes what the bot should be doing, and checked after every await,
    // so a connect paused on a network call gives up when it has been overtaken.
    private int _generation;
    private string _channel = string.Empty;
    private bool _disposed;

    public BotAccount(HttpClient httpClient, string? tokenPath = null)
    {
        // Its own file: sharing the streamer's would sign the streamer out when the bot logs in.
        _session = new TwitchSession(
            httpClient,
            new TokenStore(tokenPath ?? ProfilePaths.File("credentials.bot.bin")),
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
    /// Joins the channel, or leaves it when there is nothing to join it with. Safe to call whenever
    /// the channel, the mode or the login changed; does nothing when already in the right state.
    /// </summary>
    public async Task ApplyAsync(string channel, bool wanted)
    {
        if (_disposed) return;
        string normalized = TwitchChatClient.NormalizeChannel(channel ?? string.Empty);
        int generation = Interlocked.Increment(ref _generation);

        await _applyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Overtaken by a later call while waiting for the lock.
            if (_disposed || Volatile.Read(ref _generation) != generation) return;

            bool shouldRun = wanted && _session.IsLoggedIn && normalized.Length > 0;
            bool sameChannel = string.Equals(_channel, normalized, StringComparison.Ordinal);
            if (shouldRun && _chat.IsRunning && sameChannel) return;

            if (_chat.IsRunning) await LeaveAsync().ConfigureAwait(false);
            if (!shouldRun) return;

            // Fetched before connecting: without a token the bot would join anonymously, which looks
            // connected and can never write.
            string? token = await _session.TryGetIrcTokenAsync().ConfigureAwait(false);
            // A disconnect during the token round trip must not be followed by a join.
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

    /// <summary>Leaves the channel and cancels any connect that is in flight.</summary>
    public async Task DisconnectAsync()
    {
        // Bumped before taking the lock, so a connect paused on a network call is stale when it wakes
        // rather than joining again once this has released the lock.
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

    /// <summary>Writes one line as the bot. Throws when it is not connected.</summary>
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
