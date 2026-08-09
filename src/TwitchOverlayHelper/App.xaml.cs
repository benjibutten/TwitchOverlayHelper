using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using TwitchOverlayHelper.Diagnostics;

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
        InstallCrashHandlers();
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
            AppLog.Info("En instans kör redan – den här startades bara för att lyfta fram fönstret.");
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

    /// <summary>
    /// The three ways an exception can end this process without anyone being told, wired to the log
    /// before anything else runs. Until this existed the app simply vanished: no window, no dialog,
    /// no tray icon, nothing on disk – which is indistinguishable from someone having closed it.
    /// </summary>
    private void InstallCrashHandlers()
    {
        AppLog.StartSession();

        // An exception in a UI callback – a click, a timer tick, anything posted with BeginInvoke.
        // Marked handled on purpose: this app's job is to keep running while someone is live, and a
        // stray exception in one callback is not a reason to take the overlay off the stream. It is
        // written down instead, which is the part that was missing.
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Ohanterat fel på UI-tråden (appen fortsätter köra).", args.Exception);
            args.Handled = true;
        };

        // Nothing can stop this one – the process is already on its way down. Logged so the next
        // run's file starts with the reason the previous one ended.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Ohanterat fel – appen avslutas.", args.ExceptionObject as Exception);

        // A Task that failed with nobody awaiting it. Harmless in itself, but it is often the first
        // sign of the thing that kills the app a moment later.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Fel i en task som ingen väntade på.", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info($"Appen avslutas normalt (kod {e.ApplicationExitCode}).");
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
