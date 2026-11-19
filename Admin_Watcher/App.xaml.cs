using System;
using System.Threading;
using System.Windows;

namespace RASD_AdminWatcher
{
    public partial class App : Application
    {
        // Held for the lifetime of the process: only one relay may run. A second relay
        // would bind the same ports and stop the first relay's Node.js API.
        private const string SINGLE_INSTANCE_MUTEX = @"Global\RASD_Relay_SingleInstance";
        private static Mutex _singleInstance;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // When launched by RASD.exe launcher, skip preflight splash and open directly
            bool noSplash = Array.IndexOf(e.Args, "--no-splash") >= 0;

            bool isFirstInstance;
            _singleInstance = new Mutex(true, SINGLE_INSTANCE_MUTEX, out isFirstInstance);
            if (!isFirstInstance)
            {
                ErrorLogger.Warn("Second relay instance refused — RASD Relay is already running.");
                if (!noSplash)
                {
                    MessageBox.Show(
                        "RASD Relay is already running.\n\nOpen it from its icon in the notification area (system tray).",
                        "RASD Relay", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                Shutdown();
                return;
            }
            if (noSplash)
            {
                // API already confirmed online by the launcher
                var main = new MainWindow(apiWasOnline: true);
                main.Show();
            }
            else
            {
                var splash = new SplashWindow();
                splash.Show();
            }
        }
    }
}
