using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;

namespace RASD_Shell
{
    public partial class App : Application
    {
        public const string DashboardUrl = "http://localhost:3000/";

        private void App_Startup(object sender, StartupEventArgs e)
        {
            // Phase 5 recovery path: "--browser" opens the dashboard in the system
            // browser and exits, so the browser route is always reachable.
            if (e.Args.Any(a => a.Equals("--browser", StringComparison.OrdinalIgnoreCase)))
            {
                try { Process.Start(new ProcessStartInfo(DashboardUrl) { UseShellExecute = true }); } catch { }
                Shutdown();
                return;
            }
            new MainWindow().Show();
        }
    }
}
