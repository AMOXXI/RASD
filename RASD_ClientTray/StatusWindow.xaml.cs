using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing  = System.Drawing;
using WinForms = System.Windows.Forms;

namespace RASD_ClientTray
{
    /// <summary>
    /// Read-only endpoint status window plus its notification-area icon.
    /// Polls the connector's status file; never issues admin commands.
    /// </summary>
    public partial class StatusWindow : Window
    {
        private const int POLL_MS = 2000;

        private WinForms.NotifyIcon  _tray;
        private DispatcherTimer      _timer;
        private string               _lastTrayState = "";

        public StatusWindow()
        {
            InitializeComponent();
            BuildTrayIcon();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(POLL_MS) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();

            Refresh();
        }

        // ----------------------------------------------------------------
        // TRAY ICON
        // ----------------------------------------------------------------
        private void BuildTrayIcon()
        {
            _tray = new WinForms.NotifyIcon
            {
                Icon    = LoadAppIcon(),
                Text    = "RASD Endpoint",
                Visible = true,
            };

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Show status", null, (s, e) => ShowStatusWindow());
            menu.Items.Add("Reconnect now", null, (s, e) => DoReconnect());
            menu.Items.Add("Open connector log", null, (s, e) => OpenLog());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Hide", null, (s, e) => HideToTray());
            _tray.ContextMenuStrip = menu;

            _tray.DoubleClick += (s, e) => ShowStatusWindow();
        }

        private static Drawing.Icon LoadAppIcon()
        {
            // Prefer the icon embedded in this executable so the tray matches the
            // installed branding without depending on a loose .ico on disk.
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                var extracted = Drawing.Icon.ExtractAssociatedIcon(exe);
                if (extracted != null) return extracted;
            }
            catch { }
            return Drawing.SystemIcons.Shield;
        }

        public void ShowStatusWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Refresh();
        }

        private void HideToTray()
        {
            Hide();
        }

        // ----------------------------------------------------------------
        // REFRESH
        // ----------------------------------------------------------------
        private void Refresh()
        {
            var s = ConnectorStatus.Load();

            TxtStatus.Text    = s.State;
            TxtAdminIp.Text   = string.IsNullOrEmpty(s.AdminIp)       ? "not discovered yet" : s.AdminIp;
            TxtHeartbeat.Text = string.IsNullOrEmpty(s.LastHeartbeat) ? "no heartbeat yet"   : s.LastHeartbeat;
            TxtHostname.Text  = s.Hostname;

            // Phase 3 — adapter + wired-only state
            if (s.WiredBlocked)
                TxtAdapter.Text = "none — Ethernet cable required";
            else if (!string.IsNullOrEmpty(s.Adapter))
                TxtAdapter.Text = s.Adapter + (string.IsNullOrEmpty(s.AdapterType) ? "" : " [" + s.AdapterType + "]");
            else
                TxtAdapter.Text = "—";

            if (s.WiredBlocked && !s.WifiOverride)
            {
                TxtWiredTitle.Text = "WIRED CONNECTION REQUIRED";
                TxtWiredBody.Text  = "RASD only operates over a wired Ethernet connection. Please connect " +
                                     "this machine to the network with an Ethernet cable.";
                WiredNotice.Visibility = System.Windows.Visibility.Visible;
            }
            else if (s.WifiOverride)
            {
                TxtWiredTitle.Text = "WI-FI OVERRIDE ACTIVE";
                TxtWiredBody.Text  = "Wired-only enforcement is relaxed on this machine (allow_wifi.flag). " +
                                     "This is intended for demos only.";
                WiredNotice.Visibility = System.Windows.Visibility.Visible;
            }
            else
            {
                WiredNotice.Visibility = System.Windows.Visibility.Collapsed;
            }

            var color = ColorFor(s.State);
            DotStatus.Background = new SolidColorBrush(color);
            DotTitle.Background  = new SolidColorBrush(color);

            BtnLog.IsEnabled = ConnectorStatus.LogExists();

            if (_lastTrayState != s.State)
            {
                _lastTrayState = s.State;
                string tip = "RASD Endpoint — " + s.State;
                if (!string.IsNullOrEmpty(s.AdminIp)) tip += " (" + s.AdminIp + ")";
                // NotifyIcon.Text throws above 63 chars on some Windows builds.
                _tray.Text = tip.Length > 62 ? tip.Substring(0, 62) : tip;
            }
        }

        private static Color ColorFor(string state)
        {
            if (string.IsNullOrEmpty(state)) return Colors.Gray;
            string s = state.ToLowerInvariant();

            if (s.StartsWith("connected"))   return (Color)ColorConverter.ConvertFromString("#10B981");
            if (s.StartsWith("searching"))   return (Color)ColorConverter.ConvertFromString("#F59E0B");
            if (s.StartsWith("starting"))    return (Color)ColorConverter.ConvertFromString("#F59E0B");
            if (s.StartsWith("no lan"))      return (Color)ColorConverter.ConvertFromString("#F59E0B");
            return (Color)ColorConverter.ConvertFromString("#EF4444");
        }

        // ----------------------------------------------------------------
        // ACTIONS
        // ----------------------------------------------------------------
        private void DoReconnect()
        {
            string error;
            if (ConnectorStatus.RequestReconnect(out error))
            {
                TxtStatus.Text = "Reconnect requested...";
                DotStatus.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#F59E0B"));
            }
            else
            {
                MessageBox.Show(
                    "Could not request a reconnect:\n\n" + error,
                    "RASD Endpoint Status", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OpenLog()
        {
            try
            {
                if (!ConnectorStatus.LogExists())
                {
                    MessageBox.Show(
                        "The connector log does not exist yet:\n\n" + ConnectorStatus.LOG_FILE +
                        "\n\nThis usually means the RASD connector service has not started.",
                        "RASD Endpoint Status", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                Process.Start(new ProcessStartInfo("notepad.exe", ConnectorStatus.LOG_FILE)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the log:\n\n" + ex.Message,
                    "RASD Endpoint Status", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ----------------------------------------------------------------
        // WINDOW CHROME
        // ----------------------------------------------------------------
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void BtnHide_Click(object sender, RoutedEventArgs e)    { HideToTray(); }
        private void BtnReconnect_Click(object sender, RoutedEventArgs e) { DoReconnect(); }
        private void BtnLog_Click(object sender, RoutedEventArgs e)      { OpenLog(); }

        // Closing the window only hides it — the tray icon keeps running.
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            HideToTray();
        }
    }
}
