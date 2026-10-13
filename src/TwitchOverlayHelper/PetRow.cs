using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TwitchOverlayHelper.Pets;

namespace TwitchOverlayHelper;

/// <summary>
/// One pet in a list: its portrait, whether the spin may hand it out, how rare it is, and the two
/// boxes it is renamed with. A view of the pet's own pet.json, not of settings.json.
/// </summary>
public sealed class PetRow : INotifyPropertyChanged
{
    private bool _isWinnable;
    private string _rarity;
    private bool _isEditing;
    private string _editName;
    private string _editId;

    public PetRow(PetDefinition pet, int owners)
    {
        Id = pet.Id;
        Name = pet.Name;
        Emoji = pet.Emoji.Count > 0 ? pet.Emoji[0] : "🐾";
        Portrait = LoadPortrait(pet);
        _isWinnable = pet.WinOnly;
        _rarity = PetRarity.Normalize(pet.Rarity);
        Owners = owners;
        Aliases = pet.Aliases;
        _editName = Name;
        _editId = Id;
    }

    public string Id { get; }
    public string Name { get; }
    public string Emoji { get; }
    public ImageSource? Portrait { get; }
    public int Owners { get; }
    public IReadOnlyList<string> Aliases { get; }

    public IReadOnlyList<string> Rarities => PetRarity.All;

    /// <summary>Hidden the moment there is a real portrait, so the emoji never shows through it.</summary>
    public Visibility EmojiVisibility => Portrait is null ? Visibility.Visible : Visibility.Collapsed;

    public string Subtitle
    {
        get
        {
            string names = Aliases.Count > 0 ? $"{Id} · även {string.Join(", ", Aliases)}" : Id;
            return Owners switch
            {
                0 => names,
                1 => $"{names} · 1 ägare",
                _ => $"{names} · {Owners} ägare"
            };
        }
    }

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
    /// Whether the row is showing its two name boxes instead of the name. The boxes edit copies,
    /// so an abandoned or refused edit leaves <see cref="Name"/> and <see cref="Id"/> untouched.
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

    /// <summary>
    /// Whether the row's prize flag or rarity says something the catalog does not. False for the
    /// echo a freshly built row raises as its combo box binds, which is not an edit.
    /// </summary>
    public bool DiffersFrom(PetDefinition? pet) =>
        pet is not null && (pet.WinOnly != IsWinnable || PetRarity.Normalize(pet.Rarity) != Rarity);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// The pet's first spritesheet cell – the standing still frame – as a picture for the list.
    /// Null for anything that will not decode: the sheets are WebP, and a machine may lack the
    /// codec Windows decodes them with.
    /// </summary>
    private static ImageSource? LoadPortrait(PetDefinition pet)
    {
        if (pet.SpriteFile is not { Length: > 0 } file || !File.Exists(file)) return null;

        try
        {
            BitmapFrame frame = BitmapDecoder.Create(
                new Uri(file), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];

            // Cells are 192×208 and every sheet is 8 wide, so the row count falls out of the
            // sheet's own proportions, as it does in the overlay.
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
    /// The top-left cell of a sheet, lifted out as a frozen picture of its own with the alpha intact.
    /// </summary>
    private static ImageSource Cell(BitmapSource sheet, int width, int height)
    {
        // Copied pixel by pixel, not wrapped in a CroppedBitmap: Windows' WebP codec answers with
        // Bgr32 while filling the fourth byte with the real alpha, and WPF would draw every
        // see-through pixel opaque. Handing the same bytes back as Bgra32 makes the alpha count.
        // A paletted or 24-bit sheet is converted first, so the copy can assume four bytes a pixel.
        BitmapSource source =
            sheet.Format == PixelFormats.Bgra32 || sheet.Format == PixelFormats.Pbgra32 || sheet.Format == PixelFormats.Bgr32
                ? sheet
                : new FormatConvertedBitmap(sheet, PixelFormats.Bgra32, null, 0);

        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        source.CopyPixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);

        // A sheet with no alpha at all comes back with that byte zero throughout, which read as alpha
        // would be an invisible cell, so it is made opaque. A genuinely empty first cell then shows
        // as a blank square.
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
}
