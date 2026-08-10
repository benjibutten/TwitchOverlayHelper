using System.IO;
using System.Net.WebSockets;
using System.Text;
using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Twitch;

public sealed class TwitchChatClient : IAsyncDisposable
{
    /// <summary>
    /// How long a sent message waits for Twitch's answer – the USERSTATE that confirms it or the
    /// NOTICE that refuses it. Both normally come back in well under a second, so running out means
    /// neither arrived: the line is not echoed, and the send is reported as having gone out, because
    /// silence is the one answer that says nothing either way and treating it as a failure would put
    /// a message the reader had already sent back into the box.
    /// </summary>
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

    /// <summary>
    /// The connection that waiter belongs to. USERSTATE is also sent on JOIN, so a reconnect landing
    /// while a send is in flight would otherwise have the new connection's greeting confirm a
    /// message it never carried – and the send would be reported as delivered by a socket it was
    /// never written to.
    /// </summary>
    private ClientWebSocket? _echoSocket;

    /// <summary>
    /// How many answers are owed to sends that already gave up waiting. Nothing in USERSTATE says
    /// which line it answers, so an answer arriving after its send timed out would otherwise be
    /// handed to whatever send is waiting next – and that send would take the earlier line's id.
    /// Counted here and spent on the next answer instead, which is the safe way to be wrong: a send
    /// left without an id echoes as a local one and merely loses pin and delete, while a send given
    /// the wrong id pins or deletes somebody else's message.
    /// </summary>
    private int _staleEchoAnswers;

    /// <summary>
    /// Works out which words in a line we wrote were emotes. Twitch does that on the way to the
    /// viewers and tells everyone except the sender, so without it our own line is the one message
    /// in the column spelling its emotes out in letters. Handed in rather than reached for: what
    /// this account may send is a Helix question, and the socket has no business asking it.
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
    /// Sends a chat line over the live IRC connection. Requires an authenticated connection.
    ///
    /// Twitch never sends our own message back down the connection that wrote it – which is why a
    /// line written in the dock used to vanish while the same line typed on twitch.tv, arriving from
    /// a different connection, showed up fine. So the line is put into <see cref="MessageReceived"/>
    /// here, once Twitch has confirmed it, and reaches the views the same way every other message
    /// does rather than through a path of its own.
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
            // Read here rather than before the wait: a line queued behind another send waits out
            // that send's answer, and a reconnect landing in the meantime leaves the connection it
            // was going to be written to closed – the message would go out on a dead socket.
            ClientWebSocket? socket = _activeSocket;
            string? channel = _joinedChannel;
            if (!CanSend || socket is null || channel is null || socket.State != WebSocketState.Open)
                throw new InvalidOperationException("Chatten är inte inloggad – logga in för att kunna skriva.");

            // Registered before the send, not after: the answer can be on the wire before the send
            // call has even returned.
            var waiter = new TaskCompletionSource<EchoAnswer?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _echoSocket = socket;
            _echoWaiter = waiter;
            await SendAsync(socket, $"PRIVMSG #{channel} :{line}\r\n", cancellationToken).ConfigureAwait(false);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(EchoTimeout);
            await using CancellationTokenRegistration giveUp = deadline.Token.Register(() => waiter.TrySetResult(null));
            answer = await waiter.Task.ConfigureAwait(false);
            // Nothing came back in time, so this send's answer is late rather than absent: it is
            // still owed, and the read loop has to know not to give it to the next line.
            if (answer is null) Interlocked.Increment(ref _staleEchoAnswers);
        }
        finally
        {
            _echoWaiter = null;
            _echoSocket = null;
            _sendLock.Release();
        }

        // Twitch said no. Thrown rather than swallowed: the message never reached the chat, and the
        // dock has already emptied the box – without this the reader is told nothing at all and has
        // to notice for themselves that their line is missing.
        if (answer?.Refusal is { } refusal)
            throw new TwitchApiException($"Twitch skickade inte meddelandet: {refusal}");

        if (answer?.State is { } state && BuildEcho(state, line) is { } echo) MessageReceived?.Invoke(echo);
    }

    /// <summary>
    /// Our own sent line, dressed as a message the views can show. One thing it cannot know on its
    /// own: without an id from USERSTATE the line still needs one to be a message at all, so it gets
    /// a local one – marked as such, so nothing tries to hand that invention to Helix.
    ///
    /// Returns null for a slash command, which is an instruction to Twitch rather than something
    /// said – except "/me", which is speech and comes back as an action.
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
            // Worked out here rather than in each view: the overlay and the dock draw the same
            // message, and only one of them would ever have been taught to do this on its own.
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
    /// Connects and keeps reconnecting. The token is asked for per attempt rather than passed once:
    /// a Twitch access token expires after hours, and a reconnect the next morning with yesterday's
    /// token is rejected outright – which would end the chat rather than resume it.
    /// </summary>
    public Task ConnectAsync(string channel, string? userName = null, Func<CancellationToken, Task<string?>>? tokenProvider = null, string? userId = null)
    {
        if (IsRunning) throw new InvalidOperationException("Chatten är redan ansluten.");
        channel = NormalizeChannel(channel);
        if (channel.Length == 0) throw new ArgumentException("Ange ett Twitch-kanalnamn.", nameof(channel));

        // Carried along only so an echoed message names its author the way every other message does.
        // IRC never tells us our own numeric id – USERSTATE has no user-id tag – and without it the
        // dock could not open the user sheet on a line we wrote ourselves.
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
        // Whatever the old connection still owed died with it – this one answers only its own lines.
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
            int lineEnd;
            while ((lineEnd = buffered.IndexOf("\r\n", StringComparison.Ordinal)) >= 0)
            {
                string line = buffered[..lineEnd];
                buffered = buffered[(lineEnd + 2)..];
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

                    // A refused PRIVMSG is answered with this and nothing else, so a send waiting for
                    // its USERSTATE would otherwise wait out the whole timeout and then be reported
                    // as delivered. Only handed to a waiter on this same connection.
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
                    // Also sent on JOIN, when nobody is waiting – then it simply has no one to
                    // answer. Scoped to the connection the line was written to as well, so a
                    // reconnect greeting cannot confirm a send that went out on the old socket.
                    if (SpendStaleAnswer()) continue;
                    if (ReferenceEquals(_echoSocket, socket))
                        Interlocked.Exchange(ref _echoWaiter, null)?.TrySetResult(new EchoAnswer(userState, null));
                    continue;
                }
                if (IrcMessageParser.TryParseModerationEvent(line, out ChatModerationEvent? moderation)) { ModerationReceived?.Invoke(moderation!); continue; }
                if (IrcMessageParser.TryParseUserNotice(line, out ChatEvent? chatEvent)) EventReceived?.Invoke(chatEvent!);
            }
            pending.Clear();
            pending.Append(buffered);
        }
    }

    /// <summary>
    /// Takes one answer off the debt left by a send that timed out, if there is any – and says so,
    /// because that answer belongs to the line that gave up rather than to whatever is waiting now.
    /// Called only from the read loop, so nothing else can take the same one.
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
