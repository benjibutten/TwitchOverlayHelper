using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TwitchOverlayHelper.Speech;

/// <summary>
/// Rewrites a Twitch user name into something a voice model can actually say. Twitch names are full
/// of decorative x-es, doubled letters and shouted abbreviations that a TTS model would spell out
/// letter by letter, which is exactly the noise this feature exists to remove.
/// </summary>
public sealed class DeepSeekClient(HttpClient httpClient)
{
    private const string Endpoint = "https://api.deepseek.com/chat/completions";

    /// <summary>
    /// Kept verbatim as a system prompt: it is tuned wording, and the examples carry most of its
    /// weight. Only the user name is passed separately, so a name can never read as an instruction.
    /// </summary>
    internal const string SystemPrompt = """
        Du omvandlar användarnamn till naturlig taltext för ElevenLabs Eleven v3. Returnera endast en enda rad med det namn en människa sannolikt skulle säga högt. Skriv inga förklaringar, citattecken, pilar eller alternativa förslag. Optimera för uttal, inte för att bevara användarnamnets exakta visuella stavning.

        Regler:
        Skriv förkortningar fonetiskt som uttalbara ord. Versala bokstavsgrupper får inte lämnas kvar om ElevenLabs sannolikt skulle bokstavera dem. Exempel: SWE blir swee, inte S W E.
        Ta bort dekorativa bokstäver och tecken som x, X, understreck och bindestreck när de bara ramar in namnet.
        Ta bort en upprepad avslutande del när en människa normalt bara skulle säga huvudnamnet en gång.
        Ta bort avslutande siffror när de sannolikt inte uttalas som en del av namnet.
        Förkorta överdrivna bokstavsupprepningar. Exempel: pataaa blir pata.
        Dela upp tydliga ord, namn och orter så att de uttalas naturligt.
        Lägg till fonetiskt nödvändiga vokaler när ett konsonanttätt namn annars riskerar att bokstaveras eller uttalas trasigt.
        Gissa försiktigt. Ändra inte namnet till ett helt annat känt ord om tolkningen är osäker.
        Använd vanlig fonetisk stavning framför IPA. Använd endast formen /IPA/ för en kort del som sannolikt fortfarande skulle uttalas fel.
        Använd normal versalisering för namn, men skriv fonetiska förkortningar med små bokstäver när versaler skulle orsaka bokstavering.

        Exempel:
        xSwExDRAGONxSwEx → Ex swee dragon
        Najoroyalty → Najo royalty
        affexn → Affexen
        datapataaa → Datapata
        korvgubbeinykoping → Korvgubbe i Nyköping
        sniffsprutare1 → Sniffsprutare
        """;

    /// <summary>Returns the line the voice model should read for <paramref name="userName"/>.</summary>
    public async Task<string> RewriteNameAsync(string userName, string apiKey, string model, CancellationToken cancellationToken = default)
    {
        string body = JsonSerializer.Serialize(new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = $"Användarnamn: {userName}" }
            },
            stream = false,
            // V4 thinks by default. Reasoning about a single name would spend tokens – and the
            // budget below – on deliberation the task does not need, and can eat the whole answer.
            thinking = new { type = "disabled" },
            // The answer is one short line; a low temperature keeps the same name sounding the same.
            temperature = 0.3,
            max_tokens = 64
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new SpeechException(DescribeError(response.StatusCode, payload));

        return PronunciationText.Clean(ReadContent(payload), userName);
    }

    private static string? ReadContent(string payload)
    {
        try
        {
            JsonElement root = JsonDocument.Parse(payload).RootElement;
            if (!root.TryGetProperty("choices", out JsonElement choices) || choices.GetArrayLength() == 0) return null;
            return choices[0].TryGetProperty("message", out JsonElement message)
                   && message.TryGetProperty("content", out JsonElement content)
                ? content.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static string DescribeError(HttpStatusCode status, string payload)
    {
        string prefix = status switch
        {
            HttpStatusCode.Unauthorized => "DeepSeek nekade nyckeln – kontrollera API-nyckeln i inställningarna.",
            HttpStatusCode.PaymentRequired => "DeepSeek-kontot har slut på saldo.",
            HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity => "DeepSeek känner inte igen modellnamnet – kontrollera det i inställningarna.",
            HttpStatusCode.TooManyRequests => "För många anrop till DeepSeek på kort tid – vänta en stund.",
            _ => "DeepSeek svarade med ett fel."
        };
        string? detail = ReadErrorMessage(payload);
        return detail is null ? prefix : $"{prefix} ({detail})";
    }

    private static string? ReadErrorMessage(string payload)
    {
        try
        {
            JsonElement root = JsonDocument.Parse(payload).RootElement;
            if (root.TryGetProperty("error", out JsonElement error) && error.TryGetProperty("message", out JsonElement message))
                return message.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}
