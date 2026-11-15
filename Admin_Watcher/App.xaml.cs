using System;
using System.Windows;

namespace RASD_AdminWatcher
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // When launched by RASD.exe launcher, skip preflight splash and open directly
            bool noSplash = Array.IndexOf(e.Args, "--no-splash") >= 0;
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
