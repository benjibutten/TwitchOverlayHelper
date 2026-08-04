using System.IO;
using System.Net.WebSockets;
using System.Text;
using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Twitch;

public sealed class TwitchChatClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private Task? _runTask;
    private ClientWebSocket? _activeSocket;
    private string? _joinedChannel;

    public event Action<ChatMessage>? MessageReceived;
    public event Action<ChatModerationEvent>? ModerationReceived;
    public event Action<string>? StatusChanged;
    public event Action<string>? RoomDiscovered;
    public event Action? ConnectionStopped;

    public bool IsRunning => _runTask is { IsCompleted: false };

    /// <summary>True when the connection was authenticated, which is what sending a message requires.</summary>
    public bool CanSend { get; private set; }

    /// <summary>Sends a chat line over the live IRC connection. Requires an authenticated connection.</summary>
    public async Task SendMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        ClientWebSocket? socket = _activeSocket;
        string? channel = _joinedChannel;
        if (!CanSend || socket is null || channel is null || socket.State != WebSocketState.Open)
            throw new InvalidOperationException("Chatten är inte inloggad – logga in för att kunna skriva.");

        // IRC treats CR/LF as command separators, so a newline in the text could inject a command.
        string line = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        if (line.Length == 0) return;
        if (line.Length > 480) line = line[..480];

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SendAsync(socket, $"PRIVMSG #{channel} :{line}\r\n", cancellationToken).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    /// <summary>
    /// Connects and keeps reconnecting. The token is asked for per attempt rather than passed once:
    /// a Twitch access token expires after hours, and a reconnect the next morning with yesterday's
    /// token is rejected outright – which would end the chat rather than resume it.
    /// </summary>
    public Task ConnectAsync(string channel, string? userName = null, Func<CancellationToken, Task<string?>>? tokenProvider = null)
    {
        if (IsRunning) throw new InvalidOperationException("Chatten är redan ansluten.");
        channel = NormalizeChannel(channel);
        if (channel.Length == 0) throw new ArgumentException("Ange ett Twitch-kanalnamn.", nameof(channel));

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
                if (line.Contains(" NOTICE ", StringComparison.Ordinal) &&
                    (line.Contains("Login authentication failed", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Improperly formatted auth", StringComparison.OrdinalIgnoreCase)))
                    throw new TwitchAuthenticationException("Inloggningen nekades av Twitch – kontrollera användarnamn och OAuth-token.");
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
                if (IrcMessageParser.TryParseModerationEvent(line, out ChatModerationEvent? moderation)) ModerationReceived?.Invoke(moderation!);
            }
            pending.Clear();
            pending.Append(buffered);
        }
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
