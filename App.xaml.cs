using System.Windows;
using System.Windows.Threading;
using BDOLootTracker.Services;
using Velopack;

namespace BDOLootTracker;

public partial class App : Application
{
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

        var app = new App();
        app.InitializeComponent();

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
}
