using System.IO;
using System.Windows;
using System.Windows.Controls;
using TwitchOverlayHelper.Fight;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Web;

namespace TwitchOverlayHelper;

/// <summary>
/// The Fajt tab: the wait screen where the streamer's characters fight it out. Everything here is
/// pushed to an open wait screen as it changes, so the OBS source is set up once and never touched.
/// </summary>
public partial class MainWindow
{
    private bool _populatingFight;

    /// <summary>
    /// What the wait screen is told: the streamer's two fighters, every fighter the chat can vote
    /// for, the arena, the commands, the match rules and the banner.
    /// </summary>
    private DockFightSetup BuildFightSetup()
    {
        FightSettings fight = _settings.Fight;
        ArenaDefinition arena = _fightCatalog.ArenaOrFallback(fight.Arena);
        return new DockFightSetup(
            ToDock(_fightCatalog.FighterOrFallback(fight.Player1, 0), fight.Player1Outfit),
            ToDock(_fightCatalog.FighterOrFallback(fight.Player2, 1), fight.Player2Outfit),
            _fightCatalog.Fighters.Select(f => ToDock(f, 1)!).ToArray(),
            new DockArena(arena.Id,
                arena.ImageFile is null ? null : $"/fight/arena/{Uri.EscapeDataString(arena.Id)}?v={Stamp(arena.ImageFile)}",
                arena.Floor, arena.Left, arena.Right),
            new DockFightCommands(fight.CommandsEnabled, fight.CheerCommand, fight.HealCommand, fight.ShowCommandHint,
                fight.CooldownSeconds, fight.Pick1Command, fight.Pick2Command),
            new DockFightMatch(fight.CharacterSelect, fight.SelectSeconds, fight.WinsToWin, fight.ShowSupporters),
            fight.Headline,
            fight.Subline);

        static DockFighter? ToDock(FighterDefinition? f, int outfit)
        {
            if (f is null) return null;
            DockOutfit[] outfits = f.Outfits.Select(o => new DockOutfit(o.Code, OutfitName(o), SpriteUrl(f, o))).ToArray();
            FighterOutfit chosen = f.Outfit(outfit);
            return new DockFighter(f.Id, f.Name, SpriteUrl(f, chosen), f.Scale, chosen.Code, outfits, ToDockSpecial(f));
        }

        static DockSpecial? ToDockSpecial(FighterDefinition f) => f.Special is not { } s ? null : new DockSpecial(
            s.Name, s.Style,
            s.SpriteFile is null ? null : $"/fight/fighter/{Uri.EscapeDataString(f.Id)}/special?v={Stamp(s.SpriteFile)}",
            s.PropFile is null ? null : $"/fight/fighter/{Uri.EscapeDataString(f.Id)}/prop?v={Stamp(s.PropFile)}",
            s.Color);

        static string SpriteUrl(FighterDefinition f, FighterOutfit o) => o.Code == 1
            ? $"/fight/fighter/{Uri.EscapeDataString(f.Id)}?v={Stamp(o.SpriteFile)}"
            : $"/fight/fighter/{Uri.EscapeDataString(f.Id)}/{o.Code}?v={Stamp(o.SpriteFile)}";
    }

    /// <summary>What an outfit is called on screen and in the app: its own name, or its number.</summary>
    private static string OutfitName(FighterOutfit outfit) =>
        outfit.Name.Length > 0 ? outfit.Name : outfit.Code == 1 ? "Standard" : $"Klädsel {outfit.Code}";

    /// <summary>
    /// Changes with the file, so a sprite redrawn under the same id is fetched again rather than
    /// taken from the browser's memory of the old one.
    /// </summary>
    private static long Stamp(string file)
    {
        try { return File.GetLastWriteTimeUtc(file).Ticks / TimeSpan.TicksPerSecond; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private void PopulateFightControls()
    {
        FightSettings fight = _settings.Fight;
        _populatingFight = true;
        try
        {
            FightHeadlineBox.Text = fight.Headline;
            FightSublineBox.Text = fight.Subline;
            FightCommandsCheck.IsChecked = fight.CommandsEnabled;
            FightCheerBox.Text = fight.CheerCommand;
            FightHealBox.Text = fight.HealCommand;
            FightCooldownBox.Text = fight.CooldownSeconds.ToString();
            FightHintCheck.IsChecked = fight.ShowCommandHint;
            FightSupportersCheck.IsChecked = fight.ShowSupporters;
            FightSelectCheck.IsChecked = fight.CharacterSelect;
            FightSelectSecondsBox.Text = fight.SelectSeconds.ToString();
            FightPick1Box.Text = fight.Pick1Command;
            FightPick2Box.Text = fight.Pick2Command;
            FightWinsBox.SelectedItem = FightWinsBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag as string == fight.WinsToWin.ToString())
                ?? FightWinsBox.Items.Cast<ComboBoxItem>().First(item => item.Tag as string == "3");
        }
        finally { _populatingFight = false; }
        RefreshFightTexts(selected: null);
        RefreshFightCatalogUi();
        ShowFightCommandHint();
        ShowFightRulesHint();
    }

    private void ShowFightRulesHint()
    {
        FightSettings fight = _settings.Fight;
        string length = fight.WinsToWin > 0 ? $"först till {fight.WinsToWin} vunna ronder" : "utan slut";
        FightRulesHint.Text = fight.CharacterSelect
            ? $"Varje match börjar med karaktärsvalet: alla karaktärer visas och chatten har {fight.SelectSeconds} sekunder på sig. " +
              $"{fight.Pick1Command} my röstar på My till spelare 1, {fight.Pick2Command} zelda2 på Zelda i klädsel 2 till spelare 2 – en siffra efter namnet är klädseln. " +
              $"En röst per tittare och hörn; röstar man igen flyttas rösten. Flest röster vinner, och ett hörn ingen röstar på behåller den som stod där. Matchen spelas {length}" +
              (fight.WinsToWin > 0 ? ", sedan är det dags att välja igen." : ".")
            : $"Av – de två ovan möts, {length}" + (fight.WinsToWin > 0 ? ", och sedan börjar en ny match med samma karaktärer." : ".");
        FightP1Label.Text = fight.CharacterSelect ? "Spelare 1 – tills chatten valt" : "Spelare 1 (vänster)";
        FightP2Label.Text = fight.CharacterSelect ? "Spelare 2 – tills chatten valt" : "Spelare 2 (höger)";
    }

    /// <summary>
    /// The suggestion list: the streamer's own saved texts first, marked with a star, then the ones
    /// that ship with the app. <paramref name="selected"/> is left chosen, so it can be deleted.
    /// </summary>
    private void RefreshFightTexts(FightText? selected)
    {
        _populatingFight = true;
        try
        {
            FightPresetBox.Items.Clear();
            FightPresetBox.Items.Add(new ComboBoxItem { Content = "Välj en text …" });
            foreach (FightText text in _settings.Fight.SavedTexts)
                FightPresetBox.Items.Add(new ComboBoxItem { Content = "★ " + text.Label, Tag = text });
            foreach (FightText text in FightSettings.Suggestions)
                FightPresetBox.Items.Add(new ComboBoxItem { Content = text.Label, Tag = text });
            FightPresetBox.SelectedIndex = 0;
            if (selected is not null)
                foreach (ComboBoxItem item in FightPresetBox.Items)
                    if (ReferenceEquals(item.Tag, selected)) FightPresetBox.SelectedItem = item;
        }
        finally { _populatingFight = false; }
        FightDeleteTextButton.IsEnabled = SelectedSavedText() is not null;
    }

    /// <summary>The saved text chosen in the list, or null for a suggestion or nothing.</summary>
    private FightText? SelectedSavedText() =>
        FightPresetBox.SelectedItem is ComboBoxItem { Tag: FightText text } && _settings.Fight.SavedTexts.Contains(text) ? text : null;

    /// <summary>Rebuilds the three pickers and the list from the catalog, keeping what is chosen.</summary>
    private void RefreshFightCatalogUi()
    {
        FightSettings fight = _settings.Fight;
        _populatingFight = true;
        try
        {
            // One row per fighter and outfit, so the outfit is chosen in the same place as the fighter.
            (string Name, string Id)[] looks = _fightCatalog.Fighters
                .SelectMany(f => f.Outfits.Select(o => (o.Code == 1 ? f.Name : $"{f.Name} – {OutfitName(o)}", $"{f.Id}|{o.Code}")))
                .ToArray();
            Fill(FightP1Box, looks, Look(_fightCatalog.FighterOrFallback(fight.Player1, 0), fight.Player1Outfit));
            Fill(FightP2Box, looks, Look(_fightCatalog.FighterOrFallback(fight.Player2, 1), fight.Player2Outfit));
            Fill(FightArenaBox, _fightCatalog.Arenas.Select(a => (a.Name, a.Id)), _fightCatalog.ArenaOrFallback(fight.Arena).Id);
        }
        finally { _populatingFight = false; }

        FightArenaHint.Text = _fightCatalog.ArenaOrFallback(fight.Arena).Description;
        var lines = new List<string>
        {
            "Karaktärer: " + (_fightCatalog.Fighters.Count == 0 ? "inga – lägg en mapp under fighters" : string.Join(", ", _fightCatalog.Fighters.Select(f => f.Name))),
            "Arenor: " + string.Join(", ", _fightCatalog.Arenas.Select(a => a.Name))
        };
        lines.AddRange(_fightCatalog.Warnings.Select(w => "⚠ " + w));
        FightListText.Text = string.Join("\n", lines);

        static string? Look(FighterDefinition? f, int outfit) => f is null ? null : $"{f.Id}|{f.Outfit(outfit).Code}";

        static void Fill(ComboBox box, IEnumerable<(string Name, string Id)> items, string? selected)
        {
            box.Items.Clear();
            foreach ((string name, string id) in items)
            {
                var item = new ComboBoxItem { Content = name, Tag = id };
                box.Items.Add(item);
                if (string.Equals(id, selected, StringComparison.OrdinalIgnoreCase)) box.SelectedItem = item;
            }
        }
    }

    private void ShowFightCommandHint()
    {
        FightSettings fight = _settings.Fight;
        FightCommandHint.Text = fight.CommandsEnabled
            ? $"Chatten skriver {fight.CheerCommand} 1 eller {fight.CheerCommand} 2 för att fylla en supermätare – fyra hejarop och nästa spark blir en super som alltid träffar. {fight.HealCommand} 1 eller {fight.HealCommand} 2 ger 10 HP. Det går också att skriva p1/p2 eller karaktärens namn. Siffran är hörnet, så kommandona är desamma vem som än står där. Tittarens namn syns på skärmen."
            : "Av – fajten sköter sig själv.";
    }

    /// <summary>A fighter row's tag, "id|outfit", as the two settings it stands for.</summary>
    private static (string Id, int Outfit) ParseLook(string tag)
    {
        string[] parts = tag.Split('|', 2);
        return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out int code) ? code : 1);
    }

    /// <summary>Saved, and told to every open wait screen.</summary>
    private void FightChanged()
    {
        SaveSettings();
        _hub.PublishFightSetup();
    }

    private void FightPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _populatingFight) return;
        FightDeleteTextButton.IsEnabled = SelectedSavedText() is not null;
        if (FightPresetBox.SelectedItem is not ComboBoxItem { Tag: FightText text }) return;
        // Filled without going through the change handler twice, then saved and sent once. The
        // choice stays in the list – that is what the delete button acts on.
        _populatingFight = true;
        try
        {
            FightHeadlineBox.Text = text.Headline;
            FightSublineBox.Text = text.Subline;
        }
        finally { _populatingFight = false; }
        _settings.Fight.Headline = text.Headline;
        _settings.Fight.Subline = text.Subline;
        FightTextStatus.Text = "Lämna båda tomma så syns ingen text alls.";
        FightChanged();
    }

    private void FightText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || _populatingFight) return;
        _settings.Fight.Headline = FightHeadlineBox.Text.Trim();
        _settings.Fight.Subline = FightSublineBox.Text.Trim();
        // Once the words no longer match what is chosen, the list lets go of it: picking the same
        // text again then fills the fields again, and the delete button cannot hit the wrong one.
        if (FightPresetBox.SelectedItem is ComboBoxItem { Tag: FightText chosen }
            && (chosen.Headline != _settings.Fight.Headline || chosen.Subline != _settings.Fight.Subline))
        {
            _populatingFight = true;
            try { FightPresetBox.SelectedIndex = 0; }
            finally { _populatingFight = false; }
            FightDeleteTextButton.IsEnabled = false;
        }
        FightChanged();
    }

    private void FightSaveText_Click(object sender, RoutedEventArgs e)
    {
        FightSettings fight = _settings.Fight;
        fight.Headline = FightHeadlineBox.Text.Trim();
        fight.Subline = FightSublineBox.Text.Trim();
        if (!fight.SaveCurrentText())
        {
            FightTextStatus.Text = "Skriv något i rubriken eller raden under först.";
            return;
        }
        RefreshFightTexts(selected: fight.SavedTexts[0]);
        FightTextStatus.Text = "Sparad – den ligger överst i listan, markerad med ★.";
        SaveSettings();
    }

    private void FightDeleteText_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSavedText() is not { } text) return;
        _settings.Fight.SavedTexts.Remove(text);
        RefreshFightTexts(selected: null);
        // The fields keep their words: deleting a saved text is tidying the list, not the screen.
        FightTextStatus.Text = "Borttagen ur listan. Texten på skärmen är kvar tills du ändrar den.";
        SaveSettings();
    }

    private void FightMatch_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _populatingFight) return;
        FightSettings fight = _settings.Fight;
        if (FightP1Box.SelectedItem is ComboBoxItem { Tag: string p1 }) (fight.Player1, fight.Player1Outfit) = ParseLook(p1);
        if (FightP2Box.SelectedItem is ComboBoxItem { Tag: string p2 }) (fight.Player2, fight.Player2Outfit) = ParseLook(p2);
        if (FightArenaBox.SelectedItem is ComboBoxItem { Tag: string arena }) fight.Arena = arena;
        FightArenaHint.Text = _fightCatalog.ArenaOrFallback(fight.Arena).Description;
        FightChanged();
    }

    private void FightSwap_Click(object sender, RoutedEventArgs e)
    {
        FightSettings fight = _settings.Fight;
        string p1 = _fightCatalog.FighterOrFallback(fight.Player1, 0)?.Id ?? fight.Player1;
        string p2 = _fightCatalog.FighterOrFallback(fight.Player2, 1)?.Id ?? fight.Player2;
        (fight.Player1, fight.Player2) = (p2, p1);
        (fight.Player1Outfit, fight.Player2Outfit) = (fight.Player2Outfit, fight.Player1Outfit);
        RefreshFightCatalogUi();
        FightChanged();
    }

    private void FightCommand_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _populatingFight) return;
        FightSettings fight = _settings.Fight;
        fight.CommandsEnabled = FightCommandsCheck.IsChecked == true;
        fight.ShowCommandHint = FightHintCheck.IsChecked == true;
        fight.ShowSupporters = FightSupportersCheck.IsChecked == true;
        // The command words are taken when their box loses focus, not per keystroke: half-typed, a
        // word passes through states – "!" alone, the other command's word – that would be cleaned
        // into something the streamer never meant.
        if (int.TryParse(FightCooldownBox.Text.Trim(), out int seconds)) fight.CooldownSeconds = seconds;
        fight.Normalize();
        ShowFightCommandHint();
        FightChanged();
    }

    private void FightCommand_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => CommitFightWords();

    /// <summary>
    /// Takes every command word and number from its box, cleaned, and writes the clean versions
    /// back – a cheer renamed to "!p1" pushes the vote word aside, and both boxes have to show it.
    /// </summary>
    private void CommitFightWords()
    {
        FightSettings fight = _settings.Fight;
        fight.CheerCommand = FightCheerBox.Text;
        fight.HealCommand = FightHealBox.Text;
        fight.Pick1Command = FightPick1Box.Text;
        fight.Pick2Command = FightPick2Box.Text;
        if (int.TryParse(FightSelectSecondsBox.Text.Trim(), out int seconds)) fight.SelectSeconds = seconds;
        fight.Normalize();
        ShowFightCommandHint();
        ShowFightRulesHint();
        FightChanged();
        _populatingFight = true;
        try
        {
            FightCheerBox.Text = fight.CheerCommand;
            FightHealBox.Text = fight.HealCommand;
            FightCooldownBox.Text = fight.CooldownSeconds.ToString();
            FightPick1Box.Text = fight.Pick1Command;
            FightPick2Box.Text = fight.Pick2Command;
            FightSelectSecondsBox.Text = fight.SelectSeconds.ToString();
        }
        finally { _populatingFight = false; }
    }

    private void FightRules_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _populatingFight) return;
        // Like the command words, the seconds and the vote words wait for their box to lose focus;
        // only the switch takes effect at once.
        _settings.Fight.CharacterSelect = FightSelectCheck.IsChecked == true;
        ShowFightRulesHint();
        FightChanged();
    }

    private void FightRules_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => CommitFightWords();

    private void FightWins_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _populatingFight) return;
        if (FightWinsBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int wins))
            _settings.Fight.WinsToWin = wins;
        ShowFightRulesHint();
        FightChanged();
    }

    private void OpenFightFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_fightCatalog.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_fightCatalog.Folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            FightListText.Text = $"Kunde inte öppna mappen: {_fightCatalog.Folder}";
        }
    }

    private void ReloadFight_Click(object sender, RoutedEventArgs e)
    {
        _fightCatalog.Reload();
        RefreshFightCatalogUi();
        _hub.PublishFightSetup();
    }

    private void CopyFightUrl_Click(object sender, RoutedEventArgs e)
    {
        if (FightUrlBox.Text.Length == 0) return;
        try { Clipboard.SetText(FightUrlBox.Text); }
        catch (System.Runtime.InteropServices.COMException) { /* the address is still selectable */ }
    }

    private void OpenFight_Click(object sender, RoutedEventArgs e)
    {
        if (FightUrlBox.Text.Length == 0) return;
        OpenInBrowser(FightUrlBox.Text);
    }
}
