using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using TwitchOverlayHelper.Diagnostics;
using TwitchOverlayHelper.Updates;

namespace TwitchOverlayHelper;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\TwitchOverlayHelper.SingleInstance";
    private const string ActivateExistingInstanceEventName = @"Local\TwitchOverlayHelper.ActivateExistingInstance";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateExistingInstanceEvent;
    private RegisteredWaitHandle? _activationWaitHandle;
    private bool _ownsSingleInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Both update modes run in a copy of the exe in a temp folder, and both must return before the
        // single-instance mutex below is touched: the app they are waiting for still owns it.
        if (UpdateInstaller.IsCleanupMode(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            await UpdateInstaller.RunCleanupAsync(e.Args);
            Shutdown();
            return;
        }

        if (UpdateInstaller.IsUpdateMode(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            await UpdateInstaller.RunAsync(e.Args);
            Shutdown();
            return;
        }

        // Set when this process is the freshly installed build: the updater's temp folder is still on disk.
        UpdateInstaller.ScheduleCleanup(e.Args);

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
        {
            CheckForUpdatesWhenShown(mainWindow);
            mainWindow.StartHiddenInTray();
        }
        else
        {
            MainWindow.Show();
            _ = CheckForUpdatesAfterStartupAsync(MainWindow);
        }
    }

    /// <summary>
    /// The automatic check, held back until the app has settled. Connecting to chat is what the first
    /// seconds after launch are for, and an update dialog on top of that is in the way.
    /// </summary>
    private static async Task CheckForUpdatesAfterStartupAsync(Window owner)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (owner.Dispatcher.HasShutdownStarted)
            return;

        // Closed to the tray in the meantime. A dialog behind a hidden window is one nobody can answer.
        if (!owner.IsVisible)
        {
            CheckForUpdatesWhenShown(owner);
            return;
        }

        await UpdateCoordinator.CheckAsync(owner, manual: false);
    }

    private static void CheckForUpdatesWhenShown(Window owner)
    {
        DependencyPropertyChangedEventHandler? visibilityChanged = null;
        visibilityChanged = (_, _) =>
        {
            if (!owner.IsVisible)
                return;

            owner.IsVisibleChanged -= visibilityChanged;
            _ = CheckForUpdatesAfterStartupAsync(owner);
        };
        owner.IsVisibleChanged += visibilityChanged;
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
