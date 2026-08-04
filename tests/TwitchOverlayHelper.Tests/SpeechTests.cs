using System.Net;
using System.Net.Http;
using System.Text;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Speech;

namespace TwitchOverlayHelper.Tests;

/// <summary>Builds a speech service that never touches the network, disk of the user, or a speaker.</summary>
internal static class SpeechFixture
{
    public static string TempPath(string extension) =>
        Path.Combine(Path.GetTempPath(), "tohtests", Guid.NewGuid().ToString("N") + extension);

    public static NameSpeechService Service(
        AppSettings settings,
        HttpMessageHandler? handler = null,
        SpeechSecretStore? secrets = null,
        Func<string, double, Task>? play = null) =>
        new(new HttpClient(handler ?? new StubHandler()),
            settings,
            secrets ?? new SpeechSecretStore(TempPath(".bin")),
            play ?? ((_, _) => Task.CompletedTask),
            TempPath(string.Empty));

    /// <summary>Settings and keys that make <see cref="NameSpeechService.IsConfigured"/> true.</summary>
    public static (AppSettings Settings, SpeechSecretStore Secrets) Configured()
    {
        var settings = new AppSettings();
        settings.Normalize();
        settings.Speech.Enabled = true;
        settings.Speech.VoiceId = "voice-1";

        var secrets = new SpeechSecretStore(TempPath(".bin"));
        secrets.Save(new SpeechSecrets("deepseek-key", "eleven-key"));
        return (settings, secrets);
    }
}

/// <summary>Answers DeepSeek and ElevenLabs without leaving the process.</summary>
internal sealed class StubHandler(string? pronunciation = null, HttpStatusCode deepSeekStatus = HttpStatusCode.OK) : HttpMessageHandler
{
    public int DeepSeekCalls { get; private set; }
    public int ElevenLabsCalls { get; private set; }

    /// <summary>Lets a test make DeepSeek fail once and then recover.</summary>
    public HttpStatusCode DeepSeekStatus { get; set; } = deepSeekStatus;

    public string? LastRequestBody { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host.Contains("deepseek", StringComparison.Ordinal))
        {
            DeepSeekCalls++;
            LastRequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            if (DeepSeekStatus != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(DeepSeekStatus)
                {
                    Content = new StringContent("""{"error":{"message":"nej"}}""", Encoding.UTF8, "application/json")
                });

            string answer = System.Text.Json.JsonSerializer.Serialize(pronunciation ?? "Ex swee dragon");
            string body = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":" + answer + "}}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        ElevenLabsCalls++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x49, 0x44, 0x33, 0x04])
        });
    }
}

public sealed class PronunciationTextTests
{
    [Theory]
    [InlineData("Ex swee dragon", "Ex swee dragon")]
    [InlineData("\"Ex swee dragon\"", "Ex swee dragon")]
    [InlineData("xSwExDRAGONxSwEx → Ex swee dragon", "Ex swee dragon")]
    [InlineData("Uttal: Najo royalty", "Najo royalty")]
    [InlineData("Datapata\n\nAlternativ: Data pata", "Datapata")]
    [InlineData("- **Affexen**", "Affexen")]
    [InlineData("  Korvgubbe   i   Nyköping  ", "Korvgubbe i Nyköping")]
    public void KeepsOnlyTheLineThatShouldBeSpoken(string answer, string expected) =>
        Assert.Equal(expected, PronunciationText.Clean(answer, "reserv"));

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData(null)]
    public void FallsBackToTheWrittenNameWhenTheAnswerIsEmpty(string? answer) =>
        Assert.Equal("StiiaN", PronunciationText.Clean(answer, "StiiaN"));

    // A model that starts explaining itself must not have the explanation read out loud.
    [Fact]
    public void CutsAnAnswerThatRanAway()
    {
        string spoken = PronunciationText.Clean(new string('a', 400), "reserv");

        Assert.Equal(120, spoken.Length);
    }
}

public sealed class SpeechSettingsTests
{
    [Fact]
    public void FallsBackToTheCurrentModelsAndAValidVolume()
    {
        var settings = new SpeechSettings { DeepSeekModel = "  ", ElevenLabsModel = string.Empty, Volume = 8 };

        settings.Normalize();

        Assert.Equal("deepseek-v4-flash", settings.DeepSeekModel);
        Assert.Equal("eleven_v3", settings.ElevenLabsModel);
        Assert.Equal(1, settings.Volume);
    }

    [Fact]
    public void IsPartOfTheSavedSettings()
    {
        var settings = new AppSettings { Speech = null! };

        settings.Normalize();

        Assert.NotNull(settings.Speech);
        Assert.False(settings.Speech.Enabled);
    }
}

public sealed class SpeechSecretStoreTests
{
    [Fact]
    public void RoundTripsTheKeysWithoutWritingThemInPlainText()
    {
        string path = SpeechFixture.TempPath(".bin");
        var store = new SpeechSecretStore(path);

        store.Save(new SpeechSecrets("deepseek-hemlig", "eleven-hemlig"));

        Assert.Equal("deepseek-hemlig", new SpeechSecretStore(path).Current.DeepSeekApiKey);
        Assert.DoesNotContain("deepseek-hemlig", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsMissingKeysInsteadOfFailing()
    {
        var store = new SpeechSecretStore(SpeechFixture.TempPath(".bin"));

        Assert.False(store.Current.IsComplete);

        store.Save(new SpeechSecrets("bara-en", string.Empty));
        Assert.False(store.Current.IsComplete);

        store.Clear();
        Assert.Equal(SpeechSecrets.Empty, store.Current);
    }
}

public sealed class NameSpeechServiceTests
{
    [Fact]
    public async Task ReadsTheNameTheLanguageModelHandedBackAndCachesBothSteps()
    {
        (AppSettings settings, SpeechSecretStore secrets) = SpeechFixture.Configured();
        var handler = new StubHandler("Ex swee dragon");
        var played = new List<string>();
        NameSpeechService service = SpeechFixture.Service(settings, handler, secrets, (path, _) => { played.Add(path); return Task.CompletedTask; });

        NameSpeechResult first = await service.SpeakAsync("xSwExDRAGONxSwEx");
        NameSpeechResult second = await service.SpeakAsync("xSwExDRAGONxSwEx");

        Assert.Equal("Ex swee dragon", first.Spoken);
        Assert.Equal("Ex swee dragon", second.Spoken);
        Assert.Null(first.Warning);
        // Both calls are billed once: the same chatter gets clicked again and again during a stream.
        Assert.Equal(1, handler.DeepSeekCalls);
        Assert.Equal(1, handler.ElevenLabsCalls);
        Assert.Equal(2, played.Count);
        Assert.True(File.Exists(played[0]));
    }

    // Hearing the name spelled roughly beats hearing nothing when only the rewriting step fails.
    [Fact]
    public async Task ReadsTheWrittenNameWhenDeepSeekFails()
    {
        (AppSettings settings, SpeechSecretStore secrets) = SpeechFixture.Configured();
        var handler = new StubHandler(deepSeekStatus: HttpStatusCode.Unauthorized);
        NameSpeechService service = SpeechFixture.Service(settings, handler, secrets);

        NameSpeechResult result = await service.SpeakAsync("StiiaN__");

        Assert.Equal("StiiaN__", result.Spoken);
        Assert.Contains("kunde inte tolkas", result.Warning);
        Assert.Equal(1, handler.ElevenLabsCalls);
    }

    // A minute of DeepSeek trouble must not condemn a name to being spelled out for the rest of
    // the stream, which is exactly what caching the fallback would do.
    [Fact]
    public async Task TriesTheInterpretationAgainAfterAFailedOne()
    {
        (AppSettings settings, SpeechSecretStore secrets) = SpeechFixture.Configured();
        var handler = new StubHandler("Data pata", HttpStatusCode.TooManyRequests);
        NameSpeechService service = SpeechFixture.Service(settings, handler, secrets);

        NameSpeechResult failed = await service.SpeakAsync("datapataaa");
        handler.DeepSeekStatus = HttpStatusCode.OK;
        NameSpeechResult recovered = await service.SpeakAsync("datapataaa");

        Assert.Equal("datapataaa", failed.Spoken);
        Assert.Equal("Data pata", recovered.Spoken);
        Assert.Null(recovered.Warning);
    }

    // Reasoning about a single name would spend the small token budget on deliberation.
    [Fact]
    public async Task AsksDeepSeekNotToThink()
    {
        (AppSettings settings, SpeechSecretStore secrets) = SpeechFixture.Configured();
        var handler = new StubHandler();
        NameSpeechService service = SpeechFixture.Service(settings, handler, secrets);

        await service.SpeakAsync("Najoroyalty");

        Assert.Contains("\"thinking\":{\"type\":\"disabled\"}", handler.LastRequestBody);
    }

    [Fact]
    public async Task RefusesWithoutKeysOrVoice()
    {
        var settings = new AppSettings();
        settings.Normalize();
        settings.Speech.Enabled = true;
        NameSpeechService service = SpeechFixture.Service(settings);

        SpeechException error = await Assert.ThrowsAsync<SpeechException>(() => service.SpeakAsync("Kajsa"));

        Assert.Contains("inte konfigurerad", error.Message);
        Assert.False(service.IsConfigured);
    }

    [Fact]
    public void StaysHiddenUntilItIsBothConfiguredAndTurnedOn()
    {
        (AppSettings settings, SpeechSecretStore secrets) = SpeechFixture.Configured();
        NameSpeechService service = SpeechFixture.Service(settings, secrets: secrets);

        Assert.True(service.IsConfigured);

        settings.Speech.Enabled = false;
        Assert.False(service.IsConfigured);
        // The test button in the settings window still works while the dock button is off.
        Assert.True(service.CanSpeak);
    }

    [Fact]
    public async Task ForgettingPronunciationsAsksTheLanguageModelAgain()
    {
        (AppSettings settings, SpeechSecretStore secrets) = SpeechFixture.Configured();
        var handler = new StubHandler("Datapata");
        NameSpeechService service = SpeechFixture.Service(settings, handler, secrets);

        await service.SpeakAsync("datapataaa");
        service.ForgetPronunciations();
        await service.SpeakAsync("datapataaa");

        Assert.Equal(2, handler.DeepSeekCalls);
        // The audio for the same spoken line is still on disk, so only the cheap step repeats.
        Assert.Equal(1, handler.ElevenLabsCalls);
    }
}
