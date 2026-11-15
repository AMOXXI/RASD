using System;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace RASD_AdminWatcher
{
    public partial class SplashWindow : Window
    {
        // Sentinel for "no API, but allow bypass" scenario
        private bool _apiOptional = false;

        public SplashWindow()
        {
            InitializeComponent();
            Loaded += async (s, e) => await RunPreflightAsync();
        }

        // Allow window drag on borderless window
        private void Border_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                DragMove();
        }

        private async Task RunPreflightAsync()
        {
            BtnRetry.Visibility = Visibility.Collapsed;
            PbSplash.IsIndeterminate = true;

            // ---- STEP 1: PostgreSQL :5432 ----
            SetStep(DotPg, LblPg, StepPg, "CHECKING...", null);
            await Task.Delay(600);
            bool pgOk = await CheckPortAsync("127.0.0.1", 5432, 3000);
            SetStep(DotPg, LblPg, StepPg, pgOk ? "ONLINE" : "OFFLINE", pgOk);

            // ---- STEP 2: Node API :3000 ----
            SetStep(DotApi, LblApi, StepApi, "CHECKING...", null);
            await Task.Delay(500);
            bool apiOk = await CheckHttpAsync("http://localhost:3000/api/health", 4000);
            SetStep(DotApi, LblApi, StepApi, apiOk ? "ONLINE" : "OFFLINE", apiOk);

            // ---- STEP 3: LAN IP ----
            SetStep(DotIp, LblIp, StepIp, "DETECTING...", null);
            await Task.Delay(400);
            string lanIp = NetworkManager.GetLanIp();
            bool hasIp = !string.IsNullOrEmpty(lanIp) && lanIp != "127.0.0.1";
            SetStep(DotIp, LblIp, StepIp, hasIp ? lanIp : "NO LAN", hasIp);

            PbSplash.IsIndeterminate = false;
            PbSplash.Value = 100;

            // ---- EVALUATE ----
            if (!pgOk)
            {
                ShowError("PostgreSQL is not running on port 5432.\n\nStart the 'postgresql-x64-16' Windows service and retry.");
                return;
            }

            if (!apiOk)
            {
                // API is offline — offer bypass: Admin Watcher can start the API itself
                TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEA, 0xB3, 0x08));
                TxtStatus.Text = "API Server offline. Admin Watcher will start it automatically.";
                _apiOptional = true;
                await Task.Delay(1500);
            }
            else
            {
                TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                TxtStatus.Text = "All systems nominal. Launching Operations Center...";
                await Task.Delay(1200);
            }

            // Open main window
            OpenMain(apiOk);
        }

        private void OpenMain(bool apiWasOnline)
        {
            var main = new MainWindow(apiWasOnline);
            Application.Current.MainWindow = main;
            main.Show();
            Close();
        }

        private void ShowError(string msg)
        {
            TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            TxtStatus.Text = msg;
            BtnRetry.Visibility = Visibility.Visible;
            PbSplash.IsIndeterminate = false;
        }

        private async void BtnRetry_Click(object sender, RoutedEventArgs e)
        {
            // Reset dots
            ResetDot(DotPg, LblPg, StepPg);
            ResetDot(DotApi, LblApi, StepApi);
            ResetDot(DotIp, LblIp, StepIp);
            TxtStatus.Text = "Retrying...";
            TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x73, 0x73, 0x73));
            await RunPreflightAsync();
        }

        // ================================================================
        // HELPERS
        // ================================================================
        private void SetStep(System.Windows.Shapes.Ellipse dot, System.Windows.Controls.TextBlock lbl,
                             System.Windows.Controls.Border card, string labelText, bool? success)
        {
            Dispatcher.Invoke(() =>
            {
                lbl.Text = labelText;
                if (success == true)
                {
                    dot.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                    lbl.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                    card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x22, 0xC5, 0x5E));
                }
                else if (success == false)
                {
                    dot.Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                    lbl.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                    card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xEF, 0x44, 0x44));
                }
                else
                {
                    dot.Fill = new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16));
                    lbl.Foreground = new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16));
                }
            });
        }

        private void ResetDot(System.Windows.Shapes.Ellipse dot, System.Windows.Controls.TextBlock lbl,
                               System.Windows.Controls.Border card)
        {
            var mutedBrush  = (SolidColorBrush)FindResource("BrushMuted");
            var borderBrush = (SolidColorBrush)FindResource("BrushBorder");
            dot.Fill = mutedBrush;
            lbl.Foreground = mutedBrush;
            lbl.Text = "WAITING...";
            card.BorderBrush = borderBrush;
        }

        private static async Task<bool> CheckPortAsync(string host, int port, int timeoutMs)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connectTask = client.ConnectAsync(host, port);
                    return await Task.WhenAny(connectTask, Task.Delay(timeoutMs)) == connectTask
                           && client.Connected;
                }
            }
            catch { return false; }
        }

        private static async Task<bool> CheckHttpAsync(string url, int timeoutMs)
        {
            try
            {
                using (var http = new System.Net.Http.HttpClient())
                {
                    http.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
                    var resp = await http.GetAsync(url);
                    return resp.IsSuccessStatusCode;
                }
            }
            catch { return false; }
        }
    }
}
