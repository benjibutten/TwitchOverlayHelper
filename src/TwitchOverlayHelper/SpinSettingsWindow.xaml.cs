using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TwitchOverlayHelper.Interop;
using TwitchOverlayHelper.Pets;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Spins;

namespace TwitchOverlayHelper;

/// <summary>
/// Everything about the lucky spin in one place: the reward that buys it, how the reel looks, the
/// commands, and which pets it can hand out. Everything else about a pet lives in
/// <see cref="PetsWindow"/>.
///
/// <para>Changes are written straight through, with no OK button: the pet choices to each pet's
/// own pet.json and the rest to settings.json.</para>
/// </summary>
public partial class SpinSettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly PetCatalog _catalog;
    private readonly SpinWinStore _wins;
    private readonly Action _save;
    private readonly Action _petsChanged;
    private readonly Action _testSpin;
    private readonly Func<string, int, Task<string>> _createReward;
    private readonly Action _openPets;
    private readonly ObservableCollection<PetRow> _pets = [];
    private bool _loading = true;

    /// <param name="save">Saves settings.json after a change to the spin's own settings.</param>
    /// <param name="petsChanged">Tells the overlay and the main window a pet.json was rewritten.</param>
    /// <param name="createReward">
    /// Creates the reward in Twitch from a title and a cost, answering the new reward's id. Throws
    /// with Twitch's own wording when the reward cannot be created.
    /// </param>
    /// <param name="openPets">Brings up the window where pets are renamed and previewed.</param>
    public SpinSettingsWindow(
        AppSettings settings,
        PetCatalog catalog,
        SpinWinStore wins,
        Action save,
        Action petsChanged,
        Action testSpin,
        Func<string, int, Task<string>> createReward,
        Action openPets)
    {
        _settings = settings;
        _catalog = catalog;
        _wins = wins;
        _save = save;
        _petsChanged = petsChanged;
        _testSpin = testSpin;
        _createReward = createReward;
        _openPets = openPets;

        InitializeComponent();
        DarkTitleBar.Enable(this);
        PetList.ItemsSource = _pets;
        Populate();
        _loading = false;
    }

    private SpinSettings Spin => _settings.Spin;

    private void Populate()
    {
        EnabledCheck.IsChecked = Spin.Enabled;
        RewardNameBox.Text = Spin.RewardName;
        RewardIdBox.Text = Spin.RewardId;
        RewardIdBox.IsReadOnly = Spin.Managed;
        CostBox.Text = Spin.Cost.ToString();
        SecondsBox.Text = Spin.SpinSeconds.ToString();
        TitleBox.Text = Spin.Title;
        ListCommandBox.Text = Spin.ListCommand;
        GiveCommandBox.Text = Spin.GiveCommand;
        PoolCommandBox.Text = Spin.PoolCommand;
        ReleaseCommandBox.Text = Spin.ReleaseCommand;
        GiftMinutesBox.Text = Spin.GiftTimeoutMinutes.ToString();
        SelectSide(Spin.Side);
        UpdateRewardStatus();
        RefreshPets();
    }

    private void SelectSide(string side)
    {
        foreach (object item in SideBox.Items)
            if (item is ComboBoxItem entry && (entry.Tag as string) == side)
            {
                SideBox.SelectedItem = entry;
                return;
            }
        SideBox.SelectedIndex = 0;
    }

    /// <summary>Rebuilds the pet rows from the catalog.</summary>
    public void RefreshPets()
    {
        _pets.Clear();
        foreach (PetDefinition pet in _catalog.Pets)
            _pets.Add(new PetRow(pet, _wins.OwnerCount(pet.Id)));

        PetEmptyText.Visibility = _pets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        Spin.Enabled = EnabledCheck.IsChecked == true;
        Spin.RewardName = RewardNameBox.Text;
        // An app-created reward's id is what ties the setting to the one reward Twitch will accept
        // an answer on, so it is not editable.
        if (!Spin.Managed) Spin.RewardId = RewardIdBox.Text;
        Spin.Title = TitleBox.Text;
        Spin.ListCommand = ListCommandBox.Text;
        Spin.GiveCommand = GiveCommandBox.Text;
        Spin.PoolCommand = PoolCommandBox.Text;
        Spin.ReleaseCommand = ReleaseCommandBox.Text;
        if (SideBox.SelectedItem is ComboBoxItem side && side.Tag is string tag) Spin.Side = tag;
        if (int.TryParse(CostBox.Text, out int cost)) Spin.Cost = cost;
        if (int.TryParse(SecondsBox.Text, out int seconds)) Spin.SpinSeconds = seconds;
        if (int.TryParse(GiftMinutesBox.Text, out int minutes)) Spin.GiftTimeoutMinutes = minutes;

        // Saving does not normalize, and these values are read while the app runs: an unclamped 999
        // would be a sixteen-minute reel blocking the queue. The boxes keep what was typed until
        // focus leaves them, so a half-typed number is not rewritten under the cursor.
        Spin.Normalize();
        _save();
        UpdateRewardStatus();
    }

    /// <summary>Puts the clamped value back in the box once the user has finished with it.</summary>
    private void Number_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_loading) return;
        CostBox.Text = Spin.Cost.ToString();
        SecondsBox.Text = Spin.SpinSeconds.ToString();
        GiftMinutesBox.Text = Spin.GiftTimeoutMinutes.ToString();
    }

    /// <summary>Puts the normalized words back in their boxes: an emptied box shows the value it fell back to.</summary>
    private void Text_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_loading) return;
        TitleBox.Text = Spin.Title;
        ListCommandBox.Text = Spin.ListCommand;
        GiveCommandBox.Text = Spin.GiveCommand;
        PoolCommandBox.Text = Spin.PoolCommand;
        ReleaseCommandBox.Text = Spin.ReleaseCommand;
    }

    /// <summary>Writes one pet's choices into its own pet.json.</summary>
    private void Pet_Changed(object sender, RoutedEventArgs e)
    {
        // The rows' controls raise these as they bind, which DiffersFrom tells apart from an edit.
        if ((sender as FrameworkElement)?.DataContext is not PetRow row) return;
        if (!row.DiffersFrom(_catalog.Find(row.Id))) return;

        if (_catalog.TrySetWinnable(row.Id, row.IsWinnable, row.Rarity, out string error))
        {
            PetStatusText.Text = string.Empty;
            _petsChanged();
            return;
        }

        PetStatusText.Text = error;
        // The write failed, so the checkbox is showing something that is not true of the pet.
        RefreshPets();
    }

    private void OpenPets_Click(object sender, RoutedEventArgs e) => _openPets();

    private async void CreateReward_Click(object sender, RoutedEventArgs e)
    {
        string title = RewardNameBox.Text.Trim();
        if (title.Length == 0)
        {
            RewardStatusText.Text = "Ge belöningen ett namn först.";
            return;
        }

        // A new id replaces one this app is answering redemptions on, and anything still open on
        // the old reward is then neither answered nor paid back. Asked, not refused: with the id
        // box locked this is the only way to move to a new reward.
        if (Spin.Managed && MessageBox.Show(
                this,
                $"Appen har redan skapat en belöning för lyckosnurren.\n\nSkapar du en ny tar den över, och den gamla ligger kvar i Twitch – ta bort eller stäng av den där, annars kan inlösen på den varken besvaras eller betalas tillbaka härifrån.\n\nSkapa “{title}” ändå?",
                "Lyckosnurren",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            RewardStatusText.Text = "Belöningen är kvar som den var.";
            return;
        }

        CreateRewardButton.IsEnabled = false;
        RewardStatusText.Text = "Skapar belöningen i Twitch …";
        try
        {
            string id = await _createReward(title, int.TryParse(CostBox.Text, out int cost) ? cost : Spin.Cost);
            Spin.RewardId = id;
            Spin.Managed = true;
            _save();
            _loading = true;
            Populate();
            _loading = false;
            RewardStatusText.Text = "Belöningen är skapad. Inlösen kan nu betalas tillbaka när något inte går vägen.";
        }
        catch (Exception ex)
        {
            // Twitch's own wording is the useful part, a duplicate title above all.
            RewardStatusText.Text = ex.Message;
        }
        finally { CreateRewardButton.IsEnabled = true; }
    }

    private void UpdateRewardStatus()
    {
        if (RewardStatusText.Text.StartsWith("Skapar", StringComparison.Ordinal)) return;
        RewardStatusText.Text = Spin.CanRefund
            ? "🔒 Skapad av appen – poängen kan lämnas tillbaka."
            : Spin.RewardId.Length > 0 || Spin.RewardName.Length > 0
                ? "— Belöningen är inte skapad av appen, så poängen kan aldrig lämnas tillbaka härifrån."
                : "Ingen belöning vald än. Skriv ett namn och klicka ⚡ så skapar appen den åt dig.";
    }

    private void TestSpin_Click(object sender, RoutedEventArgs e) => _testSpin();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
