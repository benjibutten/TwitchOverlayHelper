using System.Net.Http;

namespace TwitchOverlayHelper.Twitch;

public sealed record SessionState(bool IsLoggedIn, string Login, string UserId, string? PendingUserCode, string? PendingVerificationUri, string? Error);

/// <summary>
/// Owns the logged-in Twitch identity: runs the device flow, keeps the access token fresh and
/// hands it to whoever needs to call Helix. One place to ask "are we allowed to moderate?".
/// </summary>
public sealed class TwitchSession : IDisposable
{
    private readonly TwitchAuth _auth;
    private readonly TokenStore _tokenStore;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private StoredCredentials? _credentials;
    private string? _accessToken;
    private DateTimeOffset _validatedAt = DateTimeOffset.MinValue;
    private CancellationTokenSource? _deviceFlow;
    private DeviceCodePrompt? _prompt;
    private string? _error;

    /// <summary>
    /// Bumped on logout. A login or refresh that was already in flight cannot be cancelled once it
    /// is past its last await, so it checks this before writing – otherwise it would hand the
    /// credentials back after the user signed out.
    /// </summary>
    private int _generation;

    public TwitchSession(HttpClient httpClient, TokenStore? tokenStore = null)
    {
        _auth = new TwitchAuth(httpClient);
        _tokenStore = tokenStore ?? new TokenStore();
        _credentials = _tokenStore.Load();
    }

    public event Action? StateChanged;

    public bool IsLoggedIn => _credentials is not null;
    public string Login => _credentials?.Login ?? string.Empty;
    public string UserId => _credentials?.UserId ?? string.Empty;
    public string ClientId => _credentials?.ClientId ?? string.Empty;

    /// <summary>What Twitch actually granted this login, which is not the same as what we asked for.</summary>
    public IReadOnlyList<string> Scopes => _credentials?.Scopes ?? [];

    /// <summary>
    /// Asked before subscribing to anything, so a missing permission turns a feature off quietly
    /// rather than sending a request Twitch will answer with 403.
    /// </summary>
    public bool HasScope(string scope) =>
        _credentials is { } credentials && credentials.Scopes.Contains(scope, StringComparer.OrdinalIgnoreCase);

    /// <summary>The scopes a stored login predates; empty when there is nothing to re-authorise.</summary>
    public IReadOnlyList<string> MissingScopes() =>
        _credentials is null ? [] : TwitchAuth.MissingScopes(_credentials.Scopes);

    public SessionState Snapshot() => new(
        IsLoggedIn,
        Login,
        UserId,
        _prompt?.UserCode,
        _prompt?.VerificationUri,
        _error);

    /// <summary>Starts the device flow and completes once the user has approved it on twitch.tv.</summary>
    public async Task<DeviceCodePrompt> BeginLoginAsync(string clientId, CancellationToken cancellationToken = default)
    {
        CancelPendingLogin();
        _error = null;
        DeviceCodePrompt prompt = await _auth.StartDeviceFlowAsync(clientId, cancellationToken).ConfigureAwait(false);
        _prompt = prompt;
        var flow = new CancellationTokenSource();
        _deviceFlow = flow;
        StateChanged?.Invoke();
        _ = AwaitApprovalAsync(clientId.Trim(), prompt, flow, Volatile.Read(ref _generation));
        return prompt;
    }

    private async Task AwaitApprovalAsync(string clientId, DeviceCodePrompt prompt, CancellationTokenSource flow, int generation)
    {
        try
        {
            TwitchTokens tokens = await _auth.AwaitApprovalAsync(clientId, prompt, flow.Token).ConfigureAwait(false);
            TwitchIdentity identity = await _auth.ValidateAsync(tokens.AccessToken, flow.Token).ConfigureAwait(false);

            // Signed out while this was finishing: the approval belongs to a session that is gone.
            if (Volatile.Read(ref _generation) != generation) return;

            _accessToken = tokens.AccessToken;
            _validatedAt = DateTimeOffset.UtcNow;
            _credentials = new StoredCredentials(tokens.RefreshToken, clientId, identity.Login, identity.UserId, identity.Scopes);
            _tokenStore.Save(_credentials);
            _error = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is TwitchAuthException or HttpRequestException)
        {
            _error = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_deviceFlow, flow)) { _deviceFlow = null; _prompt = null; }
            flow.Dispose();
            StateChanged?.Invoke();
        }
    }

    public void CancelPendingLogin()
    {
        CancellationTokenSource? flow = _deviceFlow;
        _deviceFlow = null;
        _prompt = null;
        flow?.Cancel();
    }

    public async Task LogoutAsync()
    {
        // Anything in flight is now stale, whether or not cancellation reached it in time.
        Interlocked.Increment(ref _generation);
        CancelPendingLogin();
        string? token = _accessToken;
        string clientId = ClientId;
        _credentials = null;
        _accessToken = null;
        _validatedAt = DateTimeOffset.MinValue;
        _error = null;
        _tokenStore.Clear();
        StateChanged?.Invoke();
        if (token is not null && clientId.Length > 0)
            await _auth.RevokeAsync(clientId, token, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Returns a token known to be valid, refreshing it when the cached one has aged out.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        StoredCredentials? credentials = _credentials
            ?? throw new TwitchAuthException("Du är inte inloggad på Twitch.");

        // Twitch tokens live for hours; re-validating every 30 minutes keeps us ahead of expiry
        // without a request per action.
        if (_accessToken is not null && DateTimeOffset.UtcNow - _validatedAt < TimeSpan.FromMinutes(30))
            return _accessToken;

        int generation = Volatile.Read(ref _generation);
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow - _validatedAt < TimeSpan.FromMinutes(30))
                return _accessToken;

            TwitchTokens tokens = await _auth.RefreshAsync(credentials.ClientId, credentials.RefreshToken, cancellationToken).ConfigureAwait(false);
            TwitchIdentity identity = await _auth.ValidateAsync(tokens.AccessToken, cancellationToken).ConfigureAwait(false);

            // A logout landed while Twitch was answering. Saving now would sign the user back in.
            if (Volatile.Read(ref _generation) != generation)
                throw new TwitchAuthException("Du är inte inloggad på Twitch.");

            _accessToken = tokens.AccessToken;
            _validatedAt = DateTimeOffset.UtcNow;
            _credentials = credentials with
            {
                RefreshToken = tokens.RefreshToken.Length > 0 ? tokens.RefreshToken : credentials.RefreshToken,
                Login = identity.Login,
                UserId = identity.UserId,
                Scopes = identity.Scopes
            };
            _tokenStore.Save(_credentials);
            return _accessToken;
        }
        catch (TwitchAuthTransientException)
        {
            // Twitch was unreachable or busy; the saved refresh token is still valid, so we keep
            // the session and let the next call try again.
            throw;
        }
        catch (TwitchAuthException)
        {
            // A refresh token Twitch has rejected will never work again – force a clean re-login.
            _credentials = null;
            _accessToken = null;
            _tokenStore.Clear();
            StateChanged?.Invoke();
            throw;
        }
        finally { _refreshLock.Release(); }
    }

    /// <summary>The IRC password format, so the authenticated chat connection can send messages.</summary>
    public async Task<string?> TryGetIrcTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!IsLoggedIn) return null;
        try { return await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TwitchAuthException or HttpRequestException) { return null; }
    }

    public void Dispose()
    {
        CancelPendingLogin();
        _refreshLock.Dispose();
    }
}
