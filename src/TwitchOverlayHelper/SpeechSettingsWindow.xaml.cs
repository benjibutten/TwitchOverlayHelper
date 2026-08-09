using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using TwitchOverlayHelper.Interop;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Speech;

namespace TwitchOverlayHelper;

/// <summary>
/// Everything name pronunciation needs: the two API keys, which voice reads the names, and a test
/// button so the whole chain can be proven before the speaker button appears in the dock.
/// </summary>
public partial class SpeechSettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SpeechSecretStore _secrets;
    private readonly NameSpeechService _speech;
    private readonly Action _onChanged;
    private bool _loading = true;

    public SpeechSettingsWindow(AppSettings settings, SpeechSecretStore secrets, NameSpeechService speech, Action onChanged)
    {
        InitializeComponent();
        DarkTitleBar.Enable(this);
        _settings = settings;
        _secrets = secrets;
        _speech = speech;
        _onChanged = onChanged;
        Populate();
        _loading = false;
    }

    private void Populate()
    {
        SpeechSettings speech = _settings.Speech;
        EnabledCheck.IsChecked = speech.Enabled;
        VoiceIdBox.Text = speech.VoiceId;
        ElevenModelBox.Text = speech.ElevenLabsModel;
        DeepSeekModelBox.Text = speech.DeepSeekModel;
        VolumeSlider.Value = speech.Volume;
        if (speech.VoiceName.Length > 0) VoiceHint.Text = $"Vald röst: {speech.VoiceName}";
        UpdateKeyState();
        UpdateValueLabels();
    }

    private void UpdateKeyState()
    {
        SpeechSecrets secrets = _secrets.Current;
        DeepSeekKeyState.Text = secrets.DeepSeekApiKey.Length > 0 ? "✓ En nyckel är sparad." : "Ingen nyckel sparad.";
        ElevenKeyState.Text = secrets.ElevenLabsApiKey.Length > 0 ? "✓ En nyckel är sparad." : "Ingen nyckel sparad.";
    }

    private void UpdateValueLabels() => VolumeValue.Text = $"{_settings.Speech.Volume:P0}";

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        SpeechSettings speech = _settings.Speech;
        string previousModel = speech.DeepSeekModel;

        speech.Enabled = EnabledCheck.IsChecked == true;
        speech.VoiceId = VoiceIdBox.Text.Trim();
        speech.ElevenLabsModel = ElevenModelBox.Text.Trim();
        speech.DeepSeekModel = DeepSeekModelBox.Text.Trim();
        speech.Volume = VolumeSlider.Value;
        speech.Normalize();

        // A different language model will read the same name differently, so the old answers go.
        if (!string.Equals(previousModel, speech.DeepSeekModel, StringComparison.Ordinal)) _speech.ForgetPronunciations();

        UpdateValueLabels();
        _onChanged();
    }

    private void SaveKeys_Click(object sender, RoutedEventArgs e)
    {
        SpeechSecrets current = _secrets.Current;
        string deepSeek = DeepSeekKeyBox.Password.Trim();
        string eleven = ElevenKeyBox.Password.Trim();

        // Empty means "leave the saved key alone": the boxes cannot show what is already stored.
        _secrets.Save(new SpeechSecrets(
            deepSeek.Length > 0 ? deepSeek : current.DeepSeekApiKey,
            eleven.Length > 0 ? eleven : current.ElevenLabsApiKey));

        DeepSeekKeyBox.Clear();
        ElevenKeyBox.Clear();
        UpdateKeyState();
        KeyStatusText.Text = "Nycklarna är sparade krypterade för det här Windows-kontot.";
        _onChanged();
    }

    private void ClearKeys_Click(object sender, RoutedEventArgs e)
    {
        _secrets.Clear();
        DeepSeekKeyBox.Clear();
        ElevenKeyBox.Clear();
        UpdateKeyState();
        KeyStatusText.Text = "Nycklarna är borttagna – högtalarknappen försvinner ur docken.";
        _onChanged();
    }

    private async void FetchVoices_Click(object sender, RoutedEventArgs e)
    {
        string apiKey = _secrets.Current.ElevenLabsApiKey;
        if (apiKey.Length == 0)
        {
            VoiceHint.Text = "Spara ElevenLabs-nyckeln först, annars går rösterna inte att hämta.";
            return;
        }

        FetchVoicesButton.IsEnabled = false;
        VoiceHint.Text = "Hämtar röster …";
        try
        {
            IReadOnlyList<VoiceOption> voices = await _speech.GetVoicesAsync(apiKey);
            VoiceBox.ItemsSource = voices;
            VoiceBox.SelectedItem = voices.FirstOrDefault(voice => voice.VoiceId == _settings.Speech.VoiceId);
            VoiceHint.Text = voices.Count > 0
                ? $"{voices.Count} röster hämtade. Välj en i listan."
                : "Kontot har inga röster – lägg till en i ElevenLabs först.";
        }
        catch (Exception ex) when (ex is SpeechException or HttpRequestException)
        {
            VoiceHint.Text = ex.Message;
        }
        finally { FetchVoicesButton.IsEnabled = true; }
    }

    private void Voice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VoiceBox.SelectedItem is not VoiceOption voice) return;

        _settings.Speech.VoiceName = voice.Name;
        VoiceHint.Text = voice.Description.Length > 0 ? $"Vald röst: {voice.Name} ({voice.Description})" : $"Vald röst: {voice.Name}";
        // Writing the id back runs Setting_Changed; the extra save covers picking the voice that
        // was already selected, where the text never changes and TextChanged never fires.
        VoiceIdBox.Text = voice.VoiceId;
        _onChanged();
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        string name = TestNameBox.Text.Trim();
        if (name.Length == 0)
        {
            TestResultText.Text = "Skriv ett namn att testa.";
            return;
        }

        TestButton.IsEnabled = false;
        TestResultText.Text = "Tolkar och läser upp …";
        try
        {
            NameSpeechResult result = await _speech.SpeakAsync(name);
            TestResultText.Text = result.Warning is null
                ? $"Läser upp: {result.Spoken}"
                : $"Läser upp: {result.Spoken} – {result.Warning}";
        }
        catch (Exception ex) when (ex is SpeechException or HttpRequestException)
        {
            TestResultText.Text = ex.Message;
        }
        finally { TestButton.IsEnabled = true; }
    }

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        _speech.ForgetPronunciations();
        TestResultText.Text = "De sparade tolkningarna är borttagna. Nästa uppläsning frågar DeepSeek igen.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
