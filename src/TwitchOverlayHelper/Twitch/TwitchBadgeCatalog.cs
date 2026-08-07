using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Text.Json;

namespace TwitchOverlayHelper.Twitch;

public sealed class TwitchBadgeCatalog
{
    private readonly HttpClient _httpClient = new();

    // Read from the IRC and dock-server threads while the UI thread loads new badges into them,
    // so a plain Dictionary would occasionally corrupt or throw mid-lookup.
    //
    // Kept apart because they have different lifetimes. A global badge looks the same in every chat
    // on Twitch and can stay; a channel's own badges – subscriber tiers above all – belong to one
    // streamer, and the same "subscriber/6" is a different picture in the next channel.
    private readonly ConcurrentDictionary<(string Set, string Version), BadgeInfo> _global = new();
    private readonly ConcurrentDictionary<(string Set, string Version), BadgeInfo> _channel = new();

    /// <summary>The channel's own version wins, since a streamer may override a set that also exists globally.</summary>
    public bool TryGet(string set, string version, out BadgeInfo? badge) =>
        _channel.TryGetValue((set, version), out badge) || _global.TryGetValue((set, version), out badge);

    /// <summary>
    /// Drops the badges belonging to the channel being left. Called on every channel switch, not
    /// only on the ones we can reload after: without a login Twitch will not hand out badges at all,
    /// and the views then fall back to the plain word "SUB" – which is at least true, where the
    /// previous streamer's sub icon next to a stranger's name is not.
    /// </summary>
    public void ForgetChannel() => _channel.Clear();

    public async Task LoadAsync(string clientId, string token, string? broadcasterId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(token)) return;
        token = token.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase) ? token[6..] : token;
        clientId = clientId.Trim();
        token = token.Trim();

        await LoadEndpointAsync("https://api.twitch.tv/helix/chat/badges/global", _global, clientId, token, cancellationToken);
        if (string.IsNullOrWhiteSpace(broadcasterId)) return;

        // Replaced rather than merged: a set the previous channel had and this one does not would
        // otherwise survive underneath, and be picked over the global fallback that should show.
        _channel.Clear();
        await LoadEndpointAsync(
            $"https://api.twitch.tv/helix/chat/badges?broadcaster_id={Uri.EscapeDataString(broadcasterId)}",
            _channel, clientId, token, cancellationToken);
    }

    private async Task LoadEndpointAsync(
        string url,
        ConcurrentDictionary<(string Set, string Version), BadgeInfo> into,
        string clientId,
        string accessToken,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Client-Id", clientId);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        using Stream stream = await response.Content.ReadAsStreamAsync(token);
        using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        Read(json.RootElement, into);
    }

    /// <summary>Adds a Helix badge response by hand, through the same parser the loaders use.</summary>
    internal void Add(string json, bool channelOwned)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Read(document.RootElement, channelOwned ? _channel : _global);
    }

    private static void Read(JsonElement root, ConcurrentDictionary<(string Set, string Version), BadgeInfo> into)
    {
        foreach (JsonElement set in root.GetProperty("data").EnumerateArray())
        {
            string setId = set.GetProperty("set_id").GetString()!;
            foreach (JsonElement version in set.GetProperty("versions").EnumerateArray())
            {
                string id = version.GetProperty("id").GetString()!;
                into[(setId, id)] = new BadgeInfo(
                    version.GetProperty("image_url_2x").GetString()!,
                    version.GetProperty("title").GetString() ?? setId);
            }
        }
    }
}

public sealed record BadgeInfo(string ImageUrl, string Title);
