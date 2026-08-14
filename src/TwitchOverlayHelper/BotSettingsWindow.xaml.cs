using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using TwitchOverlayHelper.Bot;
using TwitchOverlayHelper.Interop;
using TwitchOverlayHelper.Settings;

namespace TwitchOverlayHelper;

/// <summary>
/// One row per thing the bot can say: whether it says it, in what words, and how long it keeps quiet
/// afterwards.
///
/// <para>The preview is the point of the whole window. A template is a sentence with holes in it, and
/// the only way to know whether the holes were spelled right is to see them filled – otherwise a
/// mistyped <c>{viewers}</c> is discovered by the channel rather than by the streamer.</para>
/// </summary>
public sealed class BotFlowRow : INotifyPropertyChanged
{
    private bool _enabled;
    private string _template = string.Empty;
    private string _cooldown = "0";
    private string _preview = string.Empty;

    public BotFlowRow(BotMessageRule rule, BotSettings settings)
    {
        Flow = rule.Flow;
        _enabled = rule.Enabled;
        _template = rule.Template;
        _cooldown = rule.CooldownSeconds.ToString();
        Refresh(settings);
    }

    public BotFlow Flow { get; }

    public string Title => BotFlowText.Title(Flow);

    public string Description => BotFlowText.Description(Flow);

    /// <summary>The words this row's template may use, spelled out under the box that takes them.</summary>
    public string PlaceholderHint =>
        BotFlowText.Placeholders(BotTemplate.PlaceholdersFor(Flow));

    public string CooldownLabel => "paus, sek";

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; Raise(); }
    }

    public string Template
    {
        get => _template;
        set { _template = value; Raise(); }
    }

    public string Cooldown
    {
        get => _cooldown;
        set { _cooldown = value; Raise(); }
    }

    public string Preview
    {
        get => _preview;
        private set { _preview = value; Raise(); }
    }

    /// <summary>Writes this row into the settings and works out what it would say.</summary>
    public void Apply(BotMessageRule rule, BotSettings settings)
    {
        rule.Enabled = _enabled;
        rule.Template = _template;
        if (int.TryParse(_cooldown, out int seconds)) rule.CooldownSeconds = seconds;
        Refresh(settings);
    }

    public void Refresh(BotSettings settings)
    {
        string rendered = BotTemplate.Render(_template, settings, BotTemplate.SampleFor(Flow, settings));
        Preview = rendered.Length > 0 ? "→ " + rendered : "→ (inget skrivs)";
    }

    /// <summary>Puts the shipped wording back without touching whether the row is on.</summary>
    public void ResetTemplate(BotSettings settings)
    {
        BotMessageRule shipped = BotSettings.Defaults.First(rule => rule.Flow == Flow);
        Template = shipped.Template;
        Cooldown = shipped.CooldownSeconds.ToString();
        Refresh(settings);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// One row of the custom command table. A view of a <see cref="BotCommand"/> rather than the thing
/// itself, so the cooldown can be a half-typed string in a box without becoming a nonsense number in
/// the settings.
/// </summary>
public sealed class BotCommandRow : INotifyPropertyChanged
{
    private string _command;
    private string _response;
    private string _cooldown;
    private bool _enabled;
    private bool _moderatorsOnly;

    public BotCommandRow(BotCommand command)
    {
        Source = command;
        _command = command.Command;
        _response = command.Response;
        _cooldown = command.CooldownSeconds.ToString();
        _enabled = command.Enabled;
        _moderatorsOnly = command.ModeratorsOnly;
    }

    /// <summary>The settings object this row edits, so removing a row can find it again.</summary>
    public BotCommand Source { get; }

    public string Command
    {
        get => _command;
        set { _command = value; Raise(); }
    }

    public string Response
    {
        get => _response;
        set { _response = value; Raise(); }
    }

    public string Cooldown
    {
        get => _cooldown;
        set { _cooldown = value; Raise(); }
    }

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; Raise(); }
    }

    public bool ModeratorsOnly
    {
        get => _moderatorsOnly;
        set { _moderatorsOnly = value; Raise(); }
    }

    /// <summary>
    /// Writes the row into the settings. The command word is stored as typed and only cleaned by
    /// Normalize: rewriting it under the cursor would make "!disc" impossible to type on the way to
    /// "!discord".
    /// </summary>
    public void Apply()
    {
        Source.Command = _command;
        Source.Response = _response;
        Source.Enabled = _enabled;
        Source.ModeratorsOnly = _moderatorsOnly;
        if (int.TryParse(_cooldown, out int seconds)) Source.CooldownSeconds = seconds;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// What each flow is called and when it happens, worded for the person choosing whether to switch it
/// on. Kept here rather than on <see cref="BotFlow"/> itself: the enum is a contract with the
/// settings file, and this is copy for one window.
/// </summary>
internal static class BotFlowText
{
    /// <summary>The "kan använda" line under a template box, for a flow's words plus the global ones.</summary>
    public static string Placeholders(IEnumerable<string> own) =>
        "Kan använda: " + string.Join(" ", own.Concat(BotTemplate.GlobalPlaceholders).Select(name => "{" + name + "}"));

    public static string Title(BotFlow flow) => flow switch
    {
        BotFlow.PetRefund => "Poängen kom tillbaka",
        BotFlow.PetFulfilled => "Besöket är slut",
        BotFlow.RefundBatch => "Flera återbetalningar på en gång",
        BotFlow.PetLawnFull => "Det är fullt",
        BotFlow.PetsDisabled => "Avstängt just nu",
        BotFlow.PetOverlayDown => "Overlayen är nere",
        BotFlow.PetOverlayBack => "Overlayen är igång igen",
        BotFlow.TtsAccepted => "Uppläsningen ligger i kön",
        BotFlow.TtsWaiting => "Ingen har svarat än",
        BotFlow.TtsSpoken => "Meddelandet lästes upp",
        BotFlow.TtsRefund => "Uppläsningen blev inte av",
        BotFlow.TtsQueueFull => "Kön för uppläsning är full",
        BotFlow.TtsUnavailable => "Uppläsning är inte igång",
        BotFlow.ModCallAck => "Mod-anropet gick fram",
        BotFlow.ModCallMissed => "Mod-anropet gick inte fram",
        BotFlow.Welcome => "Ny i chatten",
        BotFlow.Raid => "Raid",
        BotFlow.ShoutoutReceived => "Shoutout till er",
        BotFlow.Subscription => "Prenumeration",
        BotFlow.HypeTrainBegin => "Hypetåget startar",
        _ => "Hypetåget är slut"
    };

    public static string Description(BotFlow flow) => flow switch
    {
        BotFlow.PetRefund =>
            "När en inlösen betalas tillbaka. Skälet skrivs med dina ord – och bara skäl appen känner igen släpps ut i chatten, aldrig ett felmeddelande från en tjänst.",
        BotFlow.PetFulfilled =>
            "När något levt klart och köpet bokförts som levererat. Avstängt från början: i en aktiv kanal blir det en rad var femte minut.",
        BotFlow.RefundBatch =>
            "Städningen efter en omstart svarar på allt som blev kvar från förra sändningen. En rad i stället för trettio – stänger du av den sägs ingenting alls om en sådan hög, för alternativet är just de trettio raderna.",
        BotFlow.PetLawnFull => "När någon löser in men det redan är fullt på skärmen.",
        BotFlow.PetsDisabled => "När någon löser in medan funktionen är avstängd i appen.",
        BotFlow.PetOverlayDown =>
            "När ingen overlay varit igång på en halv minut. Poäng som spenderas nu kommer tillbaka, och det är värt att säga innan någon spenderar dem.",
        BotFlow.PetOverlayBack => "När en overlay kopplar upp sig igen efter att ha varit borta.",
        BotFlow.TtsAccepted => "Direkt när någon betalat och begäran lagts i kön för godkännande.",
        BotFlow.TtsWaiting =>
            "När en begäran legat obesvarad längre än tiden du satt under Bot-fliken. Detta är raden som säger “den ligger kvar, du är inte glömd”.",
        BotFlow.TtsSpoken => "När meddelandet faktiskt lästs upp i sändningen.",
        BotFlow.TtsRefund =>
            "När en uppläsning nekats, gått ut på tid eller misslyckats – och poängen gått tillbaka. Skickas bara när de faktiskt gjort det.",
        BotFlow.TtsQueueFull => "När kön är full och begäran avvisas direkt.",
        BotFlow.TtsUnavailable => "När någon betalar för en uppläsning medan funktionen är avstängd eller saknar röst.",
        BotFlow.ModCallAck => "När en moderator skrivit anropskommandot och kanterna tänts.",
        BotFlow.ModCallMissed =>
            "När någon skrivit kommandot och ingenting hände. Idag står det bara i loggen, där den som skrev det aldrig ser det.",
        BotFlow.Welcome => "Första gången någon skriver i kanalen.",
        BotFlow.Raid => "När kanalen blir raidad.",
        BotFlow.ShoutoutReceived => "När en annan kanal ger er en shoutout.",
        BotFlow.Subscription => "Nya prenumerationer, resubs och gåvor. Avstängt från början – Twitch säger redan det mesta av det.",
        BotFlow.HypeTrainBegin => "När ett hypetåg drar igång.",
        _ => "När hypetåget är slut, med nivån det nådde."
    };
}

/// <summary>
/// The editor for everything the bot can say. Every change is written straight through to the
/// settings – there is no OK button, the same way the rest of the app's settings work.
/// </summary>
public partial class BotSettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action<string> _sendTest;
    private readonly Action _onChanged;
    private readonly ObservableCollection<BotFlowRow> _rows = [];
    private readonly ObservableCollection<BotCommandRow> _commands = [];
    private bool _loading = true;

    public BotSettingsWindow(AppSettings settings, Action<string> sendTest, Action onChanged)
    {
        InitializeComponent();
        DarkTitleBar.Enable(this);
        _settings = settings;
        _sendTest = sendTest;
        _onChanged = onChanged;

        foreach (BotMessageRule rule in _settings.Bot.Messages) _rows.Add(new BotFlowRow(rule, _settings.Bot));
        FlowList.ItemsSource = _rows;
        foreach (BotCommand command in _settings.Bot.Commands) _commands.Add(new BotCommandRow(command));
        CommandList.ItemsSource = _commands;
        CommandHintText.Text = BotFlowText.Placeholders(BotTemplate.CommandPlaceholders);
        ShowCommandState();
        ShowMode();
        _loading = false;
    }

    private void ShowCommandState() =>
        CommandEmptyText.Visibility = _commands.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Command_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        foreach (BotCommandRow row in _commands) row.Apply();
        _onChanged();

        // Named out loud rather than silently dropped by Normalize: two rows answering the same word
        // means only the first will ever fire, and the second looks like it works.
        var duplicates = _commands
            .Select(row => EdgeAlertSettings.CleanCommand(row.Command))
            .Where(command => command.Length > 1)
            .GroupBy(command => command, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        StatusText.Text = duplicates.Length > 0
            ? $"Sparat, men {string.Join(" och ", duplicates)} finns på fler än en rad – bara den första svarar."
            : "Sparat.";
    }

    private void AddCommand_Click(object sender, RoutedEventArgs e)
    {
        var command = new BotCommand { Response = "Svaret boten skriver. {viewer} blir den som frågade." };
        _settings.Bot.Commands.Add(command);
        _commands.Add(new BotCommandRow(command));
        ShowCommandState();
        _onChanged();
        StatusText.Text = "Skriv kommandot och svaret i den nya raden.";
    }

    private void RemoveCommand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.DataContext is not BotCommandRow row) return;
        _settings.Bot.Commands.Remove(row.Source);
        _commands.Remove(row);
        ShowCommandState();
        _onChanged();
        StatusText.Text = "Kommandot är borttaget.";
    }

    /// <summary>
    /// Says out loud when none of this can reach chat. A window full of switches that do nothing is
    /// the single most confusing state this feature has, and it is one radio button away at all times.
    /// </summary>
    private void ShowMode()
    {
        if (_settings.Bot.IsActive)
        {
            WarningText.Visibility = Visibility.Collapsed;
            return;
        }
        WarningText.Visibility = Visibility.Visible;
        WarningText.Text = "Boten är avstängd under Bot-fliken, så ingenting här skickas till chatten än. Inställningarna sparas ändå.";
    }

    private void Rule_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        BotSettings bot = _settings.Bot;
        foreach (BotFlowRow row in _rows)
        {
            BotMessageRule? rule = bot.Messages.FirstOrDefault(saved => saved.Flow == row.Flow);
            if (rule is not null) row.Apply(rule, bot);
        }
        _onChanged();
        StatusText.Text = "Sparat.";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        foreach (BotFlowRow row in _rows) row.ResetTemplate(_settings.Bot);
        _loading = false;
        Rule_Changed(sender, e);
        StatusText.Text = "Appens egna formuleringar är tillbaka.";
    }

    /// <summary>
    /// Writes one line for real, with the same stand-in values the previews use. It goes through the
    /// bot's own queue, so what it proves is the whole chain – login, connection, rate limit – and
    /// not merely that a template renders.
    /// </summary>
    private void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.Bot.IsActive)
        {
            StatusText.Text = "Slå på boten under Bot-fliken först.";
            return;
        }

        if (_rows.FirstOrDefault(row => row.Enabled) is not { } first)
        {
            StatusText.Text = "Inget meddelande är påslaget att testa med.";
            return;
        }

        string line = BotTemplate.Render(first.Template, _settings.Bot, BotTemplate.SampleFor(first.Flow, _settings.Bot));
        if (line.Length == 0)
        {
            StatusText.Text = "Den raden är tom.";
            return;
        }
        _sendTest(line);
        StatusText.Text = $"Skickade “{line}” till chatten.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
