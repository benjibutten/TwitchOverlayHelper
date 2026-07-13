using System.Windows;

namespace TwitchOverlayHelper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
