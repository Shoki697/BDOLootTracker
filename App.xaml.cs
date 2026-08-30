using System.Windows;
using Velopack;

namespace BDOLootTracker;

public partial class App : Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Must be the first application bootstrap call so Velopack can handle
        // install/update/uninstall hooks before WPF creates any windows.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
