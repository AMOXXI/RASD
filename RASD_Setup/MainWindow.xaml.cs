using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace RASD_Setup
{
    public partial class MainWindow : Window
    {
        private const string INSTALL_DIR   = @"C:\Program Files\RASD";
        private const string CONFIG_DIR    = @"C:\ProgramData\RASD";
        private const string INSTALL_LOG   = @"C:\ProgramData\RASD\install.log";

        // Service names registered on endpoint (CLIENT) machines
        private const string SVC_CONNECTOR = "RASD_Enterprise_Connector";
        private const string SVC_PROTECTOR = "RASD_Enterprise_Protector";

        // Scheduled Task that starts the endpoint tray app at any user's logon
        private const string TRAY_TASK_NAME = "RASD Endpoint Tray";

        // Download URLs — official sources
        private const string NODE_INSTALLER_URL = "https://nodejs.org/dist/v20.18.1/node-v20.18.1-x64.msi";
        private const string PG_INSTALLER_URL   = "https://get.enterprisedb.com/postgresql/postgresql-16.4-1-windows-x64.exe";

        private const string PSQL_PATH     = @"C:\Program Files\PostgreSQL\16\bin\psql.exe";
        private const string PSQL_PATH_ALT = @"C:\Program Files\PostgreSQL\15\bin\psql.exe";
        private const string PG_SERVICE    = "postgresql-x64-16";
        private const string PG_DIR        = @"C:\Program Files\PostgreSQL\16";

        

        // Selected deployment role — internal tier code: "ADMIN" or "CLIENT"
        private string _selectedRole = "ADMIN";

        public MainWindow()
        {
            InitializeComponent();
            SelectRole("ADMIN");

            InstallLog("========================================================");
            InstallLog("RASD Setup started. Elevated=" + IsElevated()
                       + "  User=" + Environment.UserName
                       + "  Machine=" + Environment.MachineName);

            // The manifest requests requireAdministrator, so we should already be
            // elevated. If something stripped or bypassed that, fail loudly rather
            // than proceeding into service registration that cannot possibly work.
            if (!IsElevated())
            {
                InstallLog("FATAL: Not elevated. Service registration is impossible.");
                MessageBox.Show(
                    "RASD Setup must run as Administrator.\n\n" +
                    "Registering the RASD Windows services requires elevation, and " +
                    "elevation was not granted.\n\n" +
                    "Close this window, right-click RASD_Setup.exe and choose " +
                    "\"Run as administrator\".",
                    "RASD Setup — Administrator Required",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
            }
        }

        // ====================================================================
        // ELEVATION + INSTALL LOGGING
        // ====================================================================
        private static bool IsElevated()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(
                        System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        private static readonly object _logLock = new object();

        /// <summary>Appends a timestamped line to C:\ProgramData\RASD\install.log.</summary>
        private static void InstallLog(string msg)
        {
            try
            {
                lock (_logLock)
                {
                    if (!Directory.Exists(CONFIG_DIR)) Directory.CreateDirectory(CONFIG_DIR);
                    File.AppendAllText(INSTALL_LOG,
                        string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}{2}",
                            DateTime.Now, msg, Environment.NewLine),
                        Encoding.UTF8);
                }
            }
            catch { }
        }

        // ====================================================================
        // ROLE SELECTION
        // ====================================================================
        private void RoleCard_Click(object sender, MouseButtonEventArgs e)
        {
            var card = sender as System.Windows.Controls.Border;
            if (card != null && card.Tag is string role) SelectRole(role);
        }

        private void SelectRole(string role)
        {
            _selectedRole = role;
            TxtAuthError.Visibility = Visibility.Hidden;

            bool admin = role == "ADMIN";

            // Administrator card — blue accent when selected
            CardAdmin.BorderBrush = Brush(admin ? "#1A6FC4" : "#D1D5DB");
            CardAdmin.Background  = Brush(admin ? "#E8F1FB" : "#FFFFFF");

            // Endpoint User card — teal accent when selected
            CardEndpoint.BorderBrush = Brush(!admin ? "#0D9B6E" : "#D1D5DB");
            CardEndpoint.Background  = Brush(!admin ? "#E6F7F1" : "#FFFFFF");

            // Continue button follows the selected role's accent
            BtnLogin.Background = Brush(admin ? "#1A6FC4" : "#0D9B6E");
        }

        private static SolidColorBrush Brush(string hex)
            => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        // ── Placeholder text toggles ──────────────────────────────────────
        private void TxtUser_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (PhUser != null)
                PhUser.Visibility = string.IsNullOrEmpty(TxtUser.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TxtPass_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (PhPass != null)
                PhPass.Visibility = string.IsNullOrEmpty(TxtPass.Password)
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        // ====================================================================
        // LOGIN
        // ====================================================================
        private void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            string user = TxtUser.Text.Trim();
            string pass = TxtPass.Password.Trim();

            bool ok = (_selectedRole == "ADMIN"  && user == "admin" && pass == "admin123")
                   || (_selectedRole == "CLIENT" && user == "user"  && pass == "user123");

            if (!ok)
            {
                TxtAuthError.Text = "Invalid username or password. Please try again.";
                TxtAuthError.Visibility = Visibility.Visible;
                return;
            }

            TxtAuthError.Visibility = Visibility.Hidden;

            // Phase 5: scan prerequisites before any deployment. Continue is blocked
            // while a hard dependency is missing.
            ShowPreflight();
        }

        private void BtnViewInstallLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(INSTALL_LOG))
                    Process.Start(new ProcessStartInfo("notepad.exe", INSTALL_LOG)
                        { UseShellExecute = true });
                else
                    MessageBox.Show("No install log was written yet:\n\n" + INSTALL_LOG,
                        "RASD Setup", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the log:\n\n" + ex.Message,
                    "RASD Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ====================================================================
        // MAIN DEPLOYMENT ORCHESTRATOR
        // ====================================================================
        private async void StartDeployment(string tier, string adminIp)
        {
            ViewLogin.Visibility   = Visibility.Collapsed;
            ViewFailure.Visibility = Visibility.Collapsed;
            ViewInstall.Visibility = Visibility.Visible;
            InstallLog("Deployment starting — tier=" + tier);
            TxtInstallMode.Text    = tier == "ADMIN"
                ? "DEPLOYING ADMINISTRATOR TIER"
                : "DEPLOYING ENDPOINT USER TIER";

            try
            {
                UpdateLog(5, "Extracting RASD ecosystem payload...");
                await Task.Run(() => ExtractPayload());

                UpdateLog(15, "Registering application with Windows...");
                await Task.Run(() => RegisterAppInOs());

                UpdateLog(18, "Applying security hardening (discovery key + folder ACLs)...");
                await Task.Run(() => ProvisionSecurity());

                if (tier == "ADMIN")
                {
                    // Check + auto-install Node.js
                    if (!IsNodeInstalled())
                    {
                        UpdateLog(22, "Downloading Node.js 20 LTS from nodejs.org...");
                        await Task.Run(() => DownloadAndInstallNode());
                    }
                    else
                    {
                        UpdateLog(22, "Node.js is already installed.");
                        await Task.Delay(400);
                    }

                    // Detect / repair / install PostgreSQL (see EnsurePostgresReady)
                    await EnsurePostgresReady();

                    UpdateLog(55, "Creating database and injecting schema...");
                    await Task.Run(() => SetupDatabase());

                    UpdateLog(70, "Installing Node.js API dependencies...");
                    await Task.Run(() => EnsureNodeModules());

                    UpdateLog(85, "Configuring admin account (admin123)...");
                    await Task.Run(() => RunAdminSetup());

                    UpdateLog(92, "Opening Windows Firewall for relay, API and discovery...");
                    await Task.Run(() => ConfigureFirewallAdmin());
                }
                else // CLIENT
                {
                    // No admin IP is collected or written any more — the connector
                    // discovers the admin over the LAN (UDP 3002) and caches it.
                    // Firewall first: the services start immediately after
                    // registration and their first probe must not be blocked.
                    UpdateLog(30, "Opening Windows Firewall for discovery...");
                    await Task.Run(() => ConfigureFirewallClient());

                    UpdateLog(40, "Registering RASD_Connector Windows Service...");
                    await Task.Run(() => RegisterConnectorService());

                    UpdateLog(60, "Registering RASD_Protector watchdog service...");
                    await Task.Run(() => RegisterProtectorService());

                    UpdateLog(75, "Verifying services are running...");
                    string svcError = await Task.Run(() => VerifyServices());
                    if (svcError != null)
                    {
                        // Never show a success screen when the agent is not running.
                        ShowFailure(svcError);
                        return;
                    }

                    UpdateLog(88, "Enabling endpoint status app at login...");
                    await Task.Run(() => RegisterTrayAutoStart());
                }

                UpdateLog(94, "Creating shortcuts...");
                await Task.Run(() => CreateShortcuts(tier));

                UpdateLog(98, "Running post-install self-test...");
                string selfTest = await Task.Run(() => SelfTest(tier));
                if (selfTest != null) { ShowFailure("Post-install self-test failed:\n\n" + selfTest); return; }

                UpdateLog(100, "Deployment complete.");
                InstallLog("Deployment completed successfully — tier=" + tier);
                await Task.Delay(800);

                ViewInstall.Visibility = Visibility.Collapsed;
                ViewSuccess.Visibility = Visibility.Visible;

                if (tier == "ADMIN")
                {
                    TxtSuccessMsg.Text = "Admin Control Center is ready. Launching now...";
                    await Task.Delay(1500);
                    LaunchAdminWatcher();
                }
                else
                {
                    TxtSuccessMsg.Text =
                        "Connector and watchdog services are running. This endpoint will " +
                        "find the admin server automatically on your network.";
                    LaunchClientTray();
                }

                ShowWarningsIfAny();
            }
            catch (Exception ex)
            {
                InstallLog("FATAL: Deployment threw — " + ex);
                ShowFailure(ex.Message);
            }
        }

        /// <summary>
        /// Non-fatal problems (usually firewall rules) are shown on the final screen
        /// instead of being buried in the log — a blocked port looks exactly like a
        /// broken product to the user, so they need to know.
        /// </summary>
        private void ShowWarningsIfAny()
        {
            if (_warnings.Count == 0) return;

            var sb = new StringBuilder();
            sb.AppendLine("Installation completed, but with " + _warnings.Count + " warning(s):");
            sb.AppendLine();
            foreach (string w in _warnings) sb.AppendLine("  • " + w);
            sb.AppendLine();
            sb.AppendLine("RASD may not communicate correctly until these are resolved.");
            sb.AppendLine("Details: " + INSTALL_LOG);

            InstallLog("Completed with " + _warnings.Count + " warning(s).");

            Dispatcher.Invoke(() =>
            {
                TxtSuccessMsg.Text += "  (" + _warnings.Count + " warning(s) — see install.log)";
                MessageBox.Show(sb.ToString(), "RASD Setup — Warnings",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }

        /// <summary>Switches to the explicit failure view — never the success screen.</summary>
        private void ShowFailure(string message)
        {
            InstallLog("DEPLOYMENT FAILED: " + message);
            Dispatcher.Invoke(() =>
            {
                ViewInstall.Visibility = Visibility.Collapsed;
                ViewSuccess.Visibility = Visibility.Collapsed;
                ViewFailure.Visibility = Visibility.Visible;
                TxtFailureMsg.Text     = message;
            });
        }

        private void UpdateLog(int progress, string msg)
        {
            Dispatcher.Invoke(() => { PbInstall.Value = progress; TxtLog.Text = msg; });
        }

        // ====================================================================
        // EXTRACT PAYLOAD
        // ====================================================================
        private void ExtractPayload()
        {
            if (!Directory.Exists(INSTALL_DIR))
                Directory.CreateDirectory(INSTALL_DIR);

            using (var stream = Assembly.GetExecutingAssembly()
                                        .GetManifestResourceStream("RASD_Setup.Payload.zip"))
            {
                if (stream == null) return; // Dev mode — no embedded payload yet

                string tempZip = Path.Combine(Path.GetTempPath(), "rasd_payload.zip");
                try
                {
                    using (var fs = File.Create(tempZip)) stream.CopyTo(fs);
                    using (var archive = ZipFile.OpenRead(tempZip))
                    {
                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            string destPath = Path.Combine(INSTALL_DIR, entry.FullName);
                            string destDir  = Path.GetDirectoryName(destPath);
                            if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                            if (!string.IsNullOrEmpty(entry.Name))
                                entry.ExtractToFile(destPath, overwrite: true);
                        }
                    }
                }
                finally
                {
                    try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                }
            }
        }

        // ====================================================================
        // WINDOWS REGISTRATION (Apps & Features — "RASD")
        // ====================================================================
        private void RegisterAppInOs()
        {
            const string keyPath =
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RASD_Operations_Ecosystem";

            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(keyPath))
            {
                if (key == null) return;
                string uninstaller = Path.Combine(INSTALL_DIR, "RASD_Uninstaller.exe");
                string icon        = Path.Combine(INSTALL_DIR, "assets", "rasd.ico");

                key.SetValue("DisplayName",         "RASD Operations Ecosystem");
                key.SetValue("Publisher",            "RASD");
                key.SetValue("DisplayVersion",       "2.0.0");
                key.SetValue("InstallLocation",      INSTALL_DIR);
                key.SetValue("InstallDate",          DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("UninstallString",      uninstaller);
                key.SetValue("QuietUninstallString", uninstaller + " /silent");
                key.SetValue("EstimatedSize",        450000, RegistryValueKind.DWord);
                key.SetValue("NoModify",             1, RegistryValueKind.DWord);
                key.SetValue("NoRepair",             1, RegistryValueKind.DWord);
                if (File.Exists(icon)) key.SetValue("DisplayIcon", icon);
            }
        }

        // ====================================================================
        // NODE.JS — CHECK AND AUTO-INSTALL
        // ====================================================================
        private static bool IsNodeInstalled()
        {
            // Check PATH for node.exe
            string nodePath = FindInPath("node.exe");
            if (nodePath != null) return true;

            // Check default install location
            if (File.Exists(@"C:\Program Files\nodejs\node.exe")) return true;

            return false;
        }

        private void DownloadAndInstallNode()
        {
            string tempMsi = Path.Combine(Path.GetTempPath(), "nodejs_installer.msi");
            try
            {
                UpdateLog(23, "Downloading Node.js 20 LTS... (this may take a minute)");
                DownloadFile(NODE_INSTALLER_URL, tempMsi, (pct) =>
                    UpdateLog(23 + (int)(pct * 0.1), // occupies 10% of progress bar
                        string.Format("Downloading Node.js... {0}%", pct)));

                UpdateLog(33, "Installing Node.js silently...");
                var psi = new ProcessStartInfo
                {
                    FileName        = "msiexec.exe",
                    Arguments       = string.Format("/i \"{0}\" /qn /norestart ADDLOCAL=ALL", tempMsi),
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(180000))
                    {
                        p.Kill();
                        throw new Exception("Node.js installation timed out.");
                    }
                    if (p.ExitCode != 0 && p.ExitCode != 3010) // 3010 = success, reboot needed
                        throw new Exception(string.Format("Node.js installer returned exit code {0}.", p.ExitCode));
                }

                // Refresh PATH in current process
                string currentPath = Environment.GetEnvironmentVariable("PATH",
                    EnvironmentVariableTarget.Machine);
                Environment.SetEnvironmentVariable("PATH", currentPath);
            }
            finally
            {
                try { if (File.Exists(tempMsi)) File.Delete(tempMsi); } catch { }
            }
        }

        // ====================================================================
        // POSTGRESQL — CHECK AND AUTO-INSTALL
        // ====================================================================
        private static bool IsPostgresInstalled()
        {
            if (File.Exists(PSQL_PATH))     return true;
            if (File.Exists(PSQL_PATH_ALT)) return true;

            // Check registry
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\PostgreSQL\Installations", false))
            {
                if (key != null && key.SubKeyCount > 0) return true;
            }
            return false;
        }

        private void DownloadAndInstallPostgres()
        {
            string tempExe = Path.Combine(Path.GetTempPath(), "postgresql_installer.exe");
            try
            {
                UpdateLog(36, "Downloading PostgreSQL 16... (this may take 2-3 minutes)");
                DownloadFile(PG_INSTALLER_URL, tempExe, (pct) =>
                    UpdateLog(36 + (int)(pct * 0.15),
                        string.Format("Downloading PostgreSQL 16... {0}%", pct)));

                UpdateLog(51, "Installing PostgreSQL 16 silently...");
                var psi = new ProcessStartInfo
                {
                    FileName  = tempExe,
                    Arguments = "--mode unattended --superpassword admin123 " +
                                "--serverport 5432 --servicename postgresql-x64-16 " +
                                "--servicepassword admin123 --unattendedmodeui none",
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                };
                using (var p = Process.Start(psi))
                {
                    const int totalTimeoutMs = 20 * 60 * 1000; // 20 minutes
                    const int tickMs         = 30000;          // progress update every 30s
                    int waitedMs = 0;

                    // Poll so we can surface progress. WaitForExit(tickMs) returns true
                    // once the installer finishes, or false after each 30s slice.
                    while (!p.WaitForExit(tickMs))
                    {
                        waitedMs += tickMs;
                        if (waitedMs >= totalTimeoutMs)
                        {
                            p.Kill();
                            throw new Exception("PostgreSQL installation timed out after 20 minutes.");
                        }
                        UpdateLog(51, string.Format(
                            "Still installing PostgreSQL 16... please wait ({0} min elapsed)",
                            waitedMs / 60000));
                    }

                    if (p.ExitCode != 0)
                        throw new Exception(string.Format("PostgreSQL installer returned exit code {0}.", p.ExitCode));
                }

                // Give the PostgreSQL service a moment to start
                Thread.Sleep(4000);

                if (!File.Exists(PSQL_PATH))
                    throw new Exception("PostgreSQL installed but psql.exe not found. Try rebooting and re-running.");
            }
            finally
            {
                try { if (File.Exists(tempExe)) File.Delete(tempExe); } catch { }
            }
        }

        // ====================================================================
        // POSTGRESQL — DETECT / REPAIR / INSTALL
        // ====================================================================
        //  C1: psql.exe present AND service running        -> skip install
        //  C2: psql.exe present BUT service missing/stopped -> try to start;
        //      if unrecoverable, remove the broken install and reinstall
        //  C3: psql.exe absent                              -> fresh install
        private async Task EnsurePostgresReady()
        {
            // CONDITION 3 — fresh machine
            if (!File.Exists(PSQL_PATH))
            {
                UpdateLog(35, "Downloading PostgreSQL 16 from enterprisedb.com...");
                await Task.Run(() => DownloadAndInstallPostgres());
                return;
            }

            // CONDITION 1 — fully installed and running
            if (IsServiceRunning(PG_SERVICE))
            {
                UpdateLog(35, "PostgreSQL 16 is installed and running.");
                await Task.Delay(300);
                return;
            }

            // CONDITION 2 — psql present but service missing/stopped: broken install
            UpdateLog(35, "Broken PostgreSQL installation detected — attempting to start service...");
            await Task.Run(() => StartPgService(PG_SERVICE));
            await Task.Delay(10000); // give the service 10s to come up

            if (IsServiceRunning(PG_SERVICE))
            {
                UpdateLog(40, "PostgreSQL service started successfully.");
                await Task.Delay(300);
                return;
            }

            // Still down — remove the broken installation and reinstall from scratch
            UpdateLog(40, "Service unrecoverable — removing broken installation...");
            await Task.Run(() => RemoveBrokenPostgres());

            UpdateLog(45, "Broken installation removed — reinstalling PostgreSQL 16 from scratch...");
            await Task.Run(() => DownloadAndInstallPostgres());
        }

        // Returns true only if the given Windows service exists AND is running.
        private static bool IsServiceRunning(string service)
        {
            try
            {
                var psi = new ProcessStartInfo("sc.exe", "query \"" + service + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                    RedirectStandardOutput = true,
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return output.Contains("RUNNING");
                }
            }
            catch { return false; }
        }

        // net start <service>
        private static void StartPgService(string service)
        {
            try
            {
                var psi = new ProcessStartInfo("net.exe", "start \"" + service + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                };
                using (var p = Process.Start(psi)) p.WaitForExit(30000);
            }
            catch { /* start failure is handled by the caller re-checking the service */ }
        }

        // Silently uninstall a broken PostgreSQL 16, deleting leftovers + registry keys.
        private static void RemoveBrokenPostgres()
        {
            // 1) Prefer the bundled uninstaller (silent)
            string[] uninstallers =
            {
                Path.Combine(PG_DIR, "uninstall-postgresql.exe"),
                Path.Combine(PG_DIR, "uninstall.exe"),
            };
            foreach (var un in uninstallers)
            {
                if (!File.Exists(un)) continue;
                try
                {
                    var psi = new ProcessStartInfo(un, "--mode unattended")
                    { UseShellExecute = false, CreateNoWindow = true };
                    using (var p = Process.Start(psi)) p.WaitForExit(180000);
                }
                catch { }
                break;
            }

            // 2) If the folder is still there, delete it outright
            if (Directory.Exists(PG_DIR))
            {
                try { Directory.Delete(PG_DIR, recursive: true); } catch { }
                if (Directory.Exists(PG_DIR))
                {
                    try
                    {
                        var psi = new ProcessStartInfo("cmd.exe", "/c rd /s /q \"" + PG_DIR + "\"")
                        { UseShellExecute = false, CreateNoWindow = true };
                        using (var p = Process.Start(psi)) p.WaitForExit(30000);
                    }
                    catch { }
                }
            }

            // 3) Remove PostgreSQL registry keys
            try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\PostgreSQL", throwOnMissingSubKey: false); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\WOW6432Node\PostgreSQL", throwOnMissingSubKey: false); } catch { }
        }

        // ====================================================================
        // DATABASE SETUP
        // ====================================================================

        // Try these passwords in order when connecting to PostgreSQL
        private static readonly string[] PG_PASSWORDS = {
            "admin123", "postgres", "admin", "password", ""
        };

        private void SetupDatabase()
        {
            string psql = FindPsqlExe();
            if (psql == null)
                throw new Exception(
                    "psql.exe not found.\n" +
                    "Expected at: C:\\Program Files\\PostgreSQL\\16\\bin\\psql.exe\n" +
                    "Make sure PostgreSQL 16 is installed.");

            // Step 1: Make sure the PostgreSQL Windows service is running
            EnsurePostgresServiceRunning();

            // Step 2: Find which password works (user may have installed PG with a different password)
            string workingPassword = FindWorkingPgPassword(psql);

            // All automatic attempts failed — PostgreSQL is likely pre-installed with a
            // custom password. Pause and ask the user to enter it, retrying until it
            // works or they cancel.
            while (workingPassword == null)
            {
                string entered = PromptForPgPassword();
                if (string.IsNullOrEmpty(entered))
                    throw new Exception(
                        "Cannot connect to PostgreSQL as user 'postgres'.\n" +
                        "No password was entered. Set the postgres password and re-run.");

                UpdateLog(55, "Verifying PostgreSQL password...");
                if (TestPgPassword(psql, entered))
                    workingPassword = entered;
                else
                    UpdateLog(55, "Incorrect password — please try again.");
            }

            // Step 3: Create the database (safe — ignores if already exists)
            RunPsqlWithPassword(psql, "postgres", workingPassword,
                "-c \"CREATE DATABASE rasd_security;\"", ignoreErrors: true);

            // Step 4: Find the schema file
            string[] schemaPaths = {
                Path.Combine(INSTALL_DIR, "API_Server", "db", "schema.sql"),
                Path.Combine(INSTALL_DIR, "db", "schema.sql"),
                Path.Combine(INSTALL_DIR, "API_Server", "db_init.sql"),
            };
            string schema = null;
            foreach (var p in schemaPaths) if (File.Exists(p)) { schema = p; break; }
            if (schema == null)
                throw new Exception("Schema file not found inside: " + INSTALL_DIR +
                    "\nPayload extraction may have failed.");

            // Step 5: Inject schema — always ignore errors (IF NOT EXISTS handles duplicates)
            RunPsqlWithPassword(psql, "rasd_security", workingPassword,
                string.Format("-v ON_ERROR_STOP=0 -f \"{0}\"", schema),
                ignoreErrors: true);

            // Step 6: If the working password wasn't admin123, update it so the API can connect
            if (workingPassword != "admin123")
            {
                RunPsqlWithPassword(psql, "postgres", workingPassword,
                    "-c \"ALTER USER postgres WITH PASSWORD 'admin123';\"",
                    ignoreErrors: true);
            }
        }

        private static string FindPsqlExe()
        {
            // Check all common PostgreSQL installation paths
            string[] paths = {
                @"C:\Program Files\PostgreSQL\16\bin\psql.exe",
                @"C:\Program Files\PostgreSQL\15\bin\psql.exe",
                @"C:\Program Files\PostgreSQL\17\bin\psql.exe",
                @"C:\Program Files\PostgreSQL\14\bin\psql.exe",
            };
            foreach (var p in paths) if (File.Exists(p)) return p;

            // Search registry for any installed version
            string[] regKeys = {
                @"SOFTWARE\PostgreSQL\Installations",
                @"SOFTWARE\WOW6432Node\PostgreSQL\Installations",
            };
            foreach (var rk in regKeys)
            {
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(rk))
                    {
                        if (key == null) continue;
                        foreach (string sub in key.GetSubKeyNames())
                        {
                            using (var inst = key.OpenSubKey(sub))
                            {
                                string loc = inst?.GetValue("Base Directory") as string;
                                if (loc != null)
                                {
                                    string candidate = Path.Combine(loc, "bin", "psql.exe");
                                    if (File.Exists(candidate)) return candidate;
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static void EnsurePostgresServiceRunning()
        {
            // Find and start any PostgreSQL service that exists
            string[] serviceNames = {
                "postgresql-x64-16", "postgresql-x64-15",
                "postgresql-x64-17", "postgresql-x64-14",
                "postgresql", "PostgreSQL"
            };

            foreach (var svc in serviceNames)
            {
                try
                {
                    var sc = new ProcessStartInfo("sc.exe", "query \"" + svc + "\"")
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true
                    };
                    using (var p = Process.Start(sc))
                    {
                        string output = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(5000);
                        if (output.Contains("RUNNING")) return; // Already running

                        if (output.Contains("STOPPED") || output.Contains("PAUSED"))
                        {
                            // Start it
                            var start = new ProcessStartInfo("sc.exe", "start \"" + svc + "\"")
                            { UseShellExecute = false, CreateNoWindow = true };
                            using (var ps = Process.Start(start))
                                ps.WaitForExit(15000);

                            Thread.Sleep(3000); // Give it time to start
                            return;
                        }
                    }
                }
                catch { }
            }
        }

        private static bool TestPgPassword(string psql, string pwd)
        {
            var psi = new ProcessStartInfo
            {
                FileName        = psql,
                Arguments       = "-U postgres -d postgres -c \"SELECT 1;\" -t",
                UseShellExecute = false,
                CreateNoWindow  = true,
            };
            psi.EnvironmentVariables["PGPASSWORD"]        = pwd;
            psi.EnvironmentVariables["PGCONNECT_TIMEOUT"] = "5";

            try
            {
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(8000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private static string FindWorkingPgPassword(string psql)
        {
            foreach (string pwd in PG_PASSWORDS)
                if (TestPgPassword(psql, pwd)) return pwd;
            return null;
        }

        // Modal dialog shown when all automatic password attempts fail (e.g. PostgreSQL
        // was already installed with a custom password). Returns the entered password,
        // or null if the user cancelled/closed the dialog.
        private string PromptForPgPassword()
        {
            return Dispatcher.Invoke(() =>
            {
                var dlg = new Window
                {
                    Title = "PostgreSQL Password",
                    Width = 400, Height = 210,
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    WindowStyle = WindowStyle.ToolWindow,
                    ResizeMode = ResizeMode.NoResize,
                    Background = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString("#0D1B2A"),
                };

                var panel = new StackPanel { Margin = new Thickness(20) };

                panel.Children.Add(new TextBlock
                {
                    Text = "PostgreSQL is already installed.\nPlease enter your postgres user password:",
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 14),
                });

                var pb = new PasswordBox
                {
                    Height = 32, FontSize = 13,
                    Padding = new Thickness(8, 0, 0, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                panel.Children.Add(pb);

                var btn = new Button
                {
                    Content = "Confirm",
                    Height = 36,
                    Margin = new Thickness(0, 16, 0, 0),
                    Foreground = Brushes.White,
                    Background = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString("#1A6FC4"),
                    BorderThickness = new Thickness(0),
                    FontWeight = FontWeights.SemiBold,
                    Cursor = Cursors.Hand,
                };
                panel.Children.Add(btn);

                string result = null;
                btn.Click  += (s, e) => { result = pb.Password; dlg.DialogResult = true; };
                pb.KeyDown += (s, e) => { if (e.Key == Key.Enter) { result = pb.Password; dlg.DialogResult = true; } };
                pb.Loaded  += (s, e) => pb.Focus();

                dlg.Content = panel;
                dlg.ShowDialog();

                return string.IsNullOrEmpty(result) ? null : result;
            });
        }

        private void RunPsqlWithPassword(string psqlExe, string database,
            string password, string args, bool ignoreErrors)
        {
            var psi = new ProcessStartInfo
            {
                FileName        = psqlExe,
                Arguments       = string.Format("-U postgres -d {0} {1}", database, args),
                UseShellExecute = false,
                CreateNoWindow  = true,
            };
            psi.EnvironmentVariables["PGPASSWORD"]        = password;
            psi.EnvironmentVariables["PGCONNECT_TIMEOUT"] = "10";

            using (var p = Process.Start(psi))
            {
                p.WaitForExit(60000);
                if (!ignoreErrors && p.ExitCode != 0)
                    throw new Exception(string.Format("psql exited with code {0} on database {1}.",
                        p.ExitCode, database));
            }
        }

        // ====================================================================
        // ENSURE NODE_MODULES
        // ====================================================================
        private void EnsureNodeModules()
        {
            string apiDir      = Path.Combine(INSTALL_DIR, "API_Server");
            string nodeModules = Path.Combine(apiDir, "node_modules");
            if (!Directory.Exists(apiDir)) return;
            if (Directory.Exists(nodeModules)) return;

            string npm = FindInPath("npm.cmd") ?? @"C:\Program Files\nodejs\npm.cmd";
            if (!File.Exists(npm)) npm = "npm.cmd";

            var psi = new ProcessStartInfo
            {
                FileName         = npm,
                Arguments        = "install --omit=dev",
                WorkingDirectory = apiDir,
                UseShellExecute  = false,
                CreateNoWindow   = true,
            };
            using (var p = Process.Start(psi))
                p.WaitForExit(120000);
        }

        // ====================================================================
        // SETUP ADMIN USER (bcrypt hash for admin123)
        // ====================================================================
        private void RunAdminSetup()
        {
            string script = Path.Combine(INSTALL_DIR, "API_Server", "setup_admin.js");
            if (!File.Exists(script)) return;

            string node = FindInPath("node.exe") ?? @"C:\Program Files\nodejs\node.exe";
            if (!File.Exists(node) && FindInPath("node.exe") == null)
            {
                node = "node.exe"; // rely on PATH
            }

            var psi = new ProcessStartInfo
            {
                FileName         = node,
                Arguments        = string.Format("\"{0}\"", script),
                WorkingDirectory = Path.Combine(INSTALL_DIR, "API_Server"),
                UseShellExecute  = false,
                CreateNoWindow   = true,
            };
            using (var p = Process.Start(psi))
                p.WaitForExit(30000);
        }


        // ====================================================================
        // REGISTER CONNECTOR AS WINDOWS SERVICE
        // ====================================================================
        private void RegisterConnectorService()
        {
            string exe = FindExeInInstallDir("RASD_Connector.exe");
            if (exe == null)
                throw new Exception(
                    "RASD_Connector.exe not found under " + INSTALL_DIR + ". " +
                    "Make sure the payload extracted correctly.");

            InstallLog("RegisterConnectorService: binPath = " + exe);

            ScExe("stop",    "\"" + SVC_CONNECTOR + "\"", ignoreError: true);
            Thread.Sleep(1000);
            ScExe("delete",  "\"" + SVC_CONNECTOR + "\"", ignoreError: true);
            Thread.Sleep(500);

            // Mandatory — a failure here must abort the install, not slip through.
            ScExe("create",
                "\"" + SVC_CONNECTOR + "\" " +
                "binPath= \"" + exe + "\" " +
                "type= own start= auto obj= LocalSystem " +
                "DisplayName= \"RASD Enterprise Security Connector\"",
                ignoreError: false);

            // Restart automatically on crash, with no failure-count reset window.
            ScExe("failure",
                "\"" + SVC_CONNECTOR + "\" reset= 0 " +
                "actions= restart/5000/restart/5000/restart/5000",
                ignoreError: true);

            ScExe("description",
                "\"" + SVC_CONNECTOR + "\" " +
                "\"RASD endpoint security agent — USB control, file monitoring, heartbeat.\"",
                ignoreError: true);

            ScExe("start", "\"" + SVC_CONNECTOR + "\"", ignoreError: false);
        }

        // ====================================================================
        // REGISTER PROTECTOR WATCHDOG
        // ====================================================================
        private void RegisterProtectorService()
        {
            string exe = FindExeInInstallDir("RASD_Protector.exe");
            if (exe == null)
            {
                InstallLog("WARN: RASD_Protector.exe not found — watchdog not registered.");
                return; // Optional — the connector alone is still functional
            }

            InstallLog("RegisterProtectorService: binPath = " + exe);

            ScExe("stop",   "\"" + SVC_PROTECTOR + "\"", ignoreError: true);
            Thread.Sleep(500);
            ScExe("delete", "\"" + SVC_PROTECTOR + "\"", ignoreError: true);
            Thread.Sleep(500);

            ScExe("create",
                "\"" + SVC_PROTECTOR + "\" " +
                "binPath= \"" + exe + "\" " +
                "type= own start= auto obj= LocalSystem " +
                "DisplayName= \"RASD Enterprise Watchdog\"",
                ignoreError: true);

            ScExe("failure",
                "\"" + SVC_PROTECTOR + "\" reset= 0 " +
                "actions= restart/5000/restart/5000/restart/5000",
                ignoreError: true);

            ScExe("start", "\"" + SVC_PROTECTOR + "\"", ignoreError: true);
        }

        // ====================================================================
        // CREATE SHORTCUTS (Desktop + Start Menu) — like Chrome/Discord
        // ====================================================================
        private void CreateShortcuts(string tier)
        {
            try
            {
                // ADMIN:  shortcut targets RASD.exe (the launcher entry-point)
                // CLIENT: shortcut targets the tray status app — the endpoint user
                //         now has a real application instead of a bare service.
                string targetExe = tier == "ADMIN"
                    ? (FindExeInInstallDir("RASD.exe") ?? FindExeInInstallDir("RASD_Relay.exe"))
                    : (FindExeInInstallDir("RASD_ClientTray.exe") ?? FindExeInInstallDir("RASD_Connector.exe"));

                if (targetExe == null) return;

                string shortcutName = tier == "ADMIN"
                    ? "RASD.lnk"
                    : "RASD Endpoint Status.lnk";

                string iconPath = Path.Combine(INSTALL_DIR, "assets", "rasd.ico");

                // COM interop for shell shortcut creation
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell  = Activator.CreateInstance(shellType);

                // Public Desktop (visible to all users, like Chrome)
                string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                dynamic sc1 = shell.CreateShortcut(System.IO.Path.Combine(publicDesktop, shortcutName));
                sc1.TargetPath       = targetExe;
                sc1.WorkingDirectory = INSTALL_DIR;
                sc1.Description      = "RASD Operations Ecosystem";
                if (File.Exists(iconPath)) sc1.IconLocation = iconPath + ",0";
                sc1.Save();

                // Start Menu (All Programs, like Discord)
                string startMenu = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "RASD");
                if (!System.IO.Directory.Exists(startMenu))
                    System.IO.Directory.CreateDirectory(startMenu);

                dynamic sc2 = shell.CreateShortcut(System.IO.Path.Combine(startMenu, shortcutName));
                sc2.TargetPath       = targetExe;
                sc2.WorkingDirectory = INSTALL_DIR;
                sc2.Description      = "RASD Operations Ecosystem";
                if (File.Exists(iconPath)) sc2.IconLocation = iconPath + ",0";
                sc2.Save();

                // Phase 5 cutover: the dashboard opens in the WebView2 shell by default.
                // The browser path stays reachable via a separate "(Browser)" shortcut and
                // the shell's own toolbar — it is the recovery path if WebView2 fails.
                if (tier == "ADMIN")
                {
                    string shellExe = FindExeInInstallDir("RASD_Shell.exe");
                    if (shellExe != null)
                    {
                        void MakeLnk(string folder, string name, string args)
                        {
                            dynamic s = shell.CreateShortcut(System.IO.Path.Combine(folder, name));
                            s.TargetPath = shellExe; s.Arguments = args; s.WorkingDirectory = INSTALL_DIR;
                            s.Description = "RASD Operations Dashboard";
                            if (File.Exists(iconPath)) s.IconLocation = iconPath + ",0";
                            s.Save();
                        }
                        MakeLnk(publicDesktop, "RASD Dashboard.lnk", "");
                        MakeLnk(startMenu,      "RASD Dashboard.lnk", "");
                        MakeLnk(startMenu,      "RASD Dashboard (Browser).lnk", "--browser");
                    }
                }
            }
            catch { /* Non-fatal */ }
        }

        // ====================================================================
        // AUTO-LAUNCH (RASD.exe is the entry-point; RASD_Relay.exe is fallback)
        // ====================================================================
        private void LaunchAdminWatcher()
        {
            string exe = FindExeInInstallDir("RASD.exe")
                      ?? FindExeInInstallDir("RASD_Relay.exe")
                      ?? FindExeInInstallDir("RASD_AdminWatcher.exe");
            if (exe == null) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = exe,
                    UseShellExecute = true,
                });
            }
            catch { }
        }

        // ====================================================================
        // HELPERS
        // ====================================================================
        private static string FindExeInInstallDir(string exeName)
        {
            string[] candidates = {
                Path.Combine(INSTALL_DIR, exeName),
                Path.Combine(INSTALL_DIR, "Client_Connector", exeName),
                Path.Combine(INSTALL_DIR, "Client_Connector", "RASD_Connector", exeName),
                Path.Combine(INSTALL_DIR, "Client_Connector", "RASD_Protector", exeName),
                Path.Combine(INSTALL_DIR, "Admin_Watcher", exeName),
                Path.Combine(INSTALL_DIR, "bin", exeName),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return null;
        }

        private static string FindInPath(string exeName)
        {
            try
            {
                string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (string dir in pathVar.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string full = Path.Combine(dir.Trim(), exeName);
                    if (File.Exists(full)) return full;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Runs sc.exe, capturing stdout/stderr and the exit code, and logs both.
        /// The old version discarded everything, which is why silent service
        /// registration failures were invisible.
        /// </summary>
        private int ScExe(string command, string args, bool ignoreError)
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "sc.exe",
                Arguments              = command + " " + args,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };

            try
            {
                using (var p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(20000))
                    {
                        InstallLog("sc " + command + " TIMED OUT after 20s");
                        if (!ignoreError) throw new Exception("sc " + command + " timed out.");
                        return -1;
                    }

                    string combined = (stdout + " " + stderr).Replace("\r", " ").Replace("\n", " ").Trim();
                    InstallLog(string.Format("sc {0} {1} -> exit {2} :: {3}",
                        command, args, p.ExitCode, Trim(combined, 300)));

                    if (p.ExitCode != 0 && !ignoreError)
                        throw new Exception(string.Format(
                            "sc {0} failed (exit {1}): {2}", command, p.ExitCode, Trim(combined, 200)));

                    return p.ExitCode;
                }
            }
            catch (Exception ex) when (!(ex.Message.StartsWith("sc ")))
            {
                InstallLog("sc " + command + " threw — " + ex.Message);
                if (!ignoreError) throw new Exception("sc.exe failed: " + ex.Message);
                return -1;
            }
        }

        private static string Trim(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "...");

        // ====================================================================
        // WINDOWS FIREWALL — Defender blocks UDP broadcast and inbound TCP on a
        // fresh machine, which silently breaks discovery and the relay. Rules are
        // added with profile=any because Public is the default profile on an
        // unknown network and Public blocks broadcast.
        // A rule that fails to add is a loud warning, never a failed install.
        // ====================================================================
        private readonly List<string> _warnings = new List<string>();

        /// <summary>Deletes any previous rule of the same name, then adds it. Logs both exit codes.</summary>
        private void FirewallRule(string name, string ruleArgs)
        {
            // Delete first so re-installs do not accumulate duplicate rules.
            Netsh("advfirewall firewall delete rule name=\"" + name + "\"", "delete " + name, warnOnFail: false);

            int code = Netsh(
                "advfirewall firewall add rule name=\"" + name + "\" " + ruleArgs,
                "add " + name, warnOnFail: true);

            if (code == 0) InstallLog("Firewall rule OK: " + name);
        }

        private int Netsh(string args, string label, bool warnOnFail)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = "netsh.exe",
                    Arguments              = args,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(20000))
                    {
                        InstallLog("netsh " + label + " TIMED OUT");
                        if (warnOnFail) _warnings.Add("Firewall rule timed out: " + label);
                        return -1;
                    }

                    string flat = output.Replace("\r", " ").Replace("\n", " ").Trim();
                    InstallLog(string.Format("netsh {0} -> exit {1} :: {2}",
                        label, p.ExitCode, Trim(flat, 200)));

                    if (p.ExitCode != 0 && warnOnFail)
                    {
                        _warnings.Add("Firewall rule FAILED: " + label + " (exit " + p.ExitCode + ")");
                        InstallLog("WARN: firewall rule failed — " + label +
                                   ". Discovery or relay traffic may be blocked.");
                    }
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                InstallLog("netsh " + label + " threw — " + ex.Message);
                if (warnOnFail) _warnings.Add("Firewall rule error: " + label + " — " + ex.Message);
                return -1;
            }
        }

        // ====================================================================
        // SECURITY HARDENING (A1 + A2)
        //  - Provision the shared discovery key (node.key) from the payload.
        //  - Lock C:\ProgramData\RASD so a standard user cannot plant
        //    allow_wifi.flag, read the key, or tamper with connector.log.
        //  - Leave an ipc\ subfolder user-writable for the tray reconnect flag.
        // ====================================================================
        private void ProvisionSecurity()
        {
            try
            {
                if (!Directory.Exists(CONFIG_DIR)) Directory.CreateDirectory(CONFIG_DIR);

                // node.key ships inside the payload; every machine from the same
                // installer therefore shares one key (documented design choice).
                string payloadKey = FindPayloadFile("node.key");
                string destKey    = Path.Combine(CONFIG_DIR, "node.key");
                if (payloadKey != null)
                {
                    File.Copy(payloadKey, destKey, overwrite: true);
                    InstallLog("Security: discovery key provisioned -> " + destKey);
                }
                else if (File.Exists(destKey))
                {
                    InstallLog("Security: payload node.key not found; keeping existing " + destKey);
                }
                else
                {
                    _warnings.Add("Discovery key (node.key) was not provisioned — LAN discovery will be disabled.");
                    InstallLog("WARN: Security: no node.key in payload and none on disk. Discovery auth unavailable.");
                }

                // Lock the whole config dir: SYSTEM + Administrators full, Users
                // read/execute only (no write, no create) — blocks A2 escalation.
                Icacls(CONFIG_DIR,
                    "/inheritance:r /grant:r \"*S-1-5-18:(OI)(CI)F\" \"*S-1-5-32-544:(OI)(CI)F\" \"*S-1-5-32-545:(OI)(CI)RX\"",
                    "ACL lock " + CONFIG_DIR);

                // The key is even tighter: SYSTEM + Administrators only, no Users read,
                // so a standard user cannot read it to forge discovery HMACs.
                if (File.Exists(destKey))
                    Icacls(destKey,
                        "/inheritance:r /grant:r \"*S-1-5-18:F\" \"*S-1-5-32-544:F\"",
                        "ACL lock node.key");

                // ipc\ subfolder stays user-writable so the tray (limited user) can
                // drop reconnect.flag. It holds nothing security-sensitive.
                string ipc = Path.Combine(CONFIG_DIR, "ipc");
                if (!Directory.Exists(ipc)) Directory.CreateDirectory(ipc);
                Icacls(ipc, "/grant:r \"*S-1-5-32-545:(OI)(CI)M\"", "ACL ipc user-writable");
            }
            catch (Exception ex)
            {
                _warnings.Add("Security hardening did not fully apply — " + ex.Message);
                InstallLog("WARN: ProvisionSecurity threw — " + ex.Message);
            }
        }

        private void Icacls(string target, string args, string label)
        {
            int code = RunCapture("icacls.exe", "\"" + target + "\" " + args, label);
            if (code != 0)
            {
                _warnings.Add("Permission hardening step failed: " + label + " (icacls exit " + code + ").");
                InstallLog("WARN: icacls failed — " + label + " (exit " + code + ").");
            }
        }

        /// <summary>Locates a loose file inside the extracted payload (e.g. node.key).</summary>
        private static string FindPayloadFile(string name)
        {
            string[] candidates = {
                Path.Combine(INSTALL_DIR, name),
                Path.Combine(INSTALL_DIR, "assets", name),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return null;
        }

        /// <summary>ADMIN: inbound relay TCP 3001, API TCP 3000, discovery UDP 3002.</summary>
        private void ConfigureFirewallAdmin()
        {
            InstallLog("Configuring Windows Firewall for the ADMIN role...");

            FirewallRule("RASD Relay TCP In",
                "dir=in action=allow protocol=TCP localport=3001 profile=any enable=yes");
            FirewallRule("RASD API TCP In",
                "dir=in action=allow protocol=TCP localport=3000 profile=any enable=yes");
            // UDP 3002 inbound is what lets DiscoveryResponder receive agent probes.
            FirewallRule("RASD Discovery UDP In",
                "dir=in action=allow protocol=UDP localport=3002 profile=any enable=yes");

            string relay = FindExeInInstallDir("RASD_Relay.exe");
            if (relay != null)
            {
                FirewallRule("RASD Relay Program In",
                    "dir=in action=allow profile=any program=\"" + relay + "\" enable=yes");
            }
            else
            {
                InstallLog("WARN: RASD_Relay.exe not found — program-scoped rule skipped.");
                _warnings.Add("Program-scoped firewall rule for RASD_Relay.exe was skipped (exe not found).");
            }
        }

        /// <summary>
        /// CLIENT: the agent sends its probe from an ephemeral port and the admin
        /// answers by unicast, so the agent needs an inbound program rule for the
        /// reply to arrive at all.
        /// </summary>
        private void ConfigureFirewallClient()
        {
            InstallLog("Configuring Windows Firewall for the CLIENT role...");

            string connector = FindExeInInstallDir("RASD_Connector.exe");
            if (connector != null)
            {
                FirewallRule("RASD Connector UDP In",
                    "dir=in action=allow protocol=UDP profile=any program=\"" + connector + "\" enable=yes");
                FirewallRule("RASD Connector Program In",
                    "dir=in action=allow profile=any program=\"" + connector + "\" enable=yes");
            }
            else
            {
                InstallLog("WARN: RASD_Connector.exe not found — client firewall rules skipped.");
                _warnings.Add("Firewall rules for RASD_Connector.exe were skipped (exe not found).");
            }

            // Listening for the admin's unprompted 30s announcements.
            FirewallRule("RASD Discovery UDP In",
                "dir=in action=allow protocol=UDP localport=3002 profile=any enable=yes");
        }

        // ====================================================================
        // SERVICE VERIFICATION — returns null on success, else an error message
        // ====================================================================
        private string VerifyServices()
        {
            // The connector is mandatory; the protector watchdog is best-effort.
            string state = WaitForServiceRunning(SVC_CONNECTOR, 30);
            InstallLog("Verification: " + SVC_CONNECTOR + " final state = " + state);

            if (state != "RUNNING")
            {
                // One explicit retry before declaring failure.
                InstallLog("Verification: retrying start of " + SVC_CONNECTOR);
                ScExe("start", "\"" + SVC_CONNECTOR + "\"", ignoreError: true);
                state = WaitForServiceRunning(SVC_CONNECTOR, 20);
                InstallLog("Verification: " + SVC_CONNECTOR + " after retry = " + state);
            }

            if (state != "RUNNING")
            {
                return string.Format(
                    "The {0} service was registered but is not running (state: {1}).\n\n" +
                    "The endpoint agent is therefore NOT protecting this machine.\n" +
                    "See install.log for the exact sc.exe output.",
                    SVC_CONNECTOR, state);
            }

            string pState = WaitForServiceRunning(SVC_PROTECTOR, 10);
            InstallLog("Verification: " + SVC_PROTECTOR + " final state = " + pState);
            if (pState != "RUNNING")
                InstallLog("WARN: watchdog not running (" + pState + ") — connector is still active.");

            return null;
        }

        /// <summary>Polls sc query until the service reports RUNNING or time runs out.</summary>
        private string WaitForServiceRunning(string service, int timeoutSeconds)
        {
            string last = "UNKNOWN";
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

            while (DateTime.UtcNow < deadline)
            {
                last = QueryServiceState(service);
                if (last == "RUNNING") return last;
                if (last == "NOT_INSTALLED") return last;
                Thread.Sleep(1000);
            }
            return last;
        }

        /// <summary>Parses the STATE line out of sc query output.</summary>
        private string QueryServiceState(string service)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = "sc.exe",
                    Arguments              = "query \"" + service + "\"",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(10000);

                    if (output.IndexOf("1060", StringComparison.Ordinal) >= 0 ||
                        output.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "NOT_INSTALLED";

                    var m = Regex.Match(output, @"STATE\s*:\s*\d+\s+(\w+)");
                    return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "UNKNOWN";
                }
            }
            catch (Exception ex)
            {
                InstallLog("QueryServiceState(" + service + ") threw — " + ex.Message);
                return "UNKNOWN";
            }
        }

        // ====================================================================
        // CLIENT TRAY APP — autostart at login + first launch
        // ====================================================================
        private void RegisterTrayAutoStart()
        {
            string exe = FindExeInInstallDir("RASD_ClientTray.exe");
            if (exe == null)
            {
                InstallLog("WARN: RASD_ClientTray.exe not found — skipping autostart registration.");
                return;
            }

            // A Scheduled Task with an onlogon trigger, NOT HKCU\...\Run: the Run
            // key only exists in the installing user's hive, so if IT installs and
            // a different user logs in they would never get the tray.
            // /rl limited — this is a read-only status view and must not run elevated.
            try
            {
                string args = string.Format(
                    "/create /tn \"{0}\" /tr \"\\\"{1}\\\" /tray\" /sc onlogon /rl limited /f",
                    TRAY_TASK_NAME, exe);

                int code = RunCapture("schtasks.exe", args, "schtasks create tray task");
                if (code == 0)
                {
                    InstallLog("Autostart registered as Scheduled Task '" + TRAY_TASK_NAME + "' -> " + exe);
                }
                else
                {
                    _warnings.Add("Could not register the endpoint status app to start at login (schtasks exit "
                                  + code + ").");
                    InstallLog("WARN: schtasks create failed (exit " + code + ").");
                }
            }
            catch (Exception ex)
            {
                InstallLog("WARN: could not create the tray scheduled task — " + ex.Message);
                _warnings.Add("Could not register the endpoint status app at login — " + ex.Message);
            }
        }

        /// <summary>Runs a process, captures output, logs the exit code. Returns the exit code.</summary>
        private int RunCapture(string exe, string args, string label)
        {
            var psi = new ProcessStartInfo
            {
                FileName               = exe,
                Arguments              = args,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                if (!p.WaitForExit(20000))
                {
                    InstallLog(label + " TIMED OUT");
                    return -1;
                }
                string flat = output.Replace("\r", " ").Replace("\n", " ").Trim();
                InstallLog(string.Format("{0} -> exit {1} :: {2}", label, p.ExitCode, Trim(flat, 200)));
                return p.ExitCode;
            }
        }

        private void LaunchClientTray()
        {
            try
            {
                string exe = FindExeInInstallDir("RASD_ClientTray.exe");
                if (exe == null) return;
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                InstallLog("Client tray app launched.");
            }
            catch (Exception ex)
            {
                InstallLog("WARN: could not launch client tray — " + ex.Message);
            }
        }

        // Download a file with progress callback (0-100)
        private static void DownloadFile(string url, string destination,
            Action<int> progressCallback)
        {
            using (var wc = new WebClient())
            {
                wc.DownloadProgressChanged += (s, e) =>
                    progressCallback?.Invoke(e.ProgressPercentage);

                // Synchronous download with progress — use Event to block thread
                var done = new System.Threading.ManualResetEventSlim(false);
                Exception downloadError = null;

                wc.DownloadFileCompleted += (s, e) =>
                {
                    if (e.Error != null) downloadError = e.Error;
                    done.Set();
                };

                wc.DownloadFileAsync(new Uri(url), destination);
                done.Wait();

                if (downloadError != null)
                    throw new Exception("Download failed: " + downloadError.Message);
            }
        }
    }
}
