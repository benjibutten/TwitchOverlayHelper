using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TwitchOverlayHelper.Interop;
using TwitchOverlayHelper.Pets;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Spins;

namespace TwitchOverlayHelper;

/// <summary>
/// One pet in the winnable list: its portrait, whether the spin may hand it out, and how rare it is.
/// A view of the pet's own pet.json rather than of anything in settings.json – the file in the pet's
/// folder is where these two live, so a pet moved to another machine takes them along.
/// </summary>
public sealed class SpinPetRow : INotifyPropertyChanged
{
    private bool _isWinnable;
    private string _rarity;
    private bool _isEditing;
    private string _editName;
    private string _editId;

    public SpinPetRow(PetDefinition pet, ImageSource? portrait, int owners)
    {
        Id = pet.Id;
        Name = pet.Name;
        Emoji = pet.Emoji.Count > 0 ? pet.Emoji[0] : "🐾";
        Portrait = portrait;
        _isWinnable = pet.WinOnly;
        _rarity = PetRarity.Normalize(pet.Rarity);
        Owners = owners;
        _editName = Name;
        _editId = Id;
    }

    public string Id { get; }
    public string Name { get; }
    public string Emoji { get; }
    public ImageSource? Portrait { get; }
    public int Owners { get; }

    public IReadOnlyList<string> Rarities => PetRarity.All;

    /// <summary>Hidden the moment there is a real portrait, so the emoji never shows through it.</summary>
    public Visibility EmojiVisibility => Portrait is null ? Visibility.Visible : Visibility.Collapsed;

    public string Subtitle => Owners switch
    {
        0 => Id,
        1 => $"{Id} · 1 ägare",
        _ => $"{Id} · {Owners} ägare"
    };

    public bool IsWinnable
    {
        get => _isWinnable;
        set { _isWinnable = value; Raise(); }
    }

    public string Rarity
    {
        get => _rarity;
        set { _rarity = value; Raise(); }
    }

    /// <summary>
    /// Whether the row is showing its two name boxes instead of the name. The boxes edit copies
    /// rather than <see cref="Name"/> and <see cref="Id"/> themselves, so an edit that is escaped –
    /// or refused because the name is taken – leaves the row saying what the pet is actually called.
    /// </summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            if (value) (_editName, _editId) = (Name, Id);
            Raise();
            Raise(nameof(ReadVisibility));
            Raise(nameof(EditVisibility));
            Raise(nameof(EditName));
            Raise(nameof(EditId));
        }
    }

    public Visibility ReadVisibility => _isEditing ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EditVisibility => _isEditing ? Visibility.Visible : Visibility.Collapsed;

    public string EditName
    {
        get => _editName;
        set { _editName = value; Raise(); }
    }

    public string EditId
    {
        get => _editId;
        set { _editId = value; Raise(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Everything about the lucky spin in one place: the reward that buys it, how the reel looks, the
/// two commands, and which pets it can hand out.
///
/// <para>Writes straight through rather than collecting an OK. The pet choices land in each pet's
/// own pet.json and the rest in settings.json, both saved as they are changed – the same as every
/// other settings window in the app.</para>
/// </summary>
public partial class SpinSettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly PetCatalog _catalog;
    private readonly SpinWinStore _wins;
    private readonly Action _save;
    private readonly Action _testSpin;
    private readonly Func<string, int, Task<string>> _createReward;
    private readonly Func<string, string?> _inspectUrl;
    private readonly ObservableCollection<SpinPetRow> _pets = [];
    private bool _loading = true;

    /// <param name="createReward">
    /// Creates the reward in Twitch from a title and a cost, answering the new reward's id. Handed
    /// in rather than reached for, because the API client and the broadcaster live in the main
    /// window and this window has no business holding either.
    /// </param>
    /// <param name="inspectUrl">
    /// The address of one pet's inspection view, or null while the local server is not running.
    /// Handed in for the same reason: the server is the main window's, not this one's.
    /// </param>
    public SpinSettingsWindow(
        AppSettings settings,
        PetCatalog catalog,
        SpinWinStore wins,
        Action save,
        Action testSpin,
        Func<string, int, Task<string>> createReward,
        Func<string, string?> inspectUrl)
    {
        _settings = settings;
        _catalog = catalog;
        _wins = wins;
        _save = save;
        _testSpin = testSpin;
        _createReward = createReward;
        _inspectUrl = inspectUrl;

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
        GiftMinutesBox.Text = Spin.GiftTimeoutMinutes.ToString();
        SelectSide(Spin.Side);
        UpdateRewardStatus();
        LoadPets();
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

    private void LoadPets()
    {
        _pets.Clear();
        foreach (PetDefinition pet in _catalog.Pets)
            _pets.Add(new SpinPetRow(pet, Portrait(pet), _wins.OwnerCount(pet.Id)));

        PetEmptyText.Visibility = _pets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The pet's first spritesheet cell – the standing still frame – as a picture for the list.
    ///
    /// <para>Null for anything that will not decode, which is a real possibility rather than a
    /// theoretical one: the sheets are WebP, and Windows decodes those through a codec that a
    /// machine may simply not have. The row then shows the pet's emoji instead, and everything on
    /// it still works; a missing preview must not be able to stop somebody marking a pet winnable.
    /// </para>
    /// </summary>
    private static ImageSource? Portrait(PetDefinition pet)
    {
        if (pet.SpriteFile is not { Length: > 0 } file || !File.Exists(file)) return null;

        try
        {
            BitmapFrame frame = BitmapDecoder.Create(
                new Uri(file), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];

            // Cells are 192×208 and every sheet is 8 wide, so the row count falls out of the
            // sheet's own proportions – the same reasoning the overlay uses.
            int rows = (int)Math.Round(frame.PixelHeight * 8 * 192.0 / (frame.PixelWidth * 208.0));
            if (rows is not (9 or 11)) rows = pet.SpriteVersion >= 2 ? 11 : 9;

            int width = frame.PixelWidth / 8;
            int height = frame.PixelHeight / rows;
            if (width <= 0 || height <= 0) return null;

            return Cell(frame, width, height);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException
                                       or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// The top-left cell of a sheet, lifted out as a picture of its own with the alpha intact.
    ///
    /// <para>Copied pixel by pixel rather than wrapped in a <see cref="CroppedBitmap"/>, because of
    /// what Windows' WebP codec answers with: a frame in <c>Bgr32</c>, a format whose fourth byte
    /// is defined as meaningless – even though the decoder has filled it with the real alpha. WPF
    /// takes the format at its word and draws every see-through pixel opaque, which is what put a
    /// white block behind one pet and an orange one behind another: whatever colour happened to lie
    /// under the transparent parts of that particular sheet. The bytes are the same bytes; handing
    /// them back as <c>Bgra32</c> is what makes the alpha count again.</para>
    /// </summary>
    private static ImageSource Cell(BitmapSource sheet, int width, int height)
    {
        // Anything that is not already four bytes to a pixel – a paletted or 24-bit sheet – is
        // converted first, so the copy below can assume the one stride it knows how to read.
        BitmapSource source =
            sheet.Format == PixelFormats.Bgra32 || sheet.Format == PixelFormats.Pbgra32 || sheet.Format == PixelFormats.Bgr32
                ? sheet
                : new FormatConvertedBitmap(sheet, PixelFormats.Bgra32, null, 0);

        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        source.CopyPixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);

        // A sheet that really has no alpha comes back with that byte zero the whole way through,
        // and reading it as alpha would hand back a cell nobody can see. Opaque is the honest answer
        // for those, and the only cost is that a sheet whose first cell is genuinely empty shows as
        // a blank square rather than as nothing at all.
        bool transparentThroughout = true;
        for (int at = 3; at < pixels.Length && transparentThroughout; at += 4) transparentThroughout = pixels[at] == 0;
        if (transparentThroughout)
            for (int at = 3; at < pixels.Length; at += 4) pixels[at] = 255;

        BitmapSource cell = BitmapSource.Create(
            width, height, 96, 96,
            source.Format == PixelFormats.Pbgra32 ? PixelFormats.Pbgra32 : PixelFormats.Bgra32,
            null, pixels, stride);
        cell.Freeze();
        return cell;
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        Spin.Enabled = EnabledCheck.IsChecked == true;
        Spin.RewardName = RewardNameBox.Text;
        // Only while the app has not created the reward: once it has, the id is what ties the
        // setting to the one reward Twitch will accept an answer on.
        if (!Spin.Managed) Spin.RewardId = RewardIdBox.Text;
        Spin.Title = TitleBox.Text;
        Spin.ListCommand = ListCommandBox.Text;
        Spin.GiveCommand = GiveCommandBox.Text;
        Spin.PoolCommand = PoolCommandBox.Text;
        if (SideBox.SelectedItem is ComboBoxItem side && side.Tag is string tag) Spin.Side = tag;
        if (int.TryParse(CostBox.Text, out int cost)) Spin.Cost = cost;
        if (int.TryParse(SecondsBox.Text, out int seconds)) Spin.SpinSeconds = seconds;
        if (int.TryParse(GiftMinutesBox.Text, out int minutes)) Spin.GiftTimeoutMinutes = minutes;

        // Saving does not normalize – that happens on load – and these values are read while the
        // app runs. Without this a typed 999 would be a sixteen-minute reel blocking the queue for
        // the rest of the session. The boxes keep showing what was typed until focus leaves them,
        // so a half-typed number is not rewritten under the cursor.
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

    /// <summary>The same for the words: an emptied box comes back as the value it fell back to.</summary>
    private void Text_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_loading) return;
        TitleBox.Text = Spin.Title;
        ListCommandBox.Text = Spin.ListCommand;
        GiveCommandBox.Text = Spin.GiveCommand;
        PoolCommandBox.Text = Spin.PoolCommand;
    }

    /// <summary>
    /// Writes one pet's choices into its own pet.json. The list is rebuilt afterwards because the
    /// catalog reloads: a row still holding the old definition would show the pet as it was before
    /// the edit the moment anything refreshed it.
    /// </summary>
    private void Pet_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || (sender as FrameworkElement)?.DataContext is not SpinPetRow row) return;

        if (_catalog.TrySetWinnable(row.Id, row.IsWinnable, row.Rarity, out string error))
        {
            PetStatusText.Text = string.Empty;
            _save();
            return;
        }

        PetStatusText.Text = error;
        // The write failed, so the checkbox is showing something that is not true of the pet.
        Rebuild();
    }

    /// <summary>Reads the pet list back from the catalog without the change handlers firing on it.</summary>
    private void Rebuild()
    {
        _loading = true;
        LoadPets();
        _loading = false;
    }

    /// <summary>
    /// Opens one row for renaming, closing whichever was open. One at a time because that is what
    /// the two boxes are: the row's own name, not a column the whole list has.
    /// </summary>
    private void BeginEdit(SpinPetRow row)
    {
        foreach (SpinPetRow other in _pets) other.IsEditing = other == row;
        PetStatusText.Text = string.Empty;
    }

    private void PetName_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is SpinPetRow row) BeginEdit(row);
    }

    private void PetRename_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SpinPetRow row) BeginEdit(row);
    }

    private void PetEditCancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SpinPetRow row) row.IsEditing = false;
    }

    private void PetEditSave_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SpinPetRow row) CommitEdit(row);
    }

    /// <summary>Enter saves, Escape puts the row back the way it was. Neither reaches the window.</summary>
    private void PetEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SpinPetRow row) return;

        if (e.Key == Key.Enter) CommitEdit(row);
        else if (e.Key == Key.Escape) row.IsEditing = false;
        else return;
        e.Handled = true;
    }

    /// <summary>
    /// Puts the cursor in the name box the moment the row opens for editing. Done from the box's
    /// own visibility rather than from <see cref="BeginEdit"/>, because the template has not built
    /// the box yet at the point the row is told to open.
    /// </summary>
    private void PetEditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not TextBox box) return;
        box.Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    /// <summary>
    /// Writes a renamed pet to its pet.json, and moves everything that pointed at the old id along
    /// with it: the wins that are the whole point of the spin, the open gifts still looking for a
    /// home, and the channel's default pet.
    ///
    /// <para>Order matters. The manifest is written first because it is the one step that can be
    /// refused – a name another pet already answers to – and nothing else may move until the pet
    /// really has the new id.</para>
    /// </summary>
    private void CommitEdit(SpinPetRow row)
    {
        string newId = PetCatalog.SanitizeId(row.EditId);
        string newName = row.EditName.Trim();
        if (newId == row.Id && newName == row.Name)
        {
            row.IsEditing = false;
            return;
        }

        if (!_catalog.TryRename(row.Id, newId, newName, out string error))
        {
            PetStatusText.Text = error;
            return;
        }

        if (newId != row.Id)
        {
            int moved = _wins.RenamePet(row.Id, newId);
            if (string.Equals(_settings.Pets.DefaultPet, row.Id, StringComparison.OrdinalIgnoreCase))
                _settings.Pets.DefaultPet = newId;
            PetStatusText.Text = moved > 0
                ? $"“{newName}” heter nu {newId}. {moved} vinst{(moved == 1 ? "" : "er")} flyttades med."
                : $"“{newName}” heter nu {newId}.";
        }
        else PetStatusText.Text = string.Empty;

        Rebuild();
        _save();
    }

    private async void CreateReward_Click(object sender, RoutedEventArgs e)
    {
        string title = RewardNameBox.Text.Trim();
        if (title.Length == 0)
        {
            RewardStatusText.Text = "Ge belöningen ett namn först.";
            return;
        }

        // A second click writes a new id over one this app is already answering redemptions on, and
        // everything still open on the old reward falls outside both the matching and the startup
        // sweep – neither answered nor paid back. Asked rather than refused: moving to a new reward
        // is a real thing to want, and with the id box locked this is the only way there.
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
            // Twitch's own wording is the useful part here – a duplicate title above all, which is
            // the one a streamer meets and can do something about.
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

    /// <summary>
    /// Opens one pet alone in the browser, where a WebP spritesheet can actually be watched: WPF
    /// draws its first cell and nothing more, so a row that is empty or a frame that jumps is
    /// invisible from in here. A page for setting a pet up – the app neither needs it nor knows it
    /// is open, and closing the tab is the whole of putting it away.
    /// </summary>
    private void PetInspect_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SpinPetRow row) return;

        string? url = _inspectUrl(row.Id);
        if (url is null)
        {
            PetStatusText.Text = "Testvyn ritas av appens lokala server – slå på den under Chattdock först.";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            PetStatusText.Text = string.Empty;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            PetStatusText.Text = $"Kunde inte öppna webbläsaren. Adressen är {url}";
        }
    }

    private void TestSpin_Click(object sender, RoutedEventArgs e) => _testSpin();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
