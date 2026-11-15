using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace RASD_Launcher
{
    public partial class LoadingWindow : Window
    {
        private const string INSTALL_DIR  = @"C:\Program Files\RASD";
        private const string API_HEALTH   = "http://localhost:3000/api/health";
        private const int    TCP_RELAY    = 3001;

        public LoadingWindow()
        {
            InitializeComponent();
            Loaded += async (s, e) => await RunLaunchSequenceAsync();
        }

        // ================================================================
        // MAIN LAUNCH SEQUENCE
        // ================================================================
        private async Task RunLaunchSequenceAsync()
        {
            await Task.Delay(400);

            // ── Step 1: RASD Connector (Node.js API) ─────────────────────
            // If the API is already up, don't start a second relay/Node instance.
            bool apiAlreadyRunning = await Task.Run(() => IsApiAlreadyRunning());
            // Node can outlive the relay. A healthy API without RASD_Relay means
            // commands would never reach agents, so only skip when both are up.
            bool relayRunning = apiAlreadyRunning && IsRelayRunning();
            if (apiAlreadyRunning && relayRunning)
            {
                SetStatus("API already running — skipping relay launch");
                SetStepDone(1, "Online");
                SetProgress(33);
                await Task.Delay(350);
            }
            else
            {
                // Fire RASD_Relay.exe first — it will start Node.js internally
                LaunchRelayProcess();

                SetStepActive(1, "Starting...");
                SetStatus("Starting RASD Connector (Node.js API)...");
                SetProgress(5);

                if (apiAlreadyRunning)
                {
                    // The relay kills and restarts Node.js when its engine starts
                    // (NetworkManager.Start -> RestartNodeApi), so the old API goes
                    // down first. Wait for that (bounded) before waiting for health,
                    // otherwise the old process would satisfy the check below.
                    SetStatus("API running without relay — starting RASD Relay (restarts Node.js)...");
                    await WaitForApiDownAsync(timeoutSec: 15);
                }

                bool apiOk = await WaitForHttpAsync(API_HEALTH, timeoutSec: 90,
                    onTick: pct => SetProgress(5 + (int)(pct * 27)));

                if (!apiOk)
                {
                    SetStepFailed(1, "Timeout");
                    ShowRetry(
                        "RASD Connector did not start within 90 seconds.\n" +
                        "Check that Node.js is installed and RASD_Relay.exe is present.");
                    return;
                }

                SetStepDone(1, "Online");
                SetProgress(33);
                await Task.Delay(350);
            }

            // ── Step 2: RASD Relay Engine (TCP bridge) ────────────────────
            SetStepActive(2, "Starting...");
            SetStatus("Starting RASD Relay Engine...");

            bool relayOk = await WaitForTcpAsync("127.0.0.1", TCP_RELAY, timeoutSec: 20,
                onTick: pct => SetProgress(33 + (int)(pct * 33)));

            if (relayOk)
            {
                SetStepDone(2, "Running");
            }
            else
            {
                // Non-fatal: relay may be initialising; carry on
                SetStepFailed(2, "Pending");
            }
            SetProgress(66);
            await Task.Delay(350);

            // ── Step 3: RASD Dashboard (browser) ─────────────────────────
            SetStepActive(3, "Opening...");
            SetStatus("Opening RASD Dashboard in browser...");

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = "http://localhost:3000",
                    UseShellExecute = true,
                });
                await Task.Delay(1200);
                SetStepDone(3, "Launched");
                SetProgress(100);
                SetStatus("RASD Operations Ecosystem is running.");
            }
            catch
            {
                SetStepFailed(3, "No browser");
                SetStatus("Browser could not be opened. Navigate to http://localhost:3000");
            }

            // Auto-close launcher after 2s — the relay window is now visible
            await Task.Delay(2000);
            Dispatcher.Invoke(() => Application.Current.Shutdown());
        }

        // ================================================================
        // LAUNCH RASD_RELAY.EXE
        // ================================================================
        private static void LaunchRelayProcess()
        {
            string relayExe = Path.Combine(INSTALL_DIR, "RASD_Relay.exe");
            if (!File.Exists(relayExe)) return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName         = relayExe,
                    Arguments        = "--no-splash",
                    UseShellExecute  = false,
                    CreateNoWindow   = false,
                    WorkingDirectory = INSTALL_DIR,
                };
                Process.Start(psi);
            }
            catch { /* will surface as API timeout in step 1 */ }
        }

        // ================================================================
        // QUICK CHECK — IS THE API ALREADY LISTENING ON :3000?
        // ================================================================
        private static bool IsApiAlreadyRunning()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(API_HEALTH);
                req.Timeout           = 3000;
                req.Method            = "GET";
                req.AllowAutoRedirect = false;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    return true; // any successful response means it's up
                }
            }
            catch (WebException wex)
            {
                // A protocol error (non-2xx) still means something is listening
                return wex.Response != null;
            }
            catch
            {
                return false; // timeout / connection refused / anything else
            }
        }

        // ================================================================
        // IS RASD_RELAY.EXE ALREADY RUNNING?
        // ================================================================
        private static bool IsRelayRunning()
        {
            try
            {
                Process[] procs = Process.GetProcessesByName("RASD_Relay");
                bool running = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                return running;
            }
            catch
            {
                return false;
            }
        }

        // ================================================================
        // POLLING HELPERS
        // ================================================================

        /// <summary>Waits until the local API stops answering, or the timeout expires.</summary>
        private static async Task WaitForApiDownAsync(int timeoutSec)
        {
            var deadline = DateTime.Now.AddSeconds(timeoutSec);
            while (DateTime.Now < deadline)
            {
                if (!await Task.Run(() => IsApiAlreadyRunning())) return;
                await Task.Delay(1000);
            }
        }

        private static async Task<bool> WaitForHttpAsync(
            string url, int timeoutSec, Action<double> onTick)
        {
            var deadline = DateTime.Now.AddSeconds(timeoutSec);
            int elapsed  = 0;
            while (DateTime.Now < deadline)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Timeout        = 2000;
                    req.Method         = "GET";
                    req.AllowAutoRedirect = false;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        if ((int)resp.StatusCode < 400) return true;
                    }
                }
                catch { }

                await Task.Delay(1000);
                elapsed++;
                onTick?.Invoke(Math.Min(1.0, (double)elapsed / timeoutSec));
            }
            return false;
        }

        private static async Task<bool> WaitForTcpAsync(
            string host, int port, int timeoutSec, Action<double> onTick)
        {
            var deadline = DateTime.Now.AddSeconds(timeoutSec);
            int elapsed  = 0;
            while (DateTime.Now < deadline)
            {
                try
                {
                    using (var client = new TcpClient())
                    {
                        var ar = client.BeginConnect(host, port, null, null);
                        if (ar.AsyncWaitHandle.WaitOne(500))
                        {
                            client.EndConnect(ar);
                            return true;
                        }
                    }
                }
                catch { }

                await Task.Delay(1000);
                elapsed++;
                onTick?.Invoke(Math.Min(1.0, (double)elapsed / timeoutSec));
            }
            return false;
        }

        // ================================================================
        // UI HELPERS (all thread-safe via Dispatcher)
        // ================================================================
        private void SetStepActive(int step, string text)
        {
            Dispatcher.Invoke(() =>
            {
                GetDot(step).Fill = new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16));
                GetStatus(step).Text       = text;
                GetStatus(step).Foreground = new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16));
            });
        }

        private void SetStepDone(int step, string text)
        {
            Dispatcher.Invoke(() =>
            {
                GetDot(step).Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                GetStatus(step).Text       = text;
                GetStatus(step).Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
            });
        }

        private void SetStepFailed(int step, string text)
        {
            Dispatcher.Invoke(() =>
            {
                GetDot(step).Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                GetStatus(step).Text       = text;
                GetStatus(step).Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            });
        }

        private void SetProgress(int value)
            => Dispatcher.Invoke(() => PbMain.Value = value);

        private void SetStatus(string msg)
            => Dispatcher.Invoke(() => TxtStatus.Text = msg);

        private void ShowRetry(string errorMsg)
        {
            Dispatcher.Invoke(() =>
            {
                TxtStatus.Text          = errorMsg;
                BtnRetry.Visibility     = Visibility.Visible;
            });
        }

        private System.Windows.Shapes.Ellipse GetDot(int step)
        {
            if (step == 1) return Dot1;
            if (step == 2) return Dot2;
            return Dot3;
        }

        private System.Windows.Controls.TextBlock GetStatus(int step)
        {
            if (step == 1) return Status1;
            if (step == 2) return Status2;
            return Status3;
        }

        // ================================================================
        // RETRY
        // ================================================================
        private async void BtnRetry_Click(object sender, RoutedEventArgs e)
        {
            BtnRetry.Visibility = Visibility.Collapsed;
            ResetAllSteps();
            await RunLaunchSequenceAsync();
        }

        private void ResetAllSteps()
        {
            for (int i = 1; i <= 3; i++)
            {
                Dispatcher.Invoke(() =>
                {
                    GetDot(i).Fill             = new SolidColorBrush(Color.FromRgb(0x2D, 0x37, 0x48));
                    GetStatus(i).Text          = "Waiting";
                    GetStatus(i).Foreground    = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
                });
            }
            SetProgress(0);
        }
    }
}
