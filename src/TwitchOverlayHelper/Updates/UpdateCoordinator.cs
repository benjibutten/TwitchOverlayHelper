using System.Windows;
using TwitchOverlayHelper.Diagnostics;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace TwitchOverlayHelper.Updates;

/// <summary>
/// The one place that decides what the user sees around an update. An automatic check stays silent
/// unless there is something to install – someone who is live should never be interrupted by a dialog
/// that only says "you are up to date".
/// </summary>
internal static class UpdateCoordinator
{
    private const string Caption = "Twitch Overlay Helper – uppdatering";

    private static readonly GitHubUpdateService Service = new();
    private static int _checkInProgress;

    public static async Task CheckAsync(Window owner, bool manual)
    {
        Version? currentVersion = AppVersion.Current;

        // A local build has no stamped version, so every release looks newer than it. Installing over
        // a working copy of the source tree is not what anyone asked for.
        if (currentVersion is null || currentVersion.Major < 2000)
        {
            if (manual)
                MessageBox.Show(owner, "Uppdateringar går bara att söka efter i släppta versioner.", Caption, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (Interlocked.Exchange(ref _checkInProgress, 1) != 0)
            return;

        try
        {
            UpdateInfo? update = await Service.CheckAsync(currentVersion, manual);
            if (update is null)
            {
                if (manual)
                    MessageBox.Show(owner, "Du har redan den senaste versionen.", Caption, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AppLog.Info($"Uppdatering tillgänglig: {update.TagName} (installerad {AppVersion.DisplayText}).");
            var answer = MessageBox.Show(
                owner,
                $"Twitch Overlay Helper {update.TagName} finns att hämta. Du har {AppVersion.DisplayText}.\n\n"
                    + "Vill du hämta och installera den nu? Appen stängs och startar om automatiskt.\n\n"
                    + "Windows kan be om godkännande eller visa en säkerhetsvarning när den uppdaterade appen startar, "
                    + "särskilt för osignerade eller nyligen släppta bygganden.",
                Caption,
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes)
                return;

            var progressWindow = new UpdateProgressWindow(owner);
            owner.IsEnabled = false;
            progressWindow.Show();
            try
            {
                var progress = new Progress<UpdateProgress>(progressWindow.Report);
                await Service.LaunchInstallerAsync(update, progress);

                // From here the copied updater is waiting for this process to exit before it can touch
                // a single file, so the shutdown has to be the ordinary one that saves settings and chat.
                if (Application.Current.MainWindow is MainWindow mainWindow)
                    mainWindow.ExitForUpdate();
                else
                    Application.Current.Shutdown();
            }
            finally
            {
                progressWindow.Close();
                owner.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Uppdateringskontrollen misslyckades.", ex);
            if (manual)
                MessageBox.Show(owner, $"Kunde inte söka efter eller förbereda uppdateringen.\n\n{ex.Message}", Caption, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Interlocked.Exchange(ref _checkInProgress, 0);
        }
    }
}
