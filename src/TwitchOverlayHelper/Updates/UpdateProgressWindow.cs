using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace TwitchOverlayHelper.Updates;

/// <summary>
/// The only thing shown while an update is downloading or being installed. It is built in code rather
/// than XAML because the installer runs it from a copy of the exe in a temp folder, before the normal
/// application resources are anywhere in play.
/// </summary>
internal sealed class UpdateProgressWindow : Window, IProgress<UpdateProgress>
{
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progressBar;

    public UpdateProgressWindow(Window? owner)
    {
        Title = "Uppdatering av Twitch Overlay Helper";
        if (owner is not null)
            Owner = owner;
        Width = 440;
        Height = 150;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1B, 0x27));
        Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF3, 0xFF));

        _statusText = new TextBlock
        {
            Text = "Förbereder uppdateringen…",
            Margin = new Thickness(20, 18, 20, 12),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _progressBar = new ProgressBar
        {
            Height = 10,
            Margin = new Thickness(20, 0, 20, 20),
            IsIndeterminate = true,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA9, 0x70, 0xFF))
        };
        Content = new StackPanel { Children = { _statusText, _progressBar } };
    }

    public void Report(UpdateProgress value)
    {
        _statusText.Text = value.Status;
        _progressBar.IsIndeterminate = value.Percentage is null;
        if (value.Percentage is double percentage)
            _progressBar.Value = percentage;
    }
}
