using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace RASD_Setup
{
    // Phase 5 Part A — dependency preflight, elevated auto-install, self-test.
    public partial class MainWindow
    {
        private enum DepState { Present, Missing, Warn }
        private enum DepKind  { WebView2, VcRedist, Node, Postgres, EnvOnly }

        private class Dep
        {
            public string   Name;
            public DepState State;
            public string   Detail;
            public DepKind  Kind;
            public bool     Hard;      // missing Hard dep blocks Continue
            public bool     Installable => Kind != DepKind.EnvOnly && State == DepState.Missing;
        }

        private List<Dep> _deps = new List<Dep>();

        // ================= DETECTION =================
        private List<Dep> DetectDependencies(string role)
        {
            bool admin = role == "ADMIN";
            var d = new List<Dep>();

            d.Add(new Dep { Name = ".NET Framework 4.8+", Kind = DepKind.EnvOnly, Hard = true,
                State = Net48Ok() ? DepState.Present : DepState.Missing,
                Detail = Net48Ok() ? "Release " + Net48Release() : "Release < 528040 — required" });

            d.Add(new Dep { Name = "Visual C++ 2015-2022 x64", Kind = DepKind.VcRedist, Hard = true,
                State = VcRedistOk() ? DepState.Present : DepState.Missing,
                Detail = VcRedistOk() ? "Installed" : "Runtime missing" });

            if (admin)
            {
                bool node = NodePresent();
                d.Add(new Dep { Name = "Node.js LTS", Kind = DepKind.Node, Hard = true,
                    State = node ? DepState.Present : DepState.Missing,
                    Detail = node ? (FindInPath("node.exe") ?? "installed") : "not on PATH — required for the API" });

                bool pg = Pg16Present();
                d.Add(new Dep { Name = "PostgreSQL 16", Kind = DepKind.Postgres, Hard = true,
                    State = pg ? DepState.Present : DepState.Missing,
                    Detail = pg ? "service/dir present" : "not installed — required for the database" });

                bool wv = WebView2Present();
                d.Add(new Dep { Name = "WebView2 Runtime", Kind = DepKind.WebView2, Hard = true,
                    State = wv ? DepState.Present : DepState.Missing,
                    Detail = wv ? WebView2Version() : "missing — required by the dashboard shell" });
            }

            // ---- runtime environment (warnings, not blockers except where noted) ----
            bool eth = EthernetUp();
            d.Add(new Dep { Name = "Wired Ethernet adapter", Kind = DepKind.EnvOnly, Hard = false,
                State = eth ? DepState.Present : DepState.Warn,
                Detail = eth ? "up" : "RASD requires a wired connection (Phase 3 enforcement)" });

            bool os = OsOk();
            d.Add(new Dep { Name = "Windows 10 1803+ / 11", Kind = DepKind.EnvOnly, Hard = true,
                State = os ? DepState.Present : DepState.Missing,
                Detail = os ? "build " + OsBuild() : "build < 17134 — WebView2 unsupported" });

            if (admin)
            {
                var busy = BusyPorts(new[] { 3000, 3001, 3002, 5432 });
                d.Add(new Dep { Name = "Ports 3000/3001/3002/5432", Kind = DepKind.EnvOnly, Hard = false,
                    State = busy.Count == 0 ? DepState.Present : DepState.Warn,
                    Detail = busy.Count == 0 ? "free" : "in use: " + string.Join(", ", busy) });
            }

            long freeGb = FreeDiskGb();
            long need = admin ? 3 : 1;
            d.Add(new Dep { Name = "Disk space (C:)", Kind = DepKind.EnvOnly, Hard = false,
                State = freeGb >= need ? DepState.Present : DepState.Warn,
                Detail = freeGb + " GB free (need ~" + need + " GB)" });

            _deps = d;
            return d;
        }

        private static bool Net48Ok() { return Net48Release() >= 528040; }
        private static int Net48Release()
        {
            try { using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                { return k?.GetValue("Release") is int r ? r : 0; } } catch { return 0; }
        }
        private static bool VcRedistOk()
        {
            try { using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"))
                { return k != null && (k.GetValue("Installed") is int i) && i == 1; } } catch { return false; }
        }
        private static bool NodePresent()
        {
            if (!string.IsNullOrEmpty(FindInPath("node.exe"))) return true;
            return UninstallKeyContains("Node.js");
        }
        private static bool Pg16Present()
        {
            try { if (ServiceExists("postgresql-x64-16")) return true; } catch { }
            return Directory.Exists(@"C:\Program Files\PostgreSQL\16");
        }
        private static bool WebView2Present() { return !string.IsNullOrEmpty(WebView2Version()); }
        private static string WebView2Version()
        {
            const string guid = @"\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    try { using (var k = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"SOFTWARE" + guid))
                        { var pv = k?.GetValue("pv") as string; if (!string.IsNullOrEmpty(pv) && pv != "0.0.0.0") return pv; } }
                    catch { }
            return null;
        }
        private static bool EthernetUp()
        {
            try { return NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                (n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                 n.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet) &&
                !(n.Description ?? "").ToLowerInvariant().Contains("virtual")); } catch { return false; }
        }
        private static bool OsOk() { return OsBuild() >= 17134; }
        private static int OsBuild()
        {
            try { using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                { return int.TryParse(k?.GetValue("CurrentBuild") as string, out int b) ? b : 0; } } catch { return 0; }
        }
        private static List<int> BusyPorts(int[] ports)
        {
            var busy = new List<int>();
            try { var listeners = System.Net.NetworkInformation.IPGlobalProperties
                    .GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToArray();
                foreach (var p in ports) if (listeners.Contains(p)) busy.Add(p); } catch { }
            return busy;
        }
        private static long FreeDiskGb()
        {
            try { return new DriveInfo("C").AvailableFreeSpace / (1024L * 1024L * 1024L); } catch { return 999; }
        }
        private static bool ServiceExists(string name)
        {
            try { using (var sc = new System.ServiceProcess.ServiceController(name)) { var _ = sc.Status; return true; } }
            catch { return false; }
        }
        private static bool UninstallKeyContains(string displayNameFragment)
        {
            string[] roots = { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                               @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
            foreach (var root in roots)
                try { using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(root))
                    { if (k == null) continue; foreach (var sub in k.GetSubKeyNames())
                        using (var s = k.OpenSubKey(sub)) { var dn = s?.GetValue("DisplayName") as string;
                            if (!string.IsNullOrEmpty(dn) && dn.IndexOf(displayNameFragment, StringComparison.OrdinalIgnoreCase) >= 0) return true; } } }
                catch { }
            return false;
        }

        // ================= PREFLIGHT UI =================
        private void ShowPreflight()
        {
            ViewLogin.Visibility     = Visibility.Collapsed;
            ViewPreflight.Visibility = Visibility.Visible;
            RenderPreflight(DetectDependencies(_selectedRole));
        }

        private void RenderPreflight(List<Dep> deps)
        {
            PreflightList.Children.Clear();
            foreach (var dep in deps)
            {
                string glyph = dep.State == DepState.Present ? "✅" : dep.State == DepState.Warn ? "⚠️" : "❌";
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var g = new TextBlock { Text = glyph, FontSize = 13 };
                var n = new TextBlock { Text = dep.Name, FontFamily = new FontFamily("Consolas"), FontSize = 12,
                    Foreground = Brush("#F1F5F9"), VerticalAlignment = VerticalAlignment.Center };
                var det = new TextBlock { Text = dep.Detail, FontFamily = new FontFamily("Consolas"), FontSize = 10,
                    Foreground = Brush(dep.State == DepState.Present ? "#64748B" : dep.State == DepState.Warn ? "#F59E0B" : "#F87171"),
                    VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(n, 1); Grid.SetColumn(det, 2);
                row.Children.Add(g); row.Children.Add(n); row.Children.Add(det);
                PreflightList.Children.Add(row);
            }

            bool hardMissing = deps.Any(x => x.Hard && x.State == DepState.Missing);
            bool anyInstallable = deps.Any(x => x.Installable);
            BtnInstallMissing.IsEnabled = anyInstallable;
            BtnPreflightContinue.IsEnabled = !hardMissing;
            TxtPreflightNote.Text = hardMissing
                ? "Missing required components must be installed before deployment."
                : anyInstallable ? "Optional components can be installed, or continue."
                : "All required components present. Continue.";
            TxtPreflightNote.Foreground = Brush(hardMissing ? "#F87171" : "#64748B");
        }

        private async void BtnInstallMissing_Click(object sender, RoutedEventArgs e)
        {
            BtnInstallMissing.IsEnabled = false;
            BtnPreflightContinue.IsEnabled = false;
            var toInstall = _deps.Where(x => x.Installable).ToList();
            foreach (var dep in toInstall)
            {
                TxtPreflightNote.Text = "Installing " + dep.Name + "…";
                TxtPreflightNote.Foreground = Brush("#00B4D8");
                bool ok = await Task.Run(() => InstallDep(dep));
                InstallLog("Preflight install " + dep.Name + " -> " + (ok ? "OK" : "FAILED"));
            }
            RenderPreflight(DetectDependencies(_selectedRole)); // re-detect to CONFIRM
            var stillMissing = _deps.Where(x => x.Hard && x.State == DepState.Missing).Select(x => x.Name).ToList();
            if (stillMissing.Count > 0)
            {
                TxtPreflightNote.Text = "Still missing: " + string.Join(", ", stillMissing) +
                    ". See install.log for the publisher CN checked and exit codes.";
                TxtPreflightNote.Foreground = Brush("#F87171");
            }
        }

        private void BtnPreflightContinue_Click(object sender, RoutedEventArgs e)
        {
            ViewPreflight.Visibility = Visibility.Collapsed;
            StartDeployment(_selectedRole, null);
        }

        // ================= INSTALL =================
        private bool InstallDep(Dep dep)
        {
            try
            {
                switch (dep.Kind)
                {
                    case DepKind.WebView2: return InstallWebView2();
                    case DepKind.VcRedist: return InstallVia("Microsoft.VCRedist.2015+.x64", null, null, VcRedistOk);
                    case DepKind.Node:
                        return InstallVia("OpenJS.NodeJS.LTS", NODE_INSTALLER_URL,
                            new[] { "OpenJS Foundation", "Node.js" }, NodePresent, msi: true);
                    case DepKind.Postgres:
                        return InstallVia("PostgreSQL.PostgreSQL.16", PG_INSTALLER_URL,
                            new[] { "EnterpriseDB Corporation" }, Pg16Present, pgUnattended: true);
                }
            }
            catch (Exception ex) { InstallLog("InstallDep(" + dep.Name + ") threw — " + ex.Message); }
            return false;
        }

        private bool InstallWebView2()
        {
            // Bundled bootstrapper lives in the embedded payload; extract just it.
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("RASD_Setup.Payload.zip"))
                using (var z = new ZipArchive(s, ZipArchiveMode.Read))
                {
                    var entry = z.GetEntry("assets/MicrosoftEdgeWebview2Setup.exe");
                    if (entry == null) { InstallLog("WebView2 bootstrapper not in payload."); return false; }
                    entry.ExtractToFile(tmp, true);
                }
                int code = RunCapture(tmp, "/silent /install", "WebView2 bootstrapper");
                return WebView2Present();
            }
            catch (Exception ex) { InstallLog("InstallWebView2 threw — " + ex.Message); return false; }
        }

        /// <summary>
        /// winget-primary; on absence/failure, direct-download and verify the vendor
        /// Authenticode signature (publisher CN must match) before executing.
        /// </summary>
        private bool InstallVia(string wingetId, string downloadUrl, string[] publishers,
            Func<bool> verify, bool msi = false, bool pgUnattended = false)
        {
            // 1) winget (Microsoft-curated, hash-verified manifests)
            if (!string.IsNullOrEmpty(FindInPath("winget.exe")))
            {
                int wc = RunCapture("winget.exe",
                    "install --id " + wingetId + " -e --silent --accept-package-agreements --accept-source-agreements",
                    "winget " + wingetId);
                if (verify()) return true;
                InstallLog("winget did not satisfy " + wingetId + " (exit " + wc + ") — trying signed download.");
            }
            else InstallLog("winget not present — using signed download for " + wingetId);

            if (string.IsNullOrEmpty(downloadUrl)) return verify();

            // 2) signed direct download
            string dest = Path.Combine(Path.GetTempPath(), Path.GetFileName(new Uri(downloadUrl).LocalPath));
            try { DownloadFile(downloadUrl, dest, null); }
            catch (Exception ex) { InstallLog("Download failed for " + wingetId + " — " + ex.Message); return false; }

            string cn;
            if (!VerifyAuthenticode(dest, publishers, out cn))
            {
                InstallLog("SECURITY: REJECTED " + Path.GetFileName(dest) +
                    " — Authenticode invalid/unexpected publisher (CN checked: '" + (cn ?? "none") + "', expected: " +
                    string.Join(" | ", publishers) + "). Not executing.");
                return false;
            }
            InstallLog("Authenticode OK for " + Path.GetFileName(dest) + " — publisher CN '" + cn + "'.");

            int code;
            if (msi)               code = RunCapture("msiexec.exe", "/i \"" + dest + "\" /qn /norestart", "Node msi");
            else if (pgUnattended) code = RunCapture(dest, "--mode unattended --unattendedmodeui none " +
                                        "--enable-components server,commandlinetools --install_runtimes 1 " +
                                        "--superpassword postgres --serverport 5432", "Postgres unattended");
            else                   code = RunCapture(dest, "/install /quiet /norestart", "VC redist");
            return verify();
        }

        // ---- Authenticode: WinVerifyTrust chain + publisher CN check ----
        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
            new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO { public uint cbStruct; public IntPtr pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA { public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData;
            public uint dwUIChoice; public uint fdwRevocationChecks; public uint dwUnionChoice; public IntPtr pFile;
            public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference; public uint dwProvFlags;
            public uint dwUIContext; public IntPtr pSignatureSettings; }
        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

        private static bool VerifyAuthenticode(string path, string[] expectedPublishers, out string cn)
        {
            cn = null;
            try
            {
                // 1) chain/trust validity via WinVerifyTrust
                var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
                    pcwszFilePath = Marshal.StringToCoTaskMemUni(path), hFile = IntPtr.Zero, pgKnownSubject = IntPtr.Zero };
                IntPtr pFile = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
                Marshal.StructureToPtr(fileInfo, pFile, false);
                var wd = new WINTRUST_DATA { cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    dwUIChoice = 2 /*WTD_UI_NONE*/, fdwRevocationChecks = 0, dwUnionChoice = 1 /*WTD_CHOICE_FILE*/,
                    pFile = pFile, dwStateAction = 0, dwProvFlags = 0x00000010 /*WTD_SAFER_FLAG*/, dwUIContext = 0 };
                IntPtr pData = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WINTRUST_DATA)));
                Marshal.StructureToPtr(wd, pData, false);
                int result = WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, pData);
                Marshal.FreeCoTaskMem(fileInfo.pcwszFilePath);
                Marshal.FreeCoTaskMem(pFile); Marshal.FreeCoTaskMem(pData);
                if (result != 0) return false; // not trusted / tampered / unsigned

                // 2) publisher CN must match an expected value
                var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                cn = cert.GetNameInfo(X509NameType.SimpleName, false);
                string subject = cert.Subject ?? "";
                foreach (var p in expectedPublishers)
                    if ((cn ?? "").IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        subject.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }
            catch (Exception ex) { InstallLogStatic("VerifyAuthenticode threw — " + ex.Message); return false; }
        }

        private static void InstallLogStatic(string msg)
        {
            try { Directory.CreateDirectory(@"C:\ProgramData\RASD");
                File.AppendAllText(@"C:\ProgramData\RASD\install.log",
                    string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}{2}", DateTime.Now, msg, Environment.NewLine)); } catch { }
        }

        // ================= POST-INSTALL SELF-TEST =================
        // returns null on pass, else an error string.
        private string SelfTest(string role)
        {
            var fail = new List<string>();
            if (role == "ADMIN")
            {
                if (QueryServiceState("postgresql-x64-16") != "RUNNING" && !Pg16Present())
                    fail.Add("PostgreSQL 16 not running");
                if (!WebView2Present()) fail.Add("WebView2 runtime missing");
                if (FindExeInInstallDir("RASD_Shell.exe") == null) fail.Add("dashboard shell (RASD_Shell.exe) not deployed");
                var listeners = new List<int>();
                try { listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                        .GetActiveTcpListeners().Select(x => x.Port).ToList(); } catch { }
                if (!listeners.Contains(3001)) InstallLog("SelfTest WARN: relay port 3001 not yet listening.");
                if (!listeners.Contains(3000)) InstallLog("SelfTest WARN: API port 3000 not yet listening (Node may still be starting).");
            }
            else
            {
                string svc = VerifyServices(); // reuses Phase 2 verification
                if (svc != null) fail.Add(svc);
                if (!EthernetUp()) InstallLog("SelfTest WARN: no wired Ethernet adapter up on this endpoint.");
            }
            InstallLog("SelfTest(" + role + ") -> " + (fail.Count == 0 ? "PASS" : "FAIL: " + string.Join("; ", fail)));
            return fail.Count == 0 ? null : string.Join("\n", fail);
        }
    }
}
