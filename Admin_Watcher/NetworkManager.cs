using System;
using RASD.Shared;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace RASD_AdminWatcher
{
    /// <summary>
    /// NetworkManager — Continuously monitors the Admin's LAN IPv4 address.
    /// If the IP changes, it auto-corrects the API .env and restarts Node.js.
    /// Also handles starting and stopping the Node.js API server process.
    /// </summary>
    public class NetworkManager
    {
        // ----------------------------------------------------------------
        // CONFIGURATION
        // ----------------------------------------------------------------
        private const int   POLL_INTERVAL_MS   = 5000;    // check IP every 5 seconds
        private const string API_DIR           = @"API_Server";
        private const string NODE_ENTRY        = @"server.js";
        private const string NODE_CACHE_FILE   = @"C:\ProgramData\RASD\node_path.txt";

        // Relative path from the executable — adjust to match actual layout
        // In dev: exe is in Admin_Watcher/bin/Release/ → go up 3 levels to RASD/
        private static readonly string ROOT_DIR = ResolveRootDir();
        private static string _cachedNodePath = null;

        // ----------------------------------------------------------------
        // STATE
        // ----------------------------------------------------------------
        private readonly Action<string>       _log;
        private readonly Action<string>       _updateIpCard;

        private CancellationTokenSource _cts;
        private string   _lastKnownIp  = "";
        private Process  _nodeProcess;
        private readonly object _nodeLock = new object();

        // Answers "RASD_DISCOVER" probes so endpoints need no hardcoded admin IP.
        private DiscoveryResponder _discovery;

        // ----------------------------------------------------------------
        // CONSTRUCTOR
        // ----------------------------------------------------------------
        public NetworkManager(Action<string> log, Action<string> updateIpCard)
        {
            _log          = log;
            _updateIpCard = updateIpCard;
        }

        // ----------------------------------------------------------------
        // PUBLIC INTERFACE
        // ----------------------------------------------------------------
        public void Start()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => MonitorLoop(_cts.Token));
            Task.Run(() => UdpBroadcastLoop(_cts.Token));
            Task.Run(() => RegisterAdminWhenReady(_cts.Token));

            // Wired-LAN-only policy: log the inventory and never advertise a
            // non-Ethernet address to agents.
            _log(NetworkPolicy.InventoryReport());

            // Answer agent discovery probes immediately rather than making them
            // wait for the next 30s announcement.
            _discovery = new DiscoveryResponder(_log, ResolveAdvertisedIp);
            _discovery.Start();

            _log("NetworkManager: IP monitor + UDP broadcaster + discovery responder started.");
            Task.Run(() => RestartNodeApi());
        }

        public void Stop()
        {
            if (_cts != null) _cts.Cancel();
            if (_discovery != null) _discovery.Stop();
            _log("NetworkManager: IP monitor stopped.");
        }

        public string CurrentIp { get { return _lastKnownIp; } }

        /// <summary>True when no wired Ethernet adapter is available to serve agents.</summary>
        public bool WiredLanMissing
        {
            get { return NetworkPolicy.PreferredAdapter() == null; }
        }

        /// <summary>
        /// The only address we are willing to advertise to agents. Returns null when
        /// no wired adapter is up, so the responder stays silent rather than handing
        /// out a Wi-Fi IP that policy forbids agents from using.
        /// </summary>
        private string ResolveAdvertisedIp()
        {
            var wired = NetworkPolicy.PreferredAdapter();
            if (wired != null && wired.Ip != null) return wired.Ip.ToString();

            _log("WARN: [POLICY] Discovery probe cannot be answered — no wired Ethernet adapter is up. " +
                 "Refusing to advertise a Wi-Fi address.");
            return null;
        }

        // ----------------------------------------------------------------
        // MONITOR LOOP
        // ----------------------------------------------------------------
        private async Task MonitorLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string currentIp = GetLanIp();

                    if (!string.IsNullOrEmpty(currentIp) && currentIp != _lastKnownIp)
                    {
                        if (!string.IsNullOrEmpty(_lastKnownIp))
                        {
                            // IP CHANGED — self-healing protocol
                            _log(string.Format("WARN: IP CHANGE DETECTED  {0}  →  {1}", _lastKnownIp, currentIp));
                            _log("Auto-correction: Patching API .env and restarting Node.js...");
                            PatchEnvFile(currentIp);
                            if (_nodeProcess == null || _nodeProcess.HasExited) RestartNodeApi();
                        }

                        _lastKnownIp = currentIp;
                        _updateIpCard(currentIp);
                        _log(string.Format("LAN IP confirmed: {0}", currentIp));
                    }
                }
                catch (Exception ex)
                {
                    _log(string.Format("ERROR: NetworkManager.MonitorLoop — {0}", ex.Message));
                }

                await Task.Delay(POLL_INTERVAL_MS, ct).ConfigureAwait(false);
            }
        }

        // ----------------------------------------------------------------
        // UDP BROADCAST — Announces the wired admin IP every 30s on port 3002.
        // A1: the announcement is HMAC-signed (RASD_ADMIN|ip|nonce|mac) so agents
        // reject a spoofed IP. Only the wired Ethernet IP is ever advertised.
        // ----------------------------------------------------------------
        private async Task UdpBroadcastLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string ip  = ResolveAdvertisedIp();      // wired IP or null
                    byte[] key = DiscoverySecurity.LoadKey();

                    if (!string.IsNullOrEmpty(ip) && ip != "127.0.0.1" && key != null)
                    {
                        string msg = DiscoverySecurity.BuildReply(key, ip, DiscoverySecurity.NewNonce());
                        using (var udp = new UdpClient())
                        {
                            udp.EnableBroadcast = true;
                            byte[] data = Encoding.UTF8.GetBytes(msg);
                            udp.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, 3002));
                        }
                    }
                    else if (key == null)
                    {
                        _log("WARN: [SECURITY] node.key missing — cannot broadcast a signed announcement.");
                    }
                }
                catch { /* Non-fatal — broadcast is best-effort */ }

                await Task.Delay(30000, ct).ConfigureAwait(false);
            }
        }

        // ----------------------------------------------------------------
        // ACTIVE SUBNET SCAN — TCP-probe every host in the local /24 on
        // port 3001. Complements UDP broadcast discovery for VLANs and
        // networks where broadcasts are dropped.
        // ----------------------------------------------------------------
        private const int AGENT_TCP_PORT   = 3001;
        private const int SCAN_TIMEOUT_MS  = 500;
        private const int SCAN_CONCURRENCY = 20;

        public async Task<List<string>> ScanSubnetAsync()
        {
            string localIp = GetLanIp();
            if (string.IsNullOrEmpty(localIp) || localIp == "127.0.0.1")
                return new List<string>();

            // Derive the /24 base, e.g. "192.168.1."
            int lastDot = localIp.LastIndexOf('.');
            if (lastDot < 0) return new List<string>();
            string prefix = localIp.Substring(0, lastDot + 1);

            var discovered = new ConcurrentBag<string>();

            using (var throttle = new SemaphoreSlim(SCAN_CONCURRENCY))
            {
                var tasks = new List<Task>();
                for (int host = 1; host <= 254; host++)
                {
                    string ip = prefix + host;
                    if (ip == localIp) continue; // skip the admin's own IP

                    await throttle.WaitAsync().ConfigureAwait(false);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            if (await ProbeTcpAsync(ip, AGENT_TCP_PORT, SCAN_TIMEOUT_MS).ConfigureAwait(false))
                                discovered.Add(ip);
                        }
                        finally { throttle.Release(); }
                    }));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            return discovered.OrderBy(ip =>
            {
                // numeric sort by last octet
                int d = ip.LastIndexOf('.');
                int n; return int.TryParse(ip.Substring(d + 1), out n) ? n : 0;
            }).ToList();
        }

        private static async Task<bool> ProbeTcpAsync(string host, int port, int timeoutMs)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connectTask = client.ConnectAsync(host, port);
                    bool ok = await Task.WhenAny(connectTask, Task.Delay(timeoutMs))
                                        .ConfigureAwait(false) == connectTask
                              && !connectTask.IsFaulted
                              && client.Connected;
                    return ok;
                }
            }
            catch { return false; }
        }

        public void StartSubnetScan()
        {
            Task.Run(async () =>
            {
                try
                {
                    string localIp = GetLanIp();
                    if (string.IsNullOrEmpty(localIp) || localIp == "127.0.0.1")
                    {
                        _log("WARN: Subnet scan skipped — no valid LAN IP.");
                        return;
                    }

                    string prefix = localIp.Substring(0, localIp.LastIndexOf('.') + 1);
                    _log(string.Format("Subnet scan started on {0}0/24 (port {1})...", prefix, AGENT_TCP_PORT));

                    var found = await ScanSubnetAsync().ConfigureAwait(false);

                    if (found.Count == 0)
                    {
                        _log("Subnet scan complete: no agents responding on port 3001.");
                    }
                    else
                    {
                        foreach (var ip in found)
                            _log(string.Format("OK: Subnet scan found agent at {0}:{1}", ip, AGENT_TCP_PORT));
                        _log(string.Format("OK: Subnet scan complete — {0} host(s) responding.", found.Count));
                    }
                }
                catch (Exception ex)
                {
                    _log(string.Format("ERROR: Subnet scan failed — {0}", ex.Message));
                }
            });
        }

        // ----------------------------------------------------------------
        // .ENV PATCH — Updates the API_HOST / HOST entry with the new IP
        // ----------------------------------------------------------------
        private void PatchEnvFile(string newIp)
        {
            try
            {
                string envPath = Path.Combine(ROOT_DIR, API_DIR, ".env");
                if (!File.Exists(envPath))
                {
                    _log(string.Format("WARN: .env not found at {0} — skipping patch.", envPath));
                    return;
                }

                string content = File.ReadAllText(envPath);

                // Replace or append API_HOST
                if (Regex.IsMatch(content, @"^API_HOST=.*$", RegexOptions.Multiline))
                {
                    content = Regex.Replace(content, @"^API_HOST=.*$", string.Format("API_HOST={0}", newIp),
                                            RegexOptions.Multiline);
                }
                else
                {
                    content += string.Format("\r\nAPI_HOST={0}", newIp);
                }

                File.WriteAllText(envPath, content);
                _log(string.Format("OK: .env patched — API_HOST={0}", newIp));
            }
            catch (Exception ex)
            {
                _log(string.Format("ERROR: Failed to patch .env — {0}", ex.Message));
            }
        }

        // ----------------------------------------------------------------
        // NODE.JS API RESTART
        // ----------------------------------------------------------------
        public void RestartNodeApi()
        {
            lock (_nodeLock)
            {
                // Kill existing process if alive
                try
                {
                    if (_nodeProcess != null && !_nodeProcess.HasExited)
                    {
                        _nodeProcess.Kill();
                        _nodeProcess.WaitForExit(3000);
                        _log("WARN: Previous Node.js process terminated.");
                    }
                }
                catch (Exception ex)
                {
                    _log(string.Format("WARN: Could not kill old Node process — {0}", ex.Message));
                }

                // Also kill any orphaned node.exe processes running our server
                KillOrphanedNode();

                // Start fresh
                try
                {
                    string serverPath = Path.Combine(ROOT_DIR, API_DIR, NODE_ENTRY);
                    string workDir    = Path.Combine(ROOT_DIR, API_DIR);

                    if (!File.Exists(serverPath))
                    {
                        _log(string.Format("ERROR: server.js not found at {0}", serverPath));
                        return;
                    }

                    string nodeExe = ResolveNodeExe();
                    if (nodeExe == null)
                    {
                        _log("Please install Node.js from https://nodejs.org, then restart RASD.");
                        return;
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName         = nodeExe,
                        Arguments        = string.Format("\"{0}\"", serverPath),
                        WorkingDirectory = workDir,
                        UseShellExecute  = false,
                        CreateNoWindow   = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                    };

                    _nodeProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    _nodeProcess.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) _log(string.Format("[NODE] {0}", e.Data)); };
                    _nodeProcess.ErrorDataReceived  += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) _log(string.Format("[NODE][ERR] {0}", e.Data)); };
                    _nodeProcess.Exited += (s, e) => _log("WARN: Node.js process exited unexpectedly.");

                    _nodeProcess.Start();
                    _nodeProcess.BeginOutputReadLine();
                    _nodeProcess.BeginErrorReadLine();

                    _log(string.Format("OK: Node.js API started (PID {0}) — {1}", _nodeProcess.Id, serverPath));
                }
                catch (Exception ex)
                {
                    _log(string.Format("ERROR: Failed to start Node.js — {0}", ex.Message));
                    _log("Ensure Node.js is installed (https://nodejs.org).");
                }
            }
        }

        private void KillOrphanedNode()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("node"))
                {
                    try
                    {
                        // Only kill if cmdline references our server.js
                        p.Kill();
                        p.Dispose();
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ----------------------------------------------------------------
        // UNIVERSAL NODE.JS DISCOVERY — works on any machine, cached after
        // first success so subsequent launches are instant.
        // ----------------------------------------------------------------
        private string ResolveNodeExe()
        {
            // In-memory cache
            if (!string.IsNullOrEmpty(_cachedNodePath) && File.Exists(_cachedNodePath))
                return _cachedNodePath;

            // Persisted cache from a previous launch
            try
            {
                if (File.Exists(NODE_CACHE_FILE))
                {
                    string cached = File.ReadAllText(NODE_CACHE_FILE).Trim();
                    if (!string.IsNullOrEmpty(cached) && File.Exists(cached))
                    {
                        _cachedNodePath = cached;
                        return cached;
                    }
                }
            }
            catch { }

            string found = FindInPath("node.exe");                        // 1) PATH

            if (found == null)                                            // 2-4) known dirs
            {
                string[] known =
                {
                    @"C:\Program Files\nodejs\node.exe",
                    @"C:\Program Files (x86)\nodejs\node.exe",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node.exe"),
                };
                foreach (var p in known) if (File.Exists(p)) { found = p; break; }
            }

            if (found == null) found = FindNodeInRegistry();              // 5-6) registry
            if (found == null) found = ScanProgramFilesForNode();         // 7) Program Files scan

            if (found != null)
            {
                _cachedNodePath = found;
                TrySaveNodePath(found);
                _log(string.Format("OK: Node.js found at {0}", found));
            }
            else
            {
                _log("ERROR: Node.js not found — please install Node.js from nodejs.org");
            }
            return found;
        }

        private static string FindInPath(string exe)
        {
            try
            {
                string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathVar.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string full = Path.Combine(dir.Trim(), exe);
                    if (File.Exists(full)) return full;
                }
            }
            catch { }
            return null;
        }

        private static string FindNodeInRegistry()
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using (var key = hive.OpenSubKey(@"SOFTWARE\Node.js"))
                    {
                        string installPath = key?.GetValue("InstallPath") as string;
                        if (!string.IsNullOrEmpty(installPath))
                        {
                            string exe = Path.Combine(installPath, "node.exe");
                            if (File.Exists(exe)) return exe;
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static string ScanProgramFilesForNode()
        {
            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            })
            {
                try
                {
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                    foreach (var dir in Directory.GetDirectories(root, "node*"))
                    {
                        string exe = Path.Combine(dir, "node.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
                catch { }
            }
            return null;
        }

        private static void TrySaveNodePath(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(NODE_CACHE_FILE);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(NODE_CACHE_FILE, path);
            }
            catch { }
        }

        // ----------------------------------------------------------------
        // ADMIN SELF-REGISTRATION — once the local API is up, register this
        // machine so it appears in the Endpoints/Overview lists as [Admin].
        // ----------------------------------------------------------------
        private async Task RegisterAdminWhenReady(CancellationToken ct)
        {
            // Wait for the API to answer (up to ~2 min), then register once.
            for (int i = 0; i < 60 && !ct.IsCancellationRequested; i++)
            {
                if (IsApiUp())
                {
                    RegisterAdminMachine();
                    return;
                }
                try { await Task.Delay(2000, ct).ConfigureAwait(false); }
                catch { return; }
            }
        }

        private static bool IsApiUp()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://localhost:3000/api/health");
                req.Method  = "GET";
                req.Timeout = 2500;
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return (int)resp.StatusCode < 500;
            }
            catch { return false; }
        }

        private void RegisterAdminMachine()
        {
            try
            {
                string hostname = System.Net.Dns.GetHostName();
                string ip       = GetLanIp();
                string os       = Environment.OSVersion.ToString();
                string mac      = GetPrimaryMac();

                string json = string.Format(
                    "{{\"hostname\":\"{0}\",\"ip\":\"{1}\",\"os\":\"{2}\",\"mac\":\"{3}\"}}",
                    EscapeJson(hostname), ip, EscapeJson(os), mac);

                var req = (HttpWebRequest)WebRequest.Create("http://localhost:3000/api/nodes/register-admin");
                req.Method      = "POST";
                req.ContentType = "application/json";
                req.Timeout     = 8000;

                byte[] body = Encoding.UTF8.GetBytes(json);
                req.ContentLength = body.Length;
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    _log(string.Format("OK: Admin machine registered as {0}", hostname));
                }
            }
            catch (Exception ex)
            {
                _log(string.Format("WARN: Admin registration failed — {0}", ex.Message));
            }
        }

        private static string GetPrimaryMac()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    byte[] b = nic.GetPhysicalAddress().GetAddressBytes();
                    if (b.Length == 6)
                        return string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                            b[0], b[1], b[2], b[3], b[4], b[5]);
                }
            }
            catch { }
            return "00:00:00:00:00:00";
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r");
        }

        // ----------------------------------------------------------------
        // STATIC: GET LAN IPv4
        // ----------------------------------------------------------------
        public static string GetLanIp()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            return addr.Address.ToString();
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        // ----------------------------------------------------------------
        // ROOT DIR RESOLUTION
        // ----------------------------------------------------------------
        private static string ResolveRootDir()
        {
            // Walk up from assembly directory to find "API_Server" sibling
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6; i++)
            {
                if (Directory.Exists(Path.Combine(dir, "API_Server"))) return dir;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }
}
