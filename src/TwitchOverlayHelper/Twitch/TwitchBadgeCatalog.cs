using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Text.Json;

namespace TwitchOverlayHelper.Twitch;

public sealed class TwitchBadgeCatalog
{
    private readonly HttpClient _httpClient = new();
    private readonly Dictionary<(string Set, string Version), BadgeInfo> _badges = new();

    public bool TryGet(string set, string version, out BadgeInfo? badge) => _badges.TryGetValue((set, version), out badge);

    public async Task LoadAsync(string clientId, string token, string? broadcasterId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(token)) return;
        token = token.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase) ? token[6..] : token;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        _httpClient.DefaultRequestHeaders.Remove("Client-Id");
        _httpClient.DefaultRequestHeaders.Add("Client-Id", clientId.Trim());

        await LoadEndpointAsync("https://api.twitch.tv/helix/chat/badges/global", cancellationToken);
        if (!string.IsNullOrWhiteSpace(broadcasterId))
            await LoadEndpointAsync($"https://api.twitch.tv/helix/chat/badges?broadcaster_id={Uri.EscapeDataString(broadcasterId)}", cancellationToken);
    }

    private async Task LoadEndpointAsync(string url, CancellationToken token)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        using Stream stream = await response.Content.ReadAsStreamAsync(token);
        using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        foreach (JsonElement set in json.RootElement.GetProperty("data").EnumerateArray())
        {
            string setId = set.GetProperty("set_id").GetString()!;
            foreach (JsonElement version in set.GetProperty("versions").EnumerateArray())
            {
                string id = version.GetProperty("id").GetString()!;
                _badges[(setId, id)] = new BadgeInfo(
                    version.GetProperty("image_url_2x").GetString()!,
                    version.GetProperty("title").GetString() ?? setId);
            }
        }
    }
}

public sealed record BadgeInfo(string ImageUrl, string Title);
