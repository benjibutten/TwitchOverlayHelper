using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TwitchOverlayHelper.Speech;

public sealed record VoiceOption(string VoiceId, string Name, string Description);

/// <summary>Text to speech for a single short line: the name, as a human would say it.</summary>
public sealed class ElevenLabsClient(HttpClient httpClient)
{
    private const string BaseUrl = "https://api.elevenlabs.io/v1";

    /// <summary>Returns MP3 audio. 44.1 kHz/128 kbps is the default tier-safe format.</summary>
    public async Task<byte[]> SynthesizeAsync(string text, string voiceId, string apiKey, string model, CancellationToken cancellationToken = default)
    {
        if (voiceId.Length == 0) throw new SpeechException("Ingen röst är vald för uppläsningen.");

        string body = JsonSerializer.Serialize(new { text, model_id = model });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/text-to-speech/{Uri.EscapeDataString(voiceId)}?output_format=mp3_44100_128")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("xi-api-key", apiKey);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new SpeechException(DescribeError(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)));

        byte[] audio = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (audio.Length == 0) throw new SpeechException("ElevenLabs returnerade inget ljud.");
        return audio;
    }

    /// <summary>The voices on the account, so the settings window can offer names instead of ids.</summary>
    public async Task<IReadOnlyList<VoiceOption>> GetVoicesAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/voices");
        request.Headers.Add("xi-api-key", apiKey);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new SpeechException(DescribeError(response.StatusCode, payload));

        var voices = new List<VoiceOption>();
        try
        {
            JsonElement root = JsonDocument.Parse(payload).RootElement;
            if (!root.TryGetProperty("voices", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return voices;
            foreach (JsonElement voice in list.EnumerateArray())
            {
                string id = ReadString(voice, "voice_id");
                if (id.Length == 0) continue;
                voices.Add(new VoiceOption(id, ReadString(voice, "name"), Describe(voice)));
            }
        }
        catch (JsonException) { throw new SpeechException("ElevenLabs svarade med något oväntat när rösterna hämtades."); }

        return voices;
    }

    /// <summary>The labels vary between voices, so whatever is present is joined into one hint.</summary>
    private static string Describe(JsonElement voice)
    {
        if (!voice.TryGetProperty("labels", out JsonElement labels) || labels.ValueKind != JsonValueKind.Object) return string.Empty;
        return string.Join(", ", labels.EnumerateObject()
            .Where(label => label.Value.ValueKind == JsonValueKind.String)
            .Select(label => label.Value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string DescribeError(HttpStatusCode status, string payload)
    {
        string prefix = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "ElevenLabs nekade nyckeln – kontrollera API-nyckeln i inställningarna.",
            HttpStatusCode.NotFound => "ElevenLabs hittade inte rösten – välj en röst igen i inställningarna.",
            HttpStatusCode.UnprocessableEntity => "ElevenLabs kunde inte använda modellen eller rösten.",
            HttpStatusCode.TooManyRequests => "För många anrop till ElevenLabs på kort tid – vänta en stund.",
            _ => "ElevenLabs svarade med ett fel."
        };
        string? detail = ReadDetail(payload);
        return detail is null ? prefix : $"{prefix} ({detail})";
    }

    private static string? ReadDetail(string payload)
    {
        try
        {
            JsonElement root = JsonDocument.Parse(payload).RootElement;
            if (!root.TryGetProperty("detail", out JsonElement detail)) return null;
            return detail.ValueKind switch
            {
                JsonValueKind.String => detail.GetString(),
                JsonValueKind.Object when detail.TryGetProperty("message", out JsonElement message) => message.GetString(),
                _ => null
            };
        }
        catch (JsonException) { return null; }
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
