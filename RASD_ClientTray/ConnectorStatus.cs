using System;
using System.Collections.Generic;
using System.IO;

namespace RASD_ClientTray
{
    /// <summary>
    /// Reads the status surface written by RASD_Connector.
    ///
    /// The connector runs as LocalSystem and this tray runs as the logged-in
    /// user, so they cannot share memory or a socket easily. The connector
    /// writes C:\ProgramData\RASD\status.ini and this class polls it; the tray
    /// requests a reconnect by dropping a flag file the connector consumes.
    /// Read-only by design — the endpoint user gets no admin controls.
    /// </summary>
    public class ConnectorStatus
    {
        public const string CONFIG_DIR     = @"C:\ProgramData\RASD";
        public const string IPC_DIR        = @"C:\ProgramData\RASD\ipc";
        public const string STATUS_FILE    = @"C:\ProgramData\RASD\status.ini";
        // ipc\ is the only user-writable path (parent is Admins/SYSTEM-only, A2).
        public const string RECONNECT_FLAG = @"C:\ProgramData\RASD\ipc\reconnect.flag";
        public const string LOG_FILE       = @"C:\ProgramData\RASD\connector.log";

        public string   State         { get; private set; }   // Connected / Searching / Disconnected
        public string   AdminIp       { get; private set; }
        public string   LastHeartbeat { get; private set; }
        public string   Hostname      { get; private set; }
        public bool     StatusFileFound { get; private set; }
        public DateTime UpdatedUtc    { get; private set; }

        // Phase 3 — wired-LAN enforcement surface
        public string   Adapter       { get; private set; }
        public string   AdapterType   { get; private set; }
        public bool     WiredBlocked  { get; private set; }
        public bool     WifiOverride  { get; private set; }

        public ConnectorStatus()
        {
            State           = "Unknown";
            AdminIp         = "";
            LastHeartbeat   = "";
            Hostname        = Environment.MachineName;
            StatusFileFound = false;
        }

        public static ConnectorStatus Load()
        {
            var s = new ConnectorStatus();
            try
            {
                if (!File.Exists(STATUS_FILE))
                {
                    // No file at all means the connector service has never run.
                    s.State = "Service not running";
                    return s;
                }

                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(STATUS_FILE))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }

                s.StatusFileFound = true;
                s.State         = Get(map, "status",        "Unknown");
                s.AdminIp       = Get(map, "adminIp",       "");
                s.LastHeartbeat = Get(map, "lastHeartbeat", "");
                s.Hostname      = Get(map, "hostname",      Environment.MachineName);
                s.Adapter       = Get(map, "adapter",       "");
                s.AdapterType   = Get(map, "adapterType",   "");
                s.WiredBlocked  = Get(map, "wiredBlocked",  "0") == "1";
                s.WifiOverride  = Get(map, "wifiOverride",  "0") == "1";
                s.UpdatedUtc    = File.GetLastWriteTimeUtc(STATUS_FILE);

                // A file that has stopped being refreshed means the service died
                // without updating its state — report that rather than a stale
                // "Connected" that would mislead the user.
                if ((DateTime.UtcNow - s.UpdatedUtc).TotalMinutes > 5)
                    s.State = "Stale (service may be stopped)";
            }
            catch (Exception ex)
            {
                s.State = "Unreadable: " + ex.Message;
            }
            return s;
        }

        private static string Get(Dictionary<string, string> map, string key, string fallback)
        {
            string v;
            return map.TryGetValue(key, out v) && !string.IsNullOrEmpty(v) ? v : fallback;
        }

        /// <summary>Asks the connector to drop its socket and re-run LAN discovery.</summary>
        public static bool RequestReconnect(out string error)
        {
            error = null;
            try
            {
                // ipc\ is created (user-writable) by the installer; create it here too
                // in case the tray runs before/without a full install.
                if (!Directory.Exists(IPC_DIR)) Directory.CreateDirectory(IPC_DIR);
                File.WriteAllText(RECONNECT_FLAG,
                    "requested=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool LogExists()
        {
            try { return File.Exists(LOG_FILE); } catch { return false; }
        }
    }
}
