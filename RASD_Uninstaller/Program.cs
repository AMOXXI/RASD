using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace RASD_Uninstaller
{
    class Program
    {
        private const string INSTALL_DIR = @"C:\Program Files\RASD";
        private const string CONFIG_DIR  = @"C:\ProgramData\RASD";

        private const string RUN_KEY = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        // The installer registers the ARP entry under RASD_Operations_Ecosystem;
        // also try a plain "RASD" key for safety.
        private const string ARP_KEY_REAL = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RASD_Operations_Ecosystem";
        private const string ARP_KEY_ALT  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RASD";

        private const string PG_DIR   = @"C:\Program Files\PostgreSQL";
        private const string PG16_DIR = @"C:\Program Files\PostgreSQL\16";
        private const string NODE_DIR = @"C:\Program Files\nodejs";

        static void Main(string[] args)
        {
            bool silent = args.Length > 0 &&
                          args[0].Equals("/silent", StringComparison.OrdinalIgnoreCase);

            // ---- Ensure we are running elevated ----
            if (!IsAdministrator())
            {
                RelaunchAsAdmin(args);
                return;
            }

            if (!silent)
            {
                Console.WriteLine("╔══════════════════════════════════════════╗");
                Console.WriteLine("║      RASD ENTERPRISE — UNINSTALLER       ║");
                Console.WriteLine("╚══════════════════════════════════════════╝");
                Console.WriteLine();
            }

            // ---- Uninstall type ----
            bool full = false; // default: RASD only
            if (!silent)
            {
                Console.WriteLine("Select uninstall type:");
                Console.WriteLine("  [1] Full Clean Uninstall — removes RASD, PostgreSQL 16, Node.js, and all related files");
                Console.WriteLine("  [2] RASD Only — removes RASD files and services, keeps PostgreSQL and Node.js");
                Console.Write("Enter choice (1 or 2): ");
                string choice = Console.ReadLine();
                full = choice != null && choice.Trim() == "1";

                Console.WriteLine();
                Console.Write("This will remove RASD from your system. Continue? [Y/N]: ");
                string answer = Console.ReadLine();
                if (answer == null || !answer.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Uninstall cancelled.");
                    return;
                }
            }

            int errors = 0;
            Console.WriteLine();

            // ================= COMMON STEPS =================
            Log("Stopping and deleting RASD services...");
            errors += StopAndDeleteService("RASD_Enterprise_Connector");
            errors += StopAndDeleteService("RASD_Enterprise_Protector");
            errors += StopAndDeleteService("RASD_Connector");
            errors += StopAndDeleteService("RASD_Protector");

            Log("Terminating RASD processes...");
            KillProcess("RASD.exe");
            KillProcess("RASD_Relay.exe");
            KillProcess("RASD_Connector.exe");
            KillProcess("RASD_Protector.exe");
            KillProcess("RASD_ClientTray.exe");
            KillProcess("RASD_Shell.exe");
            Thread.Sleep(1500);

            Log("Removing application files...");
            errors += RemoveDirectory(INSTALL_DIR);

            Log("Removing ProgramData config...");
            errors += RemoveDirectory(CONFIG_DIR);

            Log("Removing startup entry...");
            RemoveRunValue();

            Log("Removing scheduled tasks...");
            RemoveScheduledTasks();

            Log("Removing Windows installed-apps entry...");
            RemoveArpKeys();

            Log("Removing firewall rules...");
            RemoveFirewallRules();

            // ================= FULL CLEAN ONLY =================
            if (full)
            {
                Log("Uninstalling PostgreSQL 16...");
                UninstallPostgres();

                Log("Uninstalling Node.js...");
                UninstallNode();
            }

            Log("Verifying clean removal...");
            int residue = VerifyClean();

            Console.WriteLine();
            if (residue == 0)
                Console.WriteLine("RASD has been successfully uninstalled. No residue detected.");
            else
                Console.WriteLine("RASD uninstalled with " + residue + " leftover item(s) — see the list above.");
            if (errors > 0)
                Console.WriteLine("  (" + errors + " item(s) may require manual cleanup.)");

            if (!silent)
            {
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
            }
        }

        // ================================================================
        // ELEVATION
        // ================================================================
        private static bool IsAdministrator()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static void RelaunchAsAdmin(string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName        = Process.GetCurrentProcess().MainModule.FileName,
                    Arguments       = string.Join(" ", args),
                    UseShellExecute = true,
                    Verb            = "runas",
                };
                Process.Start(psi);
            }
            catch
            {
                Console.WriteLine("Administrator privileges are required to uninstall RASD.");
                Console.WriteLine("Please right-click and 'Run as administrator'.");
                Thread.Sleep(2500);
            }
        }

        // ================================================================
        // HELPERS
        // ================================================================
        private static void Log(string msg)
        {
            Console.WriteLine("  → " + msg);
        }

        private static int StopAndDeleteService(string serviceName)
        {
            try
            {
                Run("sc.exe", "stop \"" + serviceName + "\"", ignoreExit: true);
                Thread.Sleep(1200);
                Run("sc.exe", "delete \"" + serviceName + "\"", ignoreExit: true);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("    WARN: Could not remove service " + serviceName + ": " + ex.Message);
                return 1;
            }
        }

        private static void KillProcess(string imageName)
        {
            try { Run("taskkill.exe", "/F /IM \"" + imageName + "\"", ignoreExit: true); }
            catch { }
        }

        private static int RemoveDirectory(string dirPath)
        {
            try
            {
                if (!Directory.Exists(dirPath)) return 0;

                // Force-remove even locked/read-only files via cmd
                Run("cmd.exe", "/c rd /s /q \"" + dirPath + "\"", ignoreExit: true, timeoutMs: 60000);
                Thread.Sleep(800);

                if (Directory.Exists(dirPath))
                    Directory.Delete(dirPath, recursive: true); // .NET fallback

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("    WARN: Could not fully remove " + dirPath + ": " + ex.Message);
                return 1;
            }
        }

        private static void RemoveRunValue()
        {
            // Installer writes the Run entries under HKCU; also clear HKLM just in case.
            foreach (var name in new[] { "RASD", "RASD_ClientTray" })
            {
                try { using (var k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true)) k?.DeleteValue(name, false); } catch { }
                try { using (var k = Registry.LocalMachine.OpenSubKey(RUN_KEY, true)) k?.DeleteValue(name, false); } catch { }
            }
        }

        private static void RemoveArpKeys()
        {
            try { Registry.LocalMachine.DeleteSubKeyTree(ARP_KEY_REAL, throwOnMissingSubKey: false); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(ARP_KEY_ALT,  throwOnMissingSubKey: false); } catch { }
        }

        private static void RemoveScheduledTasks()
        {
            // Created by the installer for the endpoint tray app (onlogon trigger).
            foreach (var task in new[] { "RASD Endpoint Tray" })
            {
                try { Run("schtasks.exe", "/delete /tn \"" + task + "\" /f", ignoreExit: true); } catch { }
            }
        }

        private static void RemoveFirewallRules()
        {
            // Every rule name the installer can create — leaving orphaned rules
            // behind after an uninstall is bad practice.
            var names = new[]
            {
                "RASD", "RASD_Relay", "RASD_Connector", "RASD API",
                "RASD Relay TCP In",
                "RASD API TCP In",
                "RASD Discovery UDP In",
                "RASD Relay Program In",
                "RASD Connector UDP In",
                "RASD Connector Program In",
            };

            foreach (var name in names)
            {
                try { Run("netsh.exe", "advfirewall firewall delete rule name=\"" + name + "\"", ignoreExit: true); }
                catch { }
            }
        }

        // ---- PostgreSQL ----
        private static void UninstallPostgres()
        {
            try
            {
                string uninstaller = Path.Combine(PG16_DIR, "uninstall-postgresql.exe");
                if (File.Exists(uninstaller))
                {
                    Run(uninstaller, "--mode unattended", ignoreExit: true, timeoutMs: 300000);
                    Thread.Sleep(2000);
                }

                // If the uninstaller was absent or left files behind, remove the tree.
                if (Directory.Exists(PG_DIR))
                    RemoveDirectory(PG_DIR);
            }
            catch (Exception ex)
            {
                Console.WriteLine("    WARN: PostgreSQL removal issue: " + ex.Message);
            }

            try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\PostgreSQL", false); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\WOW6432Node\PostgreSQL", false); } catch { }
        }

        // ---- Node.js ----
        private static void UninstallNode()
        {
            try
            {
                string uninstallString = FindNodeUninstallString();
                if (uninstallString != null)
                {
                    if (uninstallString.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // Node.js ships as an MSI: "MsiExec.exe /X{GUID}"
                        string guid = ExtractGuid(uninstallString);
                        if (guid != null)
                            Run("msiexec.exe", "/x " + guid + " /qn /norestart", ignoreExit: true, timeoutMs: 300000);
                    }
                    else
                    {
                        // EXE-style uninstaller — silent switch
                        Run("cmd.exe", "/c \"" + uninstallString + "\" /S", ignoreExit: true, timeoutMs: 300000);
                    }
                    Thread.Sleep(2000);
                }

                if (Directory.Exists(NODE_DIR))
                    RemoveDirectory(NODE_DIR);
            }
            catch (Exception ex)
            {
                Console.WriteLine("    WARN: Node.js removal issue: " + ex.Message);
            }
        }

        private static string FindNodeUninstallString()
        {
            string[] roots =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };
            foreach (var root in roots)
            {
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(root))
                    {
                        if (key == null) continue;
                        foreach (var sub in key.GetSubKeyNames())
                        {
                            using (var app = key.OpenSubKey(sub))
                            {
                                string name = app?.GetValue("DisplayName") as string;
                                if (name != null && name.IndexOf("Node.js", StringComparison.OrdinalIgnoreCase) >= 0)
                                    return (app.GetValue("QuietUninstallString") as string)
                                        ?? (app.GetValue("UninstallString") as string);
                            }
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static string ExtractGuid(string s)
        {
            int open = s.IndexOf('{');
            int close = open >= 0 ? s.IndexOf('}', open + 1) : -1;
            return (open >= 0 && close > open) ? s.Substring(open, close - open + 1) : null;
        }

        // ---- residue verification (programmatic) ----
        private static int VerifyClean()
        {
            int residue = 0;
            void Check(bool present, string label)
            {
                if (present) { residue++; Console.WriteLine("  RESIDUE: " + label); }
            }

            foreach (var svc in new[] { "RASD_Enterprise_Connector", "RASD_Enterprise_Protector" })
                Check(Capture("sc.exe", "query \"" + svc + "\"").IndexOf("1060", StringComparison.Ordinal) < 0, "service " + svc);

            Check(Capture("schtasks.exe", "/query /tn \"RASD Endpoint Tray\"").IndexOf("RASD Endpoint Tray", StringComparison.OrdinalIgnoreCase) >= 0,
                "scheduled task 'RASD Endpoint Tray'");

            foreach (var rule in new[] { "RASD Relay TCP In", "RASD API TCP In", "RASD Discovery UDP In",
                "RASD Relay Program In", "RASD Connector UDP In", "RASD Connector Program In" })
                Check(Capture("netsh.exe", "advfirewall firewall show rule name=\"" + rule + "\"")
                        .IndexOf("No rules match", StringComparison.OrdinalIgnoreCase) < 0, "firewall rule '" + rule + "'");

            Check(File.Exists(@"C:\ProgramData\RASD\node.key"), "node.key (shared secret!)");
            Check(Directory.Exists(CONFIG_DIR), "ProgramData folder " + CONFIG_DIR);
            Check(Directory.Exists(INSTALL_DIR), "install folder " + INSTALL_DIR);
            return residue;
        }

        private static string Capture(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(10000);
                    return o;
                }
            }
            catch { return ""; }
        }

        // ---- Process runner ----
        private static void Run(string exe, string args, bool ignoreExit, int timeoutMs = 15000)
        {
            var psi = new ProcessStartInfo
            {
                FileName        = exe,
                Arguments       = args,
                UseShellExecute = false,
                CreateNoWindow  = true,
            };
            using (var p = Process.Start(psi))
                p.WaitForExit(timeoutMs);
        }
    }
}
