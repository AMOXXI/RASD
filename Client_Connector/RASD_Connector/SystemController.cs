using System;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace RASD_Connector
{
    /// <summary>
    /// Executes native system commands (Restart, USB Lock/Unlock).
    /// </summary>
    public static class SystemController
    {
        public static void ExecuteCommand(string command)
        {
            try
            {
                switch (command)
                {
                    case "RESTART_NODE":
                    case "RESTART":
                        ForceRestart();
                        break;

                    case "SHUTDOWN_NODE":
                    case "SHUTDOWN":
                        ForceShutdown();
                        break;

                    case "UPDATE_WINDOWS":
                        RunWindowsUpdate();
                        break;

                    case "BLOCK_USB":
                        SetUsbStatus(false);
                        break;

                    case "ALLOW_USB":
                        SetUsbStatus(true);
                        break;
                }
            }
            catch (Exception)
            {
                // Silent catch — service must never crash
            }
        }

        private static void ForceRestart()
        {
            var psi = new ProcessStartInfo("shutdown", "/r /f /t 0")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi);
        }

        private static void ForceShutdown()
        {
            var psi = new ProcessStartInfo("shutdown", "/s /f /t 0")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi);
        }

        private static void RunWindowsUpdate()
        {
            // Ensure the PSWindowsUpdate module is present, then install all
            // available updates and auto-reboot. Runs as the LocalSystem service,
            // so it is already elevated (no -Verb RunAs needed).
            string script =
                "$ErrorActionPreference='SilentlyContinue';" +
                "if (-not (Get-Module -ListAvailable -Name PSWindowsUpdate)) {" +
                "Install-PackageProvider -Name NuGet -Force -Scope AllUsers;" +
                "Set-PSRepository -Name PSGallery -InstallationPolicy Trusted;" +
                "Install-Module -Name PSWindowsUpdate -Force -Scope AllUsers;" +
                "}" +
                "Import-Module PSWindowsUpdate;" +
                "Install-WindowsUpdate -AcceptAll -AutoReboot;";

            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"" + script + "\"")
            {
                CreateNoWindow  = true,
                UseShellExecute = false
            };
            Process.Start(psi);
        }

        private const string USBSTOR_KEY = @"SYSTEM\CurrentControlSet\Services\USBSTOR";
        private const string REMOVABLE_POLICY_KEY =
            @"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}";

        private static void SetUsbStatus(bool enabled)
        {
            if (enabled)
            {
                // ALLOW — reverse all layers. Unlock the key BEFORE writing to it.
                SetWpdBusEnum(blocked: false);
                SetRemovablePolicy(denyWrite: false);
                SetUsbStorKeyLock(locked: false);
                ApplyUsbStorStart(3); // 3 = Manual (Enabled)
            }
            else
            {
                // BLOCK — apply all layers. Write the value BEFORE locking the key.
                ApplyUsbStorStart(4); // 4 = Disabled
                SetUsbStorKeyLock(locked: true);
                SetRemovablePolicy(denyWrite: true);
                SetWpdBusEnum(blocked: true);
            }
        }

        // ── Layer 1: USBSTOR service Start value ──────────────────────────
        private static void ApplyUsbStorStart(int val)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(USBSTOR_KEY, true))
                {
                    if (key != null)
                        key.SetValue("Start", val, RegistryValueKind.DWord);
                }
            }
            catch { /* requires Admin — silent */ }
        }

        // ── Layer 2: Deny SetValue on USBSTOR so PnP/WPDBusEnum can't rewrite Start ──
        private static void SetUsbStorKeyLock(bool locked)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    USBSTOR_KEY,
                    RegistryKeyPermissionCheck.ReadWriteSubTree,
                    RegistryRights.ChangePermissions | RegistryRights.ReadPermissions))
                {
                    if (key == null) return;

                    RegistrySecurity sec = key.GetAccessControl();
                    var denyUsers = new RegistryAccessRule(
                        new NTAccount("BUILTIN", "Users"),
                        RegistryRights.SetValue, AccessControlType.Deny);
                    var denySystem = new RegistryAccessRule(
                        new NTAccount("NT AUTHORITY", "SYSTEM"),
                        RegistryRights.SetValue, AccessControlType.Deny);

                    if (locked)
                    {
                        sec.AddAccessRule(denyUsers);
                        sec.AddAccessRule(denySystem);
                    }
                    else
                    {
                        sec.RemoveAccessRule(denyUsers);
                        sec.RemoveAccessRule(denySystem);
                    }
                    key.SetAccessControl(sec);
                }
            }
            catch { /* silent */ }
        }

        // ── Layer 3: RemovableStorageDevices Group Policy (write-deny) ─────
        private static void SetRemovablePolicy(bool denyWrite)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(REMOVABLE_POLICY_KEY))
                {
                    if (key == null) return;
                    key.SetValue("Deny_Read",  0, RegistryValueKind.DWord);
                    key.SetValue("Deny_Write", denyWrite ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch { /* silent */ }
        }

        // ── Layer 4: WPDBusEnum service enable/disable ────────────────────
        private static void SetWpdBusEnum(bool blocked)
        {
            if (blocked)
            {
                RunSilent("sc.exe",  "config WPDBusEnum start= disabled");
                RunSilent("net.exe", "stop WPDBusEnum /y");
            }
            else
            {
                RunSilent("sc.exe",  "config WPDBusEnum start= demand");
                RunSilent("net.exe", "start WPDBusEnum");
            }
        }

        private static void RunSilent(string exe, string args)
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch { /* silent */ }
        }

        /// <summary>
        /// Reads the current USBSTOR registry value to determine USB status.
        /// Returns "ALLOWED" or "BLOCKED".
        /// </summary>
        public static string GetCurrentUsbStatus()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\USBSTOR", false))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("Start");
                        if (val != null && (int)val == 4)
                            return "BLOCKED";
                    }
                }
            }
            catch { }
            return "ALLOWED";
        }
    }
}
