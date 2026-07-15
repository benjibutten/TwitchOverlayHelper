using System.Threading;
using System.Windows;

namespace TwitchOverlayHelper;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\TwitchOverlayHelper.SingleInstance";
    private const string ActivateExistingInstanceEventName = @"Local\TwitchOverlayHelper.ActivateExistingInstance";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateExistingInstanceEvent;
    private RegisteredWaitHandle? _activationWaitHandle;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool startMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

        // Create the activation event before claiming the mutex. This removes the startup
        // race where a second process can see the mutex before the first can be signalled.
        _activateExistingInstanceEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: ActivateExistingInstanceEventName);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirstInstance);
        _ownsSingleInstanceMutex = isFirstInstance;

        if (!isFirstInstance)
        {
            if (!startMinimized)
                _activateExistingInstanceEvent.Set();

            Shutdown();
            return;
        }

        _activationWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            _activateExistingInstanceEvent,
            static (state, _) =>
            {
                if (state is App app && !app.Dispatcher.HasShutdownStarted)
                    app.Dispatcher.BeginInvoke(app.ActivateMainWindow);
            },
            this,
            Timeout.Infinite,
            executeOnlyOnce: false);

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow = new MainWindow();

        if (startMinimized && MainWindow is MainWindow mainWindow)
            mainWindow.StartHiddenInTray();
        else
            MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationWaitHandle?.Unregister(null);
        _activationWaitHandle = null;

        _activateExistingInstanceEvent?.Dispose();
        _activateExistingInstanceEvent = null;

        if (_ownsSingleInstanceMutex)
            _singleInstanceMutex?.ReleaseMutex();

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        base.OnExit(e);
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is MainWindow mainWindow)
            mainWindow.ShowAndActivate();
    }
}
