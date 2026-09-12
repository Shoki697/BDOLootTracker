using System.Threading;
using System.Windows;
using System.Windows.Threading;
using BDOLootTracker.Services;
using Velopack;

namespace BDOLootTracker;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\BDOLootTracker.SingleInstance";
    private const string ActivationEventName = @"Local\BDOLootTracker.Activate";

    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activationEvent;
    private static RegisteredWaitHandle? _activationRegistration;
    private static App? _runningApp;

    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack must remain the first bootstrap operation. The first-run hook
        // only records that the prerequisite prompt is needed; the themed dialog
        // itself is shown after WPF has loaded App.xaml resources.
        bool promptForNpcapAfterWpfInit = false;

        VelopackApp.Build()
            .OnFirstRun(_ => promptForNpcapAfterWpfInit = true)
            .Run();

        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: SingleInstanceMutexName,
            createdNew: out bool isFirstInstance);

        if (!isFirstInstance)
        {
            SignalRunningInstance();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return;
        }

        _activationEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: ActivationEventName);

        var app = new App();
        _runningApp = app;
        app.InitializeComponent();

        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            static (_, _) =>
            {
                App? current = _runningApp;
                if (current == null)
                    return;

                current.Dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    new Action(current.RestoreMainWindowFromExternalLaunch));
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        app.Exit += (_, _) => DisposeSingleInstanceResources();

        if (promptForNpcapAfterWpfInit)
        {
            // Queue the themed prerequisite dialog instead of opening a temporary
            // window before Application.Run(). This lets StartupUri create the main
            // window first and avoids WPF treating the first-run prompt as the last
            // application window when it closes.
            app.Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => NpcapPrerequisiteService.PromptIfMissing()));
        }

        app.Run();
    }

    private static void SignalRunningInstance()
    {
        // The first process creates the activation event immediately after the
        // mutex. A very fast double-click can race that small window, so retry for
        // a short moment before giving up.
        for (int i = 0; i < 20; i++)
        {
            try
            {
                using EventWaitHandle existing = EventWaitHandle.OpenExisting(ActivationEventName);
                existing.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private void RestoreMainWindowFromExternalLaunch()
    {
        if (this.MainWindow is BDOLootTracker.MainWindow mainWindow)
        {
            mainWindow.RestoreFromExternalLaunch();
            return;
        }

        // StartupUri can still be constructing the window if the user double-clicks
        // the shortcut twice very quickly. Keep the request alive until the main
        // window exists instead of launching a second tracker instance.
        var retry = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };

        int attempts = 0;
        retry.Tick += (_, _) =>
        {
            attempts++;
            if (this.MainWindow is BDOLootTracker.MainWindow main)
            {
                retry.Stop();
                main.RestoreFromExternalLaunch();
            }
            else if (attempts >= 30)
            {
                retry.Stop();
            }
        };
        retry.Start();
    }

    private static void DisposeSingleInstanceResources()
    {
        try
        {
            _activationRegistration?.Unregister(null);
        }
        catch
        {
            // Process shutdown cleanup only.
        }

        _activationRegistration = null;
        _activationEvent?.Dispose();
        _activationEvent = null;

        if (_singleInstanceMutex != null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The mutex may already have been released during shutdown.
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        _runningApp = null;
    }
}
