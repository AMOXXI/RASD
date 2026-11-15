using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace RASD_Shell
{
    /// <summary>
    /// Frameless WebView2 shell for the local RASD dashboard.
    /// Additive to the browser path (Phase 4 B1) — locks navigation to localhost,
    /// disables devtools/context-menu/accelerators, and never shows a blank window
    /// when the WebView2 runtime is missing.
    /// </summary>
    public partial class MainWindow : Window
    {
        // The dashboard is served locally by the Node API. Only these origins load.
        private const string DASHBOARD_URL = "http://localhost:3000/";
        private const string RUNTIME_DL    = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"; // Evergreen bootstrapper
        private const string BOOTSTRAPPER  = "MicrosoftEdgeWebview2Setup.exe";

        public MainWindow()
        {
            InitializeComponent();
            Loaded += async (_, __) => await InitAsync();
        }

        private async Task InitAsync()
        {
            // Fail visibly, never blank, if the runtime is absent.
            try
            {
                string ver = CoreWebView2Environment.GetAvailableBrowserVersionString();
                if (string.IsNullOrEmpty(ver)) { ShowRuntimeMissing(null); return; }
            }
            catch (Exception ex)
            {
                ShowRuntimeMissing(ex.Message);
                return;
            }

            try
            {
                // Keep the user-data folder out of Program Files (write-protected).
                string udf = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RASD", "WebView2");
                Directory.CreateDirectory(udf);

                var env = await CoreWebView2Environment.CreateAsync(null, udf);
                await Web.EnsureCoreWebView2Async(env);
                HardenWebView(Web.CoreWebView2);
                Web.Source = new Uri(DASHBOARD_URL);
            }
            catch (Exception ex)
            {
                ShowRuntimeMissing(ex.Message);
            }
        }

        /// <summary>Security lock-down for a monitoring dashboard.</summary>
        private void HardenWebView(CoreWebView2 core)
        {
            var s = core.Settings;
            s.AreDevToolsEnabled              = false;
            s.AreDefaultContextMenusEnabled   = false;   // no right-click menu
            s.AreBrowserAcceleratorKeysEnabled = false;  // no F12 / Ctrl+Shift+I / Ctrl+P etc.
            s.IsStatusBarEnabled              = false;
            s.IsZoomControlEnabled            = false;
            s.IsSwipeNavigationEnabled        = false;

            // Block navigation to anything that is not the local dashboard.
            core.NavigationStarting += (_, e) =>
            {
                if (!IsLocalOrigin(e.Uri)) e.Cancel = true;
            };
            // A security dashboard must never spawn arbitrary popup windows.
            core.NewWindowRequested += (_, e) => { e.Handled = true; };
        }

        private static bool IsLocalOrigin(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return false;
            Uri u;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out u)) return false;
            string h = u.Host;
            return h == "localhost" || h == "127.0.0.1" || h == "[::1]";
        }

        // ---- runtime-missing overlay ----
        private void ShowRuntimeMissing(string detail)
        {
            if (!string.IsNullOrEmpty(detail))
                OverlayBody.Text = "The Microsoft Edge WebView2 runtime could not be started.\n\n" + detail +
                                   "\n\nInstall the runtime and reopen RASD Operations.";
            Overlay.Visibility = Visibility.Visible;
        }

        private void Fix_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Prefer the bundled bootstrapper (next to the exe or in assets\); else the web link.
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] local = { Path.Combine(baseDir, BOOTSTRAPPER), Path.Combine(baseDir, "assets", BOOTSTRAPPER) };
                string found = null;
                foreach (var p in local) { if (File.Exists(p)) { found = p; break; } }
                Process.Start(new ProcessStartInfo(found ?? RUNTIME_DL) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not launch the installer:\n\n" + ex.Message,
                    "RASD Operations", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OpenBrowser_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(DASHBOARD_URL) { UseShellExecute = true }); } catch { }
        }

        // ---- window chrome ----
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { ToggleMax(); return; }
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
        private void Reload_Click(object sender, RoutedEventArgs e)
        { try { Web.CoreWebView2?.Reload(); } catch { } }
        private void Browser_Click(object sender, RoutedEventArgs e)
        { try { Process.Start(new ProcessStartInfo(DASHBOARD_URL) { UseShellExecute = true }); } catch { } }
        private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Max_Click(object sender, RoutedEventArgs e) => ToggleMax();
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void ToggleMax()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            BtnMax.Content = WindowState == WindowState.Maximized ? "❐" : "▢";
        }
    }
}
