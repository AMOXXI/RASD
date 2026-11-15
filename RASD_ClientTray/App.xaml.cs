using System;
using System.Threading;
using System.Windows;

namespace RASD_ClientTray
{
    /// <summary>
    /// Tray-resident status app for endpoint (CLIENT) machines.
    /// Starts hidden in the notification area; the window is opened on demand.
    /// </summary>
    public partial class App : Application
    {
        private static Mutex _single;
        private StatusWindow _window;

        private void App_Startup(object sender, StartupEventArgs e)
        {
            // One tray icon per session — a second launch just exits quietly.
            bool isNew;
            _single = new Mutex(true, @"Local\RASD_ClientTray_SingleInstance", out isNew);
            if (!isNew)
            {
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show("RASD status app error:\n\n" + args.Exception.Message,
                    "RASD Endpoint Status", MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true;
            };

            _window = new StatusWindow();

            // Launched at login: stay in the tray instead of popping a window in
            // the user's face. Any other launch (shortcut) shows the window.
            bool silent = Array.Exists(e.Args,
                a => a.Equals("/tray", StringComparison.OrdinalIgnoreCase)
                  || a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

            if (!silent) _window.ShowStatusWindow();
        }
    }
}
