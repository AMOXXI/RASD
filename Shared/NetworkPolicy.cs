using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace RASD.Shared
{
    /// <summary>
    /// Phase 3 — wired-LAN-only enforcement. SINGLE shared source (Phase-4 fix A3):
    /// this file is linked into both RASD_Connector and Admin_Watcher so the two
    /// sides can never silently diverge.
    ///
    /// RASD is only permitted to carry agent/admin traffic over a wired Ethernet
    /// adapter. Wi-Fi, PPP, tunnels, VPNs and virtual adapters are refused even
    /// when they would otherwise route.
    ///
    /// The single escape hatch is C:\ProgramData\RASD\allow_wifi.flag. As of the
    /// security pass it is only honoured when the file is owned by Administrators
    /// or SYSTEM — a standard user can no longer disable the wired-only control by
    /// dropping the flag (privilege-escalation fix A2).
    /// </summary>
    public static class NetworkPolicy
    {
        public const string WIFI_OVERRIDE_FLAG = @"C:\ProgramData\RASD\allow_wifi.flag";

        /// <summary>Name/description substrings that disqualify an adapter regardless of its reported type.</summary>
        private static readonly string[] BLOCKED_PATTERNS =
        {
            "virtual", "vmware", "hyper-v", "hyper v", "vbox", "virtualbox",
            "tap", "tun", "bluetooth", "wi-fi", "wifi", "wireless",
            "wwan", "hotspot", "loopback", "pseudo",
        };

        public class AdapterInfo
        {
            public string  Name        { get; set; }
            public string  Description { get; set; }
            public NetworkInterfaceType Type { get; set; }
            public OperationalStatus    Status { get; set; }
            public IPAddress Ip         { get; set; }
            public IPAddress Mask       { get; set; }
            public bool    Allowed      { get; set; }
            public string  Reason       { get; set; }

            /// <summary>Directed broadcast address (ip | ~mask) for this adapter.</summary>
            public IPAddress DirectedBroadcast
            {
                get
                {
                    try
                    {
                        if (Ip == null || Mask == null) return null;
                        byte[] a = Ip.GetAddressBytes(), m = Mask.GetAddressBytes();
                        if (a.Length != 4 || m.Length != 4) return null;
                        var b = new byte[4];
                        for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | (byte)~m[i]);
                        return new IPAddress(b);
                    }
                    catch { return null; }
                }
            }

            public override string ToString()
            {
                return string.Format("{0} [{1}] status={2} ip={3} -> {4}{5}",
                    Name, Type, Status, Ip == null ? "none" : Ip.ToString(),
                    Allowed ? "ALLOWED" : "BLOCKED",
                    string.IsNullOrEmpty(Reason) ? "" : " (" + Reason + ")");
            }
        }

        /// <summary>
        /// True only if allow_wifi.flag exists AND is owned by Administrators or
        /// SYSTEM. A flag planted by a standard user is ignored; callers should log
        /// the security warning surfaced by <see cref="WifiOverrideStatus"/>.
        /// </summary>
        public static bool WifiOverrideActive()
        {
            string _;
            return WifiOverrideActive(out _);
        }

        public static bool WifiOverrideActive(out string securityNote)
        {
            securityNote = null;
            try
            {
                if (!File.Exists(WIFI_OVERRIDE_FLAG)) return false;

                if (IsOwnedByAdminOrSystem(WIFI_OVERRIDE_FLAG, out string owner))
                    return true;

                securityNote = "SECURITY WARNING: allow_wifi.flag is present but owned by '" +
                               owner + "', not Administrators/SYSTEM. IGNORING it — a standard " +
                               "user cannot be allowed to disable wired-only enforcement.";
                return false;
            }
            catch { return false; }
        }

        /// <summary>Owner check used to reject user-planted security files (A2).</summary>
        public static bool IsOwnedByAdminOrSystem(string path, out string owner)
        {
            owner = "unknown";
            try
            {
                FileSecurity sec = File.GetAccessControl(path);
                IdentityReference id = sec.GetOwner(typeof(SecurityIdentifier));
                var sid = id as SecurityIdentifier;
                if (sid == null) return false;

                try { owner = sid.Translate(typeof(NTAccount)).Value; } catch { owner = sid.Value; }

                return sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                    || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                    || sid.IsWellKnown(WellKnownSidType.AccountAdministratorSid)
                    // Trusted installer / high-integrity service owners are acceptable too.
                    || owner.IndexOf("TrustedInstaller", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>True if this interface type counts as wired Ethernet.</summary>
        public static bool IsWiredType(NetworkInterfaceType t)
        {
            return t == NetworkInterfaceType.Ethernet
                || t == NetworkInterfaceType.GigabitEthernet
                || t == NetworkInterfaceType.FastEthernetT
                || t == NetworkInterfaceType.FastEthernetFx;
        }

        /// <summary>
        /// Full adapter inventory with an ALLOWED/BLOCKED verdict and the reason.
        /// Only IPv4, Up adapters can ever be allowed.
        /// </summary>
        public static List<AdapterInfo> Inventory()
        {
            var list = new List<AdapterInfo>();
            bool overrideOn = WifiOverrideActive();

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    IPAddress ip = null, mask = null;
                    try
                    {
                        foreach (var a in nic.GetIPProperties().UnicastAddresses)
                        {
                            if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            ip   = a.Address;
                            mask = a.IPv4Mask;
                            break;
                        }
                    }
                    catch { }

                    var info = new AdapterInfo
                    {
                        Name        = nic.Name,
                        Description = nic.Description,
                        Type        = nic.NetworkInterfaceType,
                        Status      = nic.OperationalStatus,
                        Ip          = ip,
                        Mask        = mask,
                    };

                    Classify(info, overrideOn);
                    list.Add(info);
                }
            }
            catch { }

            return list;
        }

        private static void Classify(AdapterInfo a, bool overrideOn)
        {
            if (a.Status != OperationalStatus.Up)
            {
                a.Allowed = false;
                a.Reason  = "not up";
                return;
            }

            if (a.Ip == null)
            {
                a.Allowed = false;
                a.Reason  = "no IPv4 address";
                return;
            }

            if (a.Type == NetworkInterfaceType.Loopback)
            {
                // Loopback stays usable only so the admin can self-test locally.
                a.Allowed = IPAddress.IsLoopback(a.Ip);
                a.Reason  = a.Allowed ? "loopback permitted for local self-test" : "loopback";
                return;
            }

            string haystack = ((a.Name ?? "") + " " + (a.Description ?? "")).ToLowerInvariant();
            foreach (string pattern in BLOCKED_PATTERNS)
            {
                if (haystack.Contains(pattern))
                {
                    if (overrideOn)
                    {
                        a.Allowed = true;
                        a.Reason  = "matches '" + pattern + "' but allow_wifi.flag override is active";
                    }
                    else
                    {
                        a.Allowed = false;
                        a.Reason  = "name/description matches '" + pattern + "'";
                    }
                    return;
                }
            }

            if (IsWiredType(a.Type))
            {
                a.Allowed = true;
                a.Reason  = "wired ethernet";
                return;
            }

            if (overrideOn)
            {
                a.Allowed = true;
                a.Reason  = a.Type + " allowed only by allow_wifi.flag override";
                return;
            }

            a.Allowed = false;
            a.Reason  = a.Type + " is not wired ethernet";
        }

        /// <summary>Allowed, routable (non-loopback) adapters — the ones RASD may use.</summary>
        public static List<AdapterInfo> UsableAdapters()
        {
            var usable = new List<AdapterInfo>();
            foreach (var a in Inventory())
            {
                if (!a.Allowed) continue;
                if (a.Ip == null || IPAddress.IsLoopback(a.Ip)) continue;
                usable.Add(a);
            }
            return usable;
        }

        /// <summary>The adapter RASD should bind to, or null when no wired LAN is present.</summary>
        public static AdapterInfo PreferredAdapter()
        {
            var usable = UsableAdapters();
            if (usable.Count == 0) return null;

            // Prefer a genuinely wired adapter over anything the override let through.
            foreach (var a in usable)
                if (IsWiredType(a.Type)) return a;

            return usable[0];
        }

        /// <summary>True if this local IP belongs to an adapter RASD is allowed to use.</summary>
        public static bool IsAllowedLocalIp(IPAddress ip, out string adapterName)
        {
            adapterName = "unknown";
            if (ip == null) return false;

            foreach (var a in Inventory())
            {
                if (a.Ip == null || !a.Ip.Equals(ip)) continue;
                adapterName = string.Format("{0} [{1}]", a.Name, a.Type);
                return a.Allowed;
            }

            // An address we cannot attribute to any adapter is not trusted.
            return false;
        }

        /// <summary>Multi-line inventory report for the startup log.</summary>
        public static string InventoryReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[POLICY] Wired-LAN-only enforcement active. Adapter inventory:");

            var all = Inventory();
            if (all.Count == 0) sb.AppendLine("[POLICY]   (no adapters enumerated)");

            foreach (var a in all)
                sb.AppendLine("[POLICY]   " + a);

            if (WifiOverrideActive(out string note) || note != null)
            {
                if (note != null)
                {
                    sb.AppendLine("[POLICY] " + note);
                }
                else
                {
                    sb.AppendLine("[POLICY] *** WARNING: allow_wifi.flag is present (owner verified) — the");
                    sb.AppendLine("[POLICY] *** wired-only restriction is RELAXED. Non-Ethernet adapters may");
                    sb.AppendLine("[POLICY] *** carry RASD traffic. Remove " + WIFI_OVERRIDE_FLAG);
                    sb.AppendLine("[POLICY] *** to restore enforcement.");
                }
            }

            var pref = PreferredAdapter();
            sb.Append("[POLICY] Selected adapter: " +
                (pref == null ? "NONE — no wired LAN available" : pref.Name + " / " + pref.Ip));

            return sb.ToString();
        }
    }
}
