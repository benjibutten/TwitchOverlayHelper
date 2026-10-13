using System.IO;
using System.Net.WebSockets;
using System.Text;
using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Twitch;

public sealed class TwitchChatClient : IAsyncDisposable
{
    // How long a sent message waits for Twitch's answer – the USERSTATE that confirms it or the
    // NOTICE that refuses it. On timeout the line is not echoed and the send still counts as
    // delivered: silence proves nothing, and failing would make the dock offer a message that
    // may have gone out for sending again.
    private static readonly TimeSpan EchoTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private Task? _runTask;
    private ClientWebSocket? _activeSocket;
    private string? _joinedChannel;
    private string _login = string.Empty;
    private string _userId = string.Empty;

    /// <summary>What Twitch said about a line we sent: the state that confirms it, or a refusal.</summary>
    private sealed record EchoAnswer(UserState? State, string? Refusal);

    /// <summary>
    /// The send that is waiting for Twitch's answer, if any. Set and cleared under
    /// <see cref="_sendLock"/> so there is never more than one, and completed from the read loop.
    /// </summary>
    private TaskCompletionSource<EchoAnswer?>? _echoWaiter;

    // The connection that waiter belongs to. USERSTATE is also sent on JOIN, so without this a
    // reconnect during a send would let the new connection's greeting confirm a message it never
    // carried.
    private ClientWebSocket? _echoSocket;

    // How many answers are owed to sends that gave up waiting. USERSTATE does not say which line
    // it answers, so a late answer must be discarded rather than handed to the next send: a send
    // without an id only loses pin and delete, while one with the wrong id pins or deletes
    // somebody else's message.
    private int _staleEchoAnswers;

    /// <summary>
    /// Works out which words in a line we wrote were emotes, for the echo of our own messages.
    /// Twitch reports emote positions to everyone except the sender.
    /// </summary>
    public Func<string, IReadOnlyList<EmoteSpan>>? ResolveEmotes { get; set; }

    public event Action<ChatMessage>? MessageReceived;
    public event Action<ChatModerationEvent>? ModerationReceived;
    public event Action<ChatEvent>? EventReceived;
    public event Action<string>? StatusChanged;
    public event Action<string>? RoomDiscovered;
    public event Action? ConnectionStopped;

    public bool IsRunning => _runTask is { IsCompleted: false };

    /// <summary>True when the connection was authenticated, which is what sending a message requires.</summary>
    public bool CanSend { get; private set; }

    /// <summary>
    /// Sends a chat line over the live IRC connection. Requires an authenticated connection, and
    /// throws <see cref="TwitchApiException"/> when Twitch refuses the line.
    ///
    /// Twitch never sends our own message back down the connection that wrote it, so once Twitch
    /// has confirmed the line it is raised through <see cref="MessageReceived"/> like any other
    /// message.
    /// </summary>
    public async Task SendMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!CanSend) throw new InvalidOperationException("Chatten är inte inloggad – logga in för att kunna skriva.");

        // IRC treats CR/LF as command separators, so a newline in the text could inject a command.
        string line = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        if (line.Length == 0) return;
        if (line.Length > 480) line = line[..480];

        EchoAnswer? answer;
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read after the wait, not before: a reconnect while queued behind another send would
            // leave a socket captured earlier closed.
            ClientWebSocket? socket = _activeSocket;
            string? channel = _joinedChannel;
            if (!CanSend || socket is null || channel is null || socket.State != WebSocketState.Open)
                throw new InvalidOperationException("Chatten är inte inloggad – logga in för att kunna skriva.");

            // Registered before the send: the answer can arrive before the send call returns.
            var waiter = new TaskCompletionSource<EchoAnswer?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _echoSocket = socket;
            _echoWaiter = waiter;
            await SendAsync(socket, $"PRIVMSG #{channel} :{line}\r\n", cancellationToken).ConfigureAwait(false);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(EchoTimeout);
            await using CancellationTokenRegistration giveUp = deadline.Token.Register(() => waiter.TrySetResult(null));
            answer = await waiter.Task.ConfigureAwait(false);
            // The answer is late rather than absent; the read loop must not give it to the next line.
            if (answer is null) Interlocked.Increment(ref _staleEchoAnswers);
        }
        finally
        {
            _echoWaiter = null;
            _echoSocket = null;
            _sendLock.Release();
        }

        // Thrown rather than swallowed: the dock has already emptied the box, so this is the only
        // way the reader learns the message never reached the chat.
        if (answer?.Refusal is { } refusal)
            throw new TwitchApiException($"Twitch skickade inte meddelandet: {refusal}");

        if (answer?.State is { } state && BuildEcho(state, line) is { } echo) MessageReceived?.Invoke(echo);
    }

    /// <summary>
    /// Our own sent line as a message the views can show. Without an id from USERSTATE it gets a
    /// local one and is marked <see cref="ChatMessage.IsLocalEcho"/>, so the id is never handed to
    /// Helix.
    ///
    /// Returns null for a slash command, except "/me", which comes back as an action.
    /// </summary>
    private ChatMessage? BuildEcho(UserState state, string line)
    {
        bool isAction = line.StartsWith("/me ", StringComparison.OrdinalIgnoreCase);
        if (isAction) line = line[4..].Trim();
        else if (line[0] is '/' or '.') return null;
        if (line.Length == 0) return null;

        return new ChatMessage(
            state.MessageId ?? Guid.NewGuid().ToString("N"),
            state.DisplayName ?? (_login.Length > 0 ? _login : "Jag"),
            line,
            state.Color,
            state.Badges,
            IsFirstMessage: false,
            IsHighlighted: false,
            DateTimeOffset.Now,
            // Resolved here so the overlay and the dock both get a finished message.
            ResolveEmotes?.Invoke(line))
        {
            UserId = _userId,
            UserLogin = _login,
            HasModTag = state.HasModTag,
            IsAction = isAction,
            IsLocalEcho = state.MessageId is null
        };
    }

    /// <summary>
    /// Connects and keeps reconnecting. <paramref name="tokenProvider"/> is asked on every attempt,
    /// because a Twitch access token expires after hours and a reconnect with an expired one is
    /// rejected.
    /// </summary>
    public Task ConnectAsync(string channel, string? userName = null, Func<CancellationToken, Task<string?>>? tokenProvider = null, string? userId = null)
    {
        if (IsRunning) throw new InvalidOperationException("Chatten är redan ansluten.");
        channel = NormalizeChannel(channel);
        if (channel.Length == 0) throw new ArgumentException("Ange ett Twitch-kanalnamn.", nameof(channel));

        // For echoed messages: USERSTATE has no user-id tag, and the dock needs the id to open the
        // user sheet on a line we wrote ourselves.
        _userId = userId ?? string.Empty;
        _lifetime?.Dispose();
        _lifetime = new CancellationTokenSource();
        _runTask = RunWithReconnectAsync(channel, userName, tokenProvider, _lifetime.Token);
        _ = NotifyWhenStoppedAsync(_runTask);
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        CancellationTokenSource? lifetime = _lifetime;
        Task? runTask = _runTask;
        lifetime?.Cancel();
        if (runTask is not null)
        {
            try { await runTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        if (ReferenceEquals(_lifetime, lifetime))
        {
            _lifetime = null;
            _runTask = null;
            lifetime?.Dispose();
        }
        StatusChanged?.Invoke("Frånkopplad");
    }

    private async Task RunWithReconnectAsync(string channel, string? userName, Func<CancellationToken, Task<string?>>? tokenProvider, CancellationToken token)
    {
        int attempt = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                StatusChanged?.Invoke(attempt == 0 ? "Ansluter …" : "Återansluter …");
                await RunConnectionAsync(channel, userName, tokenProvider, token).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (TwitchAuthenticationException ex)
            {
                // Retrying with the same rejected credentials would loop forever.
                StatusChanged?.Invoke(ex.Message);
                break;
            }
            catch (Exception ex)
            {
                attempt++;
                int delay = Math.Min(20, 2 * attempt);
                StatusChanged?.Invoke($"Kontakt tappad – nytt försök om {delay} s ({ex.Message})");
                await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
            }
        }
    }

    private async Task NotifyWhenStoppedAsync(Task runTask)
    {
        try { await runTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally { ConnectionStopped?.Invoke(); }
    }

    private async Task RunConnectionAsync(string channel, string? userName, Func<CancellationToken, Task<string?>>? tokenProvider, CancellationToken token)
    {
        // Fetched before the socket opens so a refresh does not run down Twitch's login timeout.
        string? oauthToken = tokenProvider is null ? null : await tokenProvider(token).ConfigureAwait(false);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri("wss://irc-ws.chat.twitch.tv:443"), token).ConfigureAwait(false);

        bool authenticated = !string.IsNullOrWhiteSpace(userName) && !string.IsNullOrWhiteSpace(oauthToken);
        string nick = authenticated ? userName!.Trim().ToLowerInvariant() : $"justinfan{Random.Shared.Next(10000, 99999)}";
        string password = authenticated
            ? (oauthToken!.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase) ? oauthToken : "oauth:" + oauthToken)
            : "SCHMOOPIIE";

        // Twitch is happiest when each IRC command is its own WebSocket message.
        // Request metadata before joining so the first chat line already carries badges.
        await SendAsync(socket, "CAP REQ :twitch.tv/tags twitch.tv/commands\r\n", token).ConfigureAwait(false);
        await SendAsync(socket, $"PASS {password}\r\n", token).ConfigureAwait(false);
        await SendAsync(socket, $"NICK {nick}\r\n", token).ConfigureAwait(false);
        await SendAsync(socket, $"JOIN #{channel}\r\n", token).ConfigureAwait(false);
        _activeSocket = socket;
        _joinedChannel = channel;
        // Answers owed by the previous connection died with it.
        Volatile.Write(ref _staleEchoAnswers, 0);
        _login = authenticated ? nick : string.Empty;
        CanSend = authenticated;
        // Reading the chat matters more than sending in it: when a token cannot be had right now,
        // the connection continues anonymously instead of leaving the reader with nothing.
        StatusChanged?.Invoke(tokenProvider is not null && !authenticated
            ? $"Live • #{channel} • utan inloggning"
            : $"Live • #{channel}");
        try
        {
            await ReadLoopAsync(socket, token).ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_activeSocket, socket)) { _activeSocket = null; CanSend = false; }
        }
    }

    private async Task ReadLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        byte[] buffer = new byte[16 * 1024];
        var pending = new StringBuilder();
        using var messageBytes = new MemoryStream();
        string? discoveredRoom = null;
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            messageBytes.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException("Twitch stängde anslutningen");
                messageBytes.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;
            pending.Append(Encoding.UTF8.GetString(messageBytes.GetBuffer(), 0, checked((int)messageBytes.Length)));
            string buffered = pending.ToString();
            // Walked with an index rather than by re-slicing the remainder after every line: Twitch
            // batches lines into one frame when chat is busy, and each re-slice would copy the
            // rest of the frame.
            int start = 0;
            int lineEnd;
            while ((lineEnd = buffered.IndexOf("\r\n", start, StringComparison.Ordinal)) >= 0)
            {
                string line = buffered[start..lineEnd];
                start = lineEnd + 2;
                if (line.Length == 0) continue;
                if (line.StartsWith("PING", StringComparison.Ordinal))
                {
                    await SendAsync(socket, "PONG" + line[4..] + "\r\n", token).ConfigureAwait(false);
                    continue;
                }
                if (line.Contains(" NOTICE ", StringComparison.Ordinal))
                {
                    if (line.Contains("Login authentication failed", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Improperly formatted auth", StringComparison.OrdinalIgnoreCase))
                        throw new TwitchAuthenticationException("Inloggningen nekades av Twitch – kontrollera användarnamn och OAuth-token.");

                    // A refused PRIVMSG gets this and no USERSTATE. Only handed to a waiter on this
                    // same connection.
                    if (IrcMessageParser.TryParseSendRefusal(line, out string? refusal))
                    {
                        if (SpendStaleAnswer()) continue;
                        if (ReferenceEquals(_echoSocket, socket))
                        {
                            Interlocked.Exchange(ref _echoWaiter, null)?.TrySetResult(new EchoAnswer(null, refusal));
                            continue;
                        }
                    }
                }
                if (discoveredRoom is null)
                {
                    string? roomId = IrcMessageParser.TryGetRoomId(line);
                    if (!string.IsNullOrWhiteSpace(roomId))
                    {
                        discoveredRoom = roomId;
                        RoomDiscovered?.Invoke(roomId);
                    }
                }
                if (IrcMessageParser.TryParseChatMessage(line, out ChatMessage? message)) { MessageReceived?.Invoke(message!); continue; }
                if (IrcMessageParser.TryParseUserState(line, out UserState? userState))
                {
                    // Also sent on JOIN, when nobody is waiting. Scoped to the sending connection so
                    // a reconnect greeting cannot confirm a send that went out on the old socket.
                    if (SpendStaleAnswer()) continue;
                    if (ReferenceEquals(_echoSocket, socket))
                        Interlocked.Exchange(ref _echoWaiter, null)?.TrySetResult(new EchoAnswer(userState, null));
                    continue;
                }
                if (IrcMessageParser.TryParseModerationEvent(line, out ChatModerationEvent? moderation)) { ModerationReceived?.Invoke(moderation!); continue; }
                if (IrcMessageParser.TryParseUserNotice(line, out ChatEvent? chatEvent)) EventReceived?.Invoke(chatEvent!);
            }
            // Whatever is left after the last complete line is the start of the next one.
            pending.Clear();
            pending.Append(buffered, start, buffered.Length - start);
        }
    }

    /// <summary>
    /// Takes one answer off the debt left by sends that timed out. Returns true when the current
    /// answer belongs to such a send and must be discarded. Call only from the read loop.
    /// </summary>
    private bool SpendStaleAnswer()
    {
        if (Volatile.Read(ref _staleEchoAnswers) <= 0) return false;
        Interlocked.Decrement(ref _staleEchoAnswers);
        return true;
    }

    private static async Task SendAsync(ClientWebSocket socket, string message, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
    }

    internal static string NormalizeChannel(string value)
    {
        string channel = value.Trim().TrimStart('#').ToLowerInvariant();
        if (Uri.TryCreate(channel, UriKind.Absolute, out Uri? uri) && uri.Host.EndsWith("twitch.tv", StringComparison.OrdinalIgnoreCase))
            channel = uri.AbsolutePath.Trim('/').Split('/')[0].ToLowerInvariant();
        return new string(channel.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }
}

public sealed class TwitchAuthenticationException(string message) : Exception(message);
