using System;
using RASD.Shared;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RASD_AdminWatcher
{
    public partial class MainWindow : Window
    {
        // ── Engines ───────────────────────────────────────────────────────
        private readonly NetworkManager  _netManager;
        private readonly Watchdog        _watchdog;
        private readonly RelayEngine     _relay;
        private readonly DispatcherTimer _clockTimer;
        private bool _engineRunning = false;

        // ── UI State ──────────────────────────────────────────────────────
        private readonly AppSettings  _settings = new AppSettings();
        private readonly AlertManager _alerts;
        private bool _isDarkMode   = true;
        private bool _isFullscreen = false;
        private WindowState _preFullscreenState = WindowState.Normal;

        // ── System tray ───────────────────────────────────────────────────
        private System.Windows.Forms.NotifyIcon _trayIcon;
        private bool _exitRequested = false;

        // ── Log / search ─────────────────────────────────────────────────
        private readonly List<LogEntry> _allLogEntries = new List<LogEntry>();
        private int _totalLines = 0;
        private const int LOG_CAP = 500;

        private struct LogEntry
        {
            public string     Time;
            public string     Prefix;
            public SolidColorBrush PrefixColor;
            public string     Message;
            public SolidColorBrush TextColor;
        }

        // ================================================================
        // CONSTRUCTOR
        // ================================================================
        public MainWindow(bool apiWasOnline)
        {
            InitializeComponent();

            _alerts = new AlertManager(AlertContainer, AlertsPanel);

            // Wire engines
            _netManager = new NetworkManager(Log, UpdateIpCard);
            _watchdog   = new Watchdog(Log, UpdateApiCard, _netManager,
                (type, title, msg, hint) => _alerts.Show(type, title, msg, hint));
            _relay      = new RelayEngine(Log, UpdateRelayCard,
                onClientDisconnected: (ip, reason) =>
                {
                    var diagTask = _alerts.DiagnoseClientAsync(ip, reason);
                });

            // Auto-start the engine on window open — no manual "START ENGINE" click required
            StartEngine();

            // Clock
            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (s, e) =>
                TxtClock.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
            _clockTimer.Start();

            // Apply saved theme + settings
            _isDarkMode = _settings.DarkMode;
            ApplyTheme(_isDarkMode);
            BtnTheme.Content = _isDarkMode ? "LIGHT" : "DARK";

            _settings.EnsureStartupDefault();
            ChkStartup.IsChecked = _settings.LaunchOnStartup;
            ChkTray.IsChecked    = _settings.MinimizeToTray;

            // System tray
            InitTray();

            // Initial status
            var initTask = InitialStatusPollAsync(apiWasOnline);

            Log("RASD Operations Ecosystem v2.0 — initialized.");
            Log(string.Format("Admin LAN IP: {0}", NetworkManager.GetLanIp()));

            ErrorLogger.Info("MainWindow opened.");
        }

        // ================================================================
        // INITIAL STATUS POLL
        // ================================================================
        private async System.Threading.Tasks.Task InitialStatusPollAsync(bool apiWasOnline)
        {
            UpdateDbCard(await CheckPortQuickAsync(5432));
            UpdateApiCard(apiWasOnline);
            UpdateIpCard(NetworkManager.GetLanIp());

            if (!apiWasOnline)
            {
                _alerts.Show(AlertType.Warning,
                    "API Server Offline",
                    "The Node.js API did not respond on startup.",
                    "Click START ENGINE — the relay will auto-start Node.js.",
                    retryLabel: "START ENGINE",
                    onRetry: () =>
                    {
                        if (!_engineRunning) BtnStart_Click(this, null);
                    });
            }
        }

        // ================================================================
        // ENGINE BUTTONS
        // ================================================================
        private void BtnStart_Click(object sender, RoutedEventArgs e) => StartEngine();

        private void StartEngine()
        {
            if (_engineRunning) return;
            _engineRunning     = true;
            BtnStart.IsEnabled = false;
            BtnStop.IsEnabled  = true;

            _netManager.Start();
            _watchdog.Start();
            _relay.Start();

            Log("═══ ENGINE STARTED ═══  Watchdog · Network Monitor · Relay active.");
            TxtStatusBar.Text = "Engine running.";
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e) => StopEngine();

        private void BtnRestartApi_Click(object sender, RoutedEventArgs e)
        {
            Log("Manual API restart requested...");
            _netManager.RestartNodeApi();
        }

        private void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            _netManager.StartSubnetScan();
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            TxtLog.Inlines.Clear();
            _allLogEntries.Clear();
            _totalLines = 0;
            TxtLogCount.Text = "0 lines";
        }

        // ================================================================
        // THEME TOGGLE
        // ================================================================
        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            _isDarkMode = !_isDarkMode;
            ApplyTheme(_isDarkMode);
            BtnTheme.Content      = _isDarkMode ? "LIGHT" : "DARK";
            _settings.DarkMode    = _isDarkMode;
        }

        private void ApplyTheme(bool dark)
        {
            var res = Application.Current.Resources;
            if (dark)
            {
                res["BrushVoid"]    = new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x08));
                res["BrushSurface"] = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11));
                res["BrushPanel"]   = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18));
                res["BrushBorder"]  = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));
                res["BrushMuted"]   = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
                res["BrushDim"]     = new SolidColorBrush(Color.FromRgb(0x73, 0x73, 0x73));
                res["BrushGhost"]   = new SolidColorBrush(Color.FromRgb(0xA3, 0xA3, 0xA3));
                res["BrushWhite"]   = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
            }
            else
            {
                res["BrushVoid"]    = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                res["BrushSurface"] = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6));
                res["BrushPanel"]   = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
                res["BrushBorder"]  = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB));
                res["BrushMuted"]   = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
                res["BrushDim"]     = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));
                res["BrushGhost"]   = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37));
                res["BrushWhite"]   = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
            }
        }

        // ================================================================
        // FULLSCREEN TOGGLE
        // ================================================================
        private void BtnFullscreen_Click(object sender, RoutedEventArgs e)
        {
            if (!_isFullscreen)
            {
                _preFullscreenState   = WindowState;
                WindowState           = WindowState.Maximized;
                BtnFullscreen.Content = "[X]";
                _isFullscreen         = true;
            }
            else
            {
                WindowState           = _preFullscreenState;
                BtnFullscreen.Content = "[ ]";
                _isFullscreen         = false;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.F11) BtnFullscreen_Click(this, null);
            if (e.Key == Key.Escape && _isFullscreen) BtnFullscreen_Click(this, null);
        }

        // ================================================================
        // SEARCH
        // ================================================================
        private void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            bool visible = SearchPanel.Visibility == Visibility.Visible;
            SearchPanel.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            if (!visible)
            {
                TxtSearch.Focus();
            }
            else
            {
                TxtSearch.Text = "";
                RebuildLog(null);
            }
        }

        private void TxtSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
            => RebuildLog(TxtSearch.Text);

        private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) BtnSearch_Click(this, null);
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearch.Text = "";
            TxtSearch.Focus();
        }

        // Rebuild log display with optional filter
        private void RebuildLog(string filter)
        {
            TxtLog.Inlines.Clear();
            bool hasFilter = !string.IsNullOrEmpty(filter);
            int shown = 0;
            foreach (var entry in _allLogEntries)
            {
                if (hasFilter &&
                    entry.Message.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                AppendLogEntry(entry);
                shown++;
            }

            TxtLogCount.Text = hasFilter
                ? string.Format("{0}/{1} lines", shown, _totalLines)
                : string.Format("{0} lines", _totalLines);

            LogScroll.ScrollToEnd();
        }

        // ================================================================
        // SYSTEM TRAY
        // ================================================================
        private void InitTray()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Visible = true,
                Text    = "RASD Operations Ecosystem",
                Icon    = LoadTrayIcon(),
            };

            // Context menu
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open RASD",    null, (s, e) => RestoreWindow());
            menu.Items.Add("Restart API",  null, (s, e) => Dispatcher.Invoke(() => _netManager.RestartNodeApi()));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit",         null, (s, e) => Dispatcher.Invoke(ExitApp));
            _trayIcon.ContextMenuStrip = menu;

            // Single-click → restore
            _trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left)
                    Dispatcher.Invoke(RestoreWindow);
            };
        }

        private static System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "assets", "rasd.ico");
                if (File.Exists(path)) return new System.Drawing.Icon(path);
            }
            catch { }
            return System.Drawing.SystemIcons.Application;
        }

        // Phase 4 B1: open the dashboard in the native WebView2 shell (additive —
        // the browser path still works; falls back to the browser if the shell
        // exe is not found).
        private void BtnOpenDashboard_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string[] candidates =
                {
                    System.IO.Path.Combine(dir, "RASD_Shell.exe"),
                    System.IO.Path.Combine(dir, "..", "RASD_Shell.exe"),
                    @"C:\Program Files\RASD\RASD_Shell.exe",
                };
                foreach (var exe in candidates)
                {
                    if (System.IO.File.Exists(exe))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
                        return;
                    }
                }
                // Fallback: browser
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("http://localhost:3000/") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log("ERROR: Could not open the dashboard shell — " + ex.Message);
            }
        }

        private void BtnTray_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            _trayIcon.ShowBalloonTip(2000, "RASD", "Running in the background. Click the tray icon to reopen.",
                System.Windows.Forms.ToolTipIcon.Info);
        }

        private void RestoreWindow()
        {
            Show();
            WindowState = _isFullscreen ? WindowState.Maximized : WindowState.Normal;
            Activate();
        }

        private void ExitApp()
        {
            _exitRequested = true;
            Close();
        }

        // ================================================================
        // SETTINGS CHECKBOXES
        // ================================================================
        private void ChkStartup_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkStartup.IsLoaded)
                _settings.LaunchOnStartup = ChkStartup.IsChecked == true;
        }

        private void ChkTray_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkTray.IsLoaded)
                _settings.MinimizeToTray = ChkTray.IsChecked == true;
        }

        // ================================================================
        // WINDOW EVENTS
        // ================================================================
        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            if (_settings.MinimizeToTray)
                BtnTray_Click(sender, e);
            else
                WindowState = WindowState.Minimized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (_settings.MinimizeToTray && !_exitRequested)
                BtnTray_Click(sender, e);
            else
                ExitApp();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_settings.MinimizeToTray && !_exitRequested)
            {
                e.Cancel = true; // Don't actually close — hide to tray
                Hide();
                return;
            }

            StopEngine();
            _clockTimer.Stop();
            if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && _settings.MinimizeToTray)
                Hide();
        }

        private void StopEngine()
        {
            if (!_engineRunning) return;
            _engineRunning     = false;
            _netManager.Stop();
            _watchdog.Stop();
            _relay.Stop();
            BtnStart.IsEnabled = true;
            BtnStop.IsEnabled  = false;
            Log("═══ ENGINE STOPPED ═══");
            TxtStatusBar.Text = "Engine stopped.";
        }

        // ================================================================
        // LOGGING — thread-safe, color-coded, filterable
        // ================================================================
        public void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                string time = DateTime.Now.ToString("HH:mm:ss");

                string     prefix     = "  ";
                SolidColorBrush pColor = Brush(0x40, 0x40, 0x40);
                SolidColorBrush mColor = Brush(0xA3, 0xA3, 0xA3);

                string upper = message.ToUpperInvariant();
                if (upper.Contains("ERROR") || upper.Contains("FAIL") || upper.Contains("CRASH"))
                {
                    pColor = Brush(0xEF, 0x44, 0x44);
                    mColor = Brush(0xFC, 0xA5, 0xA5);
                    prefix = "✗ ";
                    ErrorLogger.Error(message);
                }
                else if (upper.Contains("WARN") || upper.Contains("RESTART") || upper.Contains("CHANGE"))
                {
                    pColor = Brush(0xEA, 0xB3, 0x08);
                    mColor = Brush(0xFD, 0xE0, 0x68);
                    prefix = "⚠ ";
                    ErrorLogger.Warn(message);
                }
                else if (upper.Contains("OK") || upper.Contains("ONLINE") || upper.Contains("CONNECT")
                      || upper.Contains("SUCCESS") || upper.Contains("STARTED"))
                {
                    pColor = Brush(0x22, 0xC5, 0x5E);
                    mColor = Brush(0x86, 0xEF, 0xAC);
                    prefix = "✓ ";
                }
                else if (message.StartsWith("═"))
                {
                    pColor = (SolidColorBrush)FindResource("BrushAccent");
                    mColor = pColor;
                    prefix = "";
                }

                var entry = new LogEntry
                {
                    Time        = time,
                    Prefix      = prefix,
                    PrefixColor = pColor,
                    Message     = message,
                    TextColor   = mColor,
                };

                // Store (cap at LOG_CAP)
                _allLogEntries.Add(entry);
                if (_allLogEntries.Count > LOG_CAP)
                    _allLogEntries.RemoveAt(0);

                _totalLines++;

                // Only show if no active search filter, or if entry matches
                string filter = SearchPanel.Visibility == Visibility.Visible ? TxtSearch.Text : null;
                bool passes   = string.IsNullOrEmpty(filter)
                    || message.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

                if (passes)
                {
                    AppendLogEntry(entry);
                    LogScroll.ScrollToEnd();

                    // Cap visible lines (prune first inline span)
                    if (TxtLog.Inlines.Count > LOG_CAP)
                        TxtLog.Inlines.Remove(TxtLog.Inlines.FirstInline);
                }

                TxtLogCount.Text = string.IsNullOrEmpty(filter)
                    ? string.Format("{0} lines", _totalLines)
                    : string.Format("{0}/{1} lines",
                        _allLogEntries.FindAll(en =>
                            en.Message.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).Count,
                        _totalLines);
            });
        }

        private void AppendLogEntry(LogEntry e)
        {
            var span = new Span();
            span.Inlines.Add(new Run(string.Format("[{0}] ", e.Time))
                { Foreground = (SolidColorBrush)FindResource("BrushMuted") });
            span.Inlines.Add(new Run(e.Prefix)  { Foreground = e.PrefixColor });
            span.Inlines.Add(new Run(e.Message + "\n") { Foreground = e.TextColor });
            TxtLog.Inlines.Add(span);
        }

        private static SolidColorBrush Brush(byte r, byte g, byte b)
            => new SolidColorBrush(Color.FromRgb(r, g, b));

        // ================================================================
        // STATUS CARD UPDATERS (called from background threads)
        // ================================================================
        public void UpdateDbCard(bool online)
        {
            Dispatcher.Invoke(() =>
            {
                DotDb.Fill       = online ? (SolidColorBrush)FindResource("BrushOnline")
                                          : (SolidColorBrush)FindResource("BrushOffline");
                LblDb.Text       = online ? "ONLINE" : "OFFLINE";
                LblDb.Foreground = DotDb.Fill;
            });
        }

        public void UpdateApiCard(bool online)
        {
            Dispatcher.Invoke(() =>
            {
                DotApi.Fill       = online ? (SolidColorBrush)FindResource("BrushOnline")
                                           : (SolidColorBrush)FindResource("BrushOffline");
                LblApi.Text       = online ? "ONLINE" : "OFFLINE";
                LblApi.Foreground = DotApi.Fill;
            });
        }

        public void UpdateIpCard(string ip)
        {
            Dispatcher.Invoke(() =>
            {
                bool valid       = !string.IsNullOrEmpty(ip) && ip != "127.0.0.1";
                DotIp.Fill       = valid ? (SolidColorBrush)FindResource("BrushOnline")
                                         : (SolidColorBrush)FindResource("BrushWarn");
                LblIp.Text       = valid ? ip : "NO LAN";
                LblIp.Foreground = DotIp.Fill;

                UpdateWiredBanner();
            });
        }

        /// <summary>
        /// Phase 3: warn the operator when no wired Ethernet adapter is up, since
        /// agents are refused over Wi-Fi and would otherwise appear "stuck" with no
        /// visible cause. Suppressed when allow_wifi.flag is in effect.
        /// </summary>
        private void UpdateWiredBanner()
        {
            bool missing  = _netManager != null && _netManager.WiredLanMissing;
            bool override_ = NetworkPolicy.WifiOverrideActive();

            if (missing && !override_)
            {
                WiredWarnTitle.Text = "NO WIRED LAN — AGENTS CANNOT CONNECT";
                WiredWarnBody.Text  = "RASD only serves endpoints over a wired Ethernet connection. " +
                                      "Connect the admin machine to the LAN with an Ethernet cable.";
                WiredWarnBanner.Visibility = Visibility.Visible;
            }
            else if (override_)
            {
                WiredWarnTitle.Text = "WI-FI OVERRIDE ACTIVE — WIRED-ONLY ENFORCEMENT RELAXED";
                WiredWarnBody.Text  = "allow_wifi.flag is present, so agents may connect over non-Ethernet " +
                                      "links. Remove C:\\ProgramData\\RASD\\allow_wifi.flag to restore enforcement.";
                WiredWarnBanner.Visibility = Visibility.Visible;
            }
            else
            {
                WiredWarnBanner.Visibility = Visibility.Collapsed;
            }
        }

        public void UpdateRelayCard(string status, bool active)
        {
            Dispatcher.Invoke(() =>
            {
                DotRelay.Fill       = active ? (SolidColorBrush)FindResource("BrushOnline")
                                             : (SolidColorBrush)FindResource("BrushMuted");
                LblRelay.Text       = status;
                LblRelay.Foreground = DotRelay.Fill;
            });
        }

        public void UpdateWatchdogLabel(string text)
            => Dispatcher.Invoke(() => TxtWatchdog.Text = "Watchdog: " + text);

        // ================================================================
        // QUICK PORT CHECK
        // ================================================================
        private static async System.Threading.Tasks.Task<bool> CheckPortQuickAsync(int port)
        {
            try
            {
                using (var tcp = new System.Net.Sockets.TcpClient())
                {
                    var t = tcp.ConnectAsync("127.0.0.1", port);
                    return await System.Threading.Tasks.Task.WhenAny(
                               t, System.Threading.Tasks.Task.Delay(2000)) == t
                           && tcp.Connected;
                }
            }
            catch { return false; }
        }
    }
}
