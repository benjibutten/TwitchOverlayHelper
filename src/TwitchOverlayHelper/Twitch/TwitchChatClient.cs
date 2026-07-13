using System.Net.WebSockets;
using System.Text;
using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.Twitch;

public sealed class TwitchChatClient : IAsyncDisposable
{
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _lifetime;
    private Task? _runTask;

    public event Action<ChatMessage>? MessageReceived;
    public event Action<string>? StatusChanged;
    public event Action<string>? RoomDiscovered;

    public bool IsRunning => _runTask is { IsCompleted: false };

    public Task ConnectAsync(string channel, string? userName = null, string? oauthToken = null)
    {
        if (IsRunning) throw new InvalidOperationException("Chatten är redan ansluten.");
        channel = NormalizeChannel(channel);
        if (channel.Length == 0) throw new ArgumentException("Ange ett Twitch-kanalnamn.", nameof(channel));

        _lifetime = new CancellationTokenSource();
        _runTask = RunWithReconnectAsync(channel, userName, oauthToken, _lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        _lifetime?.Cancel();
        if (_runTask is not null)
        {
            try { await _runTask; } catch (OperationCanceledException) { }
        }
        StatusChanged?.Invoke("Frånkopplad");
    }

    private async Task RunWithReconnectAsync(string channel, string? userName, string? oauthToken, CancellationToken token)
    {
        int attempt = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                StatusChanged?.Invoke(attempt == 0 ? "Ansluter …" : "Återansluter …");
                await RunConnectionAsync(channel, userName, oauthToken, token);
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
                await Task.Delay(TimeSpan.FromSeconds(delay), token);
            }
        }
    }

    private async Task RunConnectionAsync(string channel, string? userName, string? oauthToken, CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        _socket = socket;
        await socket.ConnectAsync(new Uri("wss://irc-ws.chat.twitch.tv:443"), token);

        bool authenticated = !string.IsNullOrWhiteSpace(userName) && !string.IsNullOrWhiteSpace(oauthToken);
        string nick = authenticated ? userName!.Trim().ToLowerInvariant() : $"justinfan{Random.Shared.Next(10000, 99999)}";
        string password = authenticated
            ? (oauthToken!.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase) ? oauthToken : "oauth:" + oauthToken)
            : "SCHMOOPIIE";

        // Twitch is happiest when each IRC command is its own WebSocket message.
        // Request metadata before joining so the first chat line already carries badges.
        await SendAsync("CAP REQ :twitch.tv/tags twitch.tv/commands\r\n", token);
        await SendAsync($"PASS {password}\r\n", token);
        await SendAsync($"NICK {nick}\r\n", token);
        await SendAsync($"JOIN #{channel}\r\n", token);
        StatusChanged?.Invoke($"Live • #{channel}");

        byte[] buffer = new byte[16 * 1024];
        var pending = new StringBuilder();
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Twitch stängde anslutningen");
            pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            string buffered = pending.ToString();
            int lineEnd;
            while ((lineEnd = buffered.IndexOf("\r\n", StringComparison.Ordinal)) >= 0)
            {
                string line = buffered[..lineEnd];
                buffered = buffered[(lineEnd + 2)..];
                if (line.Length == 0) continue;
                if (line.StartsWith("PING", StringComparison.Ordinal))
                {
                    await SendAsync(line.Replace("PING", "PONG", StringComparison.Ordinal) + "\r\n", token);
                    continue;
                }
                if (line.Contains(" NOTICE ", StringComparison.Ordinal) &&
                    (line.Contains("Login authentication failed", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Improperly formatted auth", StringComparison.OrdinalIgnoreCase)))
                    throw new TwitchAuthenticationException("Inloggningen nekades av Twitch – kontrollera användarnamn och OAuth-token.");
                string? roomId = IrcMessageParser.TryGetRoomId(line);
                if (!string.IsNullOrWhiteSpace(roomId)) RoomDiscovered?.Invoke(roomId);
                if (IrcMessageParser.TryParseChatMessage(line, out ChatMessage? message)) MessageReceived?.Invoke(message!);
            }
            pending.Clear();
            pending.Append(buffered);
        }
    }

    private async Task SendAsync(string message, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        await _socket!.SendAsync(bytes, WebSocketMessageType.Text, true, token);
    }

    internal static string NormalizeChannel(string value)
    {
        string channel = value.Trim().TrimStart('#').ToLowerInvariant();
        if (Uri.TryCreate(channel, UriKind.Absolute, out Uri? uri) && uri.Host.EndsWith("twitch.tv", StringComparison.OrdinalIgnoreCase))
            channel = uri.AbsolutePath.Trim('/').Split('/')[0].ToLowerInvariant();
        return new string(channel.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}

public sealed class TwitchAuthenticationException(string message) : Exception(message);
