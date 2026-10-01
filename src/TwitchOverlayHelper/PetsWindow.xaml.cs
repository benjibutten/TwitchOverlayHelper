using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TwitchOverlayHelper.Interop;
using TwitchOverlayHelper.Pets;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Spins;

namespace TwitchOverlayHelper;

/// <summary>
/// Every pet in the pets folder, one row each: rename it, set its rarity and whether it is a spin
/// prize, and open it in the browser – alone in the inspector, or on the overlay's own page.
///
/// <para>Writes straight through rather than collecting an OK, into each pet's own pet.json – the
/// same as every other settings window in the app.</para>
/// </summary>
public partial class PetsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly PetCatalog _catalog;
    private readonly SpinWinStore _wins;
    private readonly Action _changed;
    private readonly Action _reload;
    private readonly Func<string, string?> _inspectUrl;
    private readonly Func<string, string?> _previewUrl;
    private readonly ObservableCollection<PetRow> _pets = [];

    /// <param name="changed">Saves the settings and tells the overlay and the main window a pet changed.</param>
    /// <param name="reload">Re-reads the pets folder, the way the button on the pets tab does.</param>
    /// <param name="inspectUrl">
    /// The address of one pet's inspection view, or null while the local server is not running.
    /// Handed in because the server is the main window's, not this one's.
    /// </param>
    /// <param name="previewUrl">The same for the page that shows the pet as the overlay draws it.</param>
    public PetsWindow(
        AppSettings settings,
        PetCatalog catalog,
        SpinWinStore wins,
        Action changed,
        Action reload,
        Func<string, string?> inspectUrl,
        Func<string, string?> previewUrl)
    {
        _settings = settings;
        _catalog = catalog;
        _wins = wins;
        _changed = changed;
        _reload = reload;
        _inspectUrl = inspectUrl;
        _previewUrl = previewUrl;

        InitializeComponent();
        DarkTitleBar.Enable(this);
        PetList.ItemsSource = _pets;
        RefreshPets();
    }

    /// <summary>
    /// Rebuilds the rows from the catalog. Also how the main window hands over a change made
    /// somewhere else; a rename in progress keeps what has been typed so far.
    /// </summary>
    public void RefreshPets()
    {
        PetRow? editing = _pets.FirstOrDefault(row => row.IsEditing);
        _pets.Clear();
        foreach (PetDefinition pet in _catalog.Pets)
        {
            var row = new PetRow(pet, _wins.OwnerCount(pet.Id));
            if (editing is not null && row.Id == editing.Id)
            {
                row.IsEditing = true;
                row.EditName = editing.EditName;
                row.EditId = editing.EditId;
            }
            _pets.Add(row);
        }

        EmptyText.Visibility = _pets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WarningsText.Text = string.Join("\n", _catalog.Warnings.Select(warning => $"⚠ {warning}"));
    }

    /// <summary>
    /// Writes one pet's rarity and prize flag into its own pet.json. The list is rebuilt when that
    /// fails, because the row is then showing something that is not true of the pet.
    /// </summary>
    private void Pet_Changed(object sender, RoutedEventArgs e)
    {
        // The rows' controls raise these as they bind, which DiffersFrom tells apart from an edit.
        if ((sender as FrameworkElement)?.DataContext is not PetRow row) return;
        if (!row.DiffersFrom(_catalog.Find(row.Id))) return;

        if (_catalog.TrySetWinnable(row.Id, row.IsWinnable, row.Rarity, out string error))
        {
            StatusText.Text = string.Empty;
            _changed();
            return;
        }

        StatusText.Text = error;
        RefreshPets();
    }

    /// <summary>
    /// Opens one row for renaming, closing whichever was open. One at a time because that is what
    /// the two boxes are: the row's own name, not a column the whole list has.
    /// </summary>
    private void BeginEdit(PetRow row)
    {
        foreach (PetRow other in _pets) other.IsEditing = other == row;
        StatusText.Text = string.Empty;
    }

    private void PetName_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is PetRow row) BeginEdit(row);
    }

    private void PetRename_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PetRow row) BeginEdit(row);
    }

    private void PetEditCancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PetRow row) row.IsEditing = false;
    }

    private void PetEditSave_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PetRow row) CommitEdit(row);
    }

    /// <summary>Enter saves, Escape puts the row back the way it was. Neither reaches the window.</summary>
    private void PetEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PetRow row) return;

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
    private void CommitEdit(PetRow row)
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
            StatusText.Text = error;
            return;
        }

        if (newId != row.Id)
        {
            int moved = _wins.RenamePet(row.Id, newId);
            if (string.Equals(_settings.Pets.DefaultPet, row.Id, StringComparison.OrdinalIgnoreCase))
                _settings.Pets.DefaultPet = newId;
            StatusText.Text = moved > 0
                ? $"“{newName}” heter nu {newId}. {moved} vinst{(moved == 1 ? "" : "er")} flyttades med."
                : $"“{newName}” heter nu {newId}.";
        }
        else StatusText.Text = string.Empty;

        row.IsEditing = false;
        RefreshPets();
        _changed();
    }

    /// <summary>
    /// The inspector: one pet alone with every animation to try. Opened in the browser because that
    /// is where a WebP spritesheet can actually be watched – WPF draws its first cell and no more.
    /// </summary>
    private void PetInspect_Click(object sender, RoutedEventArgs e) => OpenPetPage(sender, _inspectUrl);

    /// <summary>The pet alone on the overlay's own page, so it looks exactly as it will in OBS.</summary>
    private void PetPreview_Click(object sender, RoutedEventArgs e) => OpenPetPage(sender, _previewUrl);

    private void OpenPetPage(object sender, Func<string, string?> urlFor)
    {
        if ((sender as FrameworkElement)?.DataContext is not PetRow row) return;

        string? url = urlFor(row.Id);
        if (url is null)
        {
            StatusText.Text = "Sidorna ritas av appens lokala server – slå på den under Chattdock först.";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            StatusText.Text = string.Empty;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText.Text = $"Kunde inte öppna webbläsaren. Adressen är {url}";
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_catalog.PetsFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_catalog.PetsFolder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"Kunde inte öppna mappen: {_catalog.PetsFolder}";
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        _reload();
        StatusText.Text = $"Pets-mappen är inläst igen – {_pets.Count} pets.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
