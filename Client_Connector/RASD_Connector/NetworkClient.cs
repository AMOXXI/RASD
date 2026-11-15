using System;
using RASD.Shared;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RASD_Connector
{
    /// <summary>
    /// Connects to the Admin Watcher on port 3001.
    /// Resolves admin IP from: (1) config file, (2) UDP broadcast discovery, (3) fallback subnet scan.
    /// Maintains the TCP connection, sends heartbeats every 60s, and routes incoming commands.
    /// </summary>
    public static class NetworkClient
    {
        private const int    ADMIN_TCP_PORT  = 3001;
        private const int    HEARTBEAT_MS    = 60000;
        private const int    DISCOVERY_PORT  = 3002;
        private const string CONFIG_FILE     = @"C:\ProgramData\RASD\admin_ip.txt";

        // Keep-alive + reconnect tuning
        private const int    PING_MS         = 20000;   // send PING every 20s
        private const int    PONG_TIMEOUT_MS = 60000;   // no PONG within 60s -> reconnect
        private const int    BACKOFF_MIN_MS  = 1000;    // 1s, doubling
        private const int    BACKOFF_MAX_MS  = 30000;   // capped at 30s
        private const int    SOCKET_IO_TIMEOUT_MS = 30000; // stream read/write timeout
        private const string LOG_FILE        = @"C:\ProgramData\RASD\connector.log";

        // Phase 2 — LAN auto-discovery (wire tags/format live in DiscoverySecurity)
        private const int    PROBE_WAIT_MS   = 2500;   // wait for an admin reply
        private const int    REDISCOVER_AFTER_FAILURES = 2;  // consecutive fails -> re-discover

        // Phase 2 — status surface consumed by the client tray app.
        // The reconnect flag lives in an ipc\ subfolder that the installer leaves
        // user-writable, because the tray runs as the logged-in (limited) user
        // while the rest of C:\ProgramData\RASD is locked to Admins/SYSTEM (A2).
        private const string STATUS_FILE    = @"C:\ProgramData\RASD\status.ini";
        private const string RECONNECT_FLAG = @"C:\ProgramData\RASD\ipc\reconnect.flag";

        private static volatile string   _statusText  = "Starting";
        private static DateTime          _lastHeartbeatUtc = DateTime.MinValue;

        // Phase 3 — wired-LAN-only enforcement
        private static volatile string _adapterName = "";
        private static volatile string _adapterType = "";
        private static volatile bool   _wiredBlocked = false;

        // Signalled by NetworkAddressChanged so plugging a cable in reconnects at
        // once instead of waiting out the current backoff delay.
        private static readonly SemaphoreSlim _wake = new SemaphoreSlim(0, 1);

        private static readonly Random _rng = new Random();

        private static TcpClient             _client;
        private static CancellationTokenSource _cts;
        private static string                _adminIp = null;
        private static string                _nodeId  = "";

        private static int      _started      = 0;                // single-flight guard
        private static DateTime _lastPongUtc  = DateTime.UtcNow;  // keep-alive tracking
        private static readonly object _logLock = new object();

        // Discovery: UDP listener thread updates this
        private static string _discoveredIp = null;

        public static void Start()
        {
            // Single-flight guard — never run two connection loops at once.
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                Log("[NET] Start() ignored — network client already running.");
                return;
            }

            _cts = new CancellationTokenSource();
            Log("[NET] ===== RASD Connector network client starting =====");

            // Wired-LAN-only policy: log the full adapter inventory up front so a
            // refusal to connect can always be traced to a specific adapter.
            Log(NetworkPolicy.InventoryReport());

            // The admin IP is resolved inside ConnectionLoop (cache first, then
            // active LAN discovery), so a missing or stale admin_ip.txt is no
            // longer fatal and no longer needs to be supplied at install time.
            SetStatus("Starting");

            // Plugging in a cable (or losing one) re-evaluates adapters immediately.
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

            // Passive listener: adopts the admin's unprompted 30s announcements,
            // which is how an admin IP change is picked up without a failure first.
            Task.Run(() => UdpDiscoveryLoop(_cts.Token));

            Task.Run(() => ConnectionLoop(_cts.Token));
        }

        public static void Stop()
        {
            Log("[NET] Stop() requested — shutting down network client.");
            try { NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged; } catch { }
            _cts?.Cancel();
            try { _client?.Close(); } catch { }
            Interlocked.Exchange(ref _started, 0);
        }

        // ====================================================================
        // ADAPTER CHANGE — a cable event should not wait out the backoff
        // ====================================================================
        private static void OnNetworkAddressChanged(object sender, EventArgs e)
        {
            try
            {
                var pref = NetworkPolicy.PreferredAdapter();
                Log("[POLICY] Network address change detected. Usable wired adapter: " +
                    (pref == null ? "NONE" : pref.Name + " / " + pref.Ip));

                // Drop the current socket so the loop re-evaluates against the new
                // adapter set, and wake it out of any pending backoff delay.
                try { _client?.Close(); } catch { }
                Wake();
            }
            catch { }
        }

        private static void Wake()
        {
            try { if (_wake.CurrentCount == 0) _wake.Release(); } catch { }
        }

        /// <summary>Waits out a backoff delay, but returns early on a network change.</summary>
        private static async Task<bool> DelayOrWake(int ms, CancellationToken ct)
        {
            try { return await _wake.WaitAsync(ms, ct).ConfigureAwait(false); }
            catch { return false; }
        }

        // ====================================================================
        // LOGGING — console (debug mode) + file (service mode has no console)
        // ====================================================================
        internal static void Log(string msg)
        {
            string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}", DateTime.Now, msg);
            try { Console.WriteLine(line); } catch { }
            try
            {
                lock (_logLock)
                {
                    string dir = Path.GetDirectoryName(LOG_FILE);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    // rotate at 5 MB
                    try
                    {
                        if (File.Exists(LOG_FILE) && new FileInfo(LOG_FILE).Length > 5 * 1024 * 1024)
                            File.Move(LOG_FILE, LOG_FILE.Replace(".log",
                                "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log"));
                    }
                    catch { }

                    File.AppendAllText(LOG_FILE, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }

        // ====================================================================
        // READ ADMIN IP FROM CONFIG FILE
        // ====================================================================
        private static string ReadAdminIpFromConfig()
        {
            try
            {
                if (!File.Exists(CONFIG_FILE)) return null;
                string ip = File.ReadAllText(CONFIG_FILE).Trim();
                if (IsValidIp(ip)) return ip;
                Log("[NET] Config file contains invalid IP: " + ip);
                return null;
            }
            catch (Exception ex)
            {
                Log("[NET] Error reading config: " + ex.Message);
                return null;
            }
        }

        // ====================================================================
        // UDP DISCOVERY LOOP — listen for admin broadcasts on port 3002
        // ====================================================================
        private static async Task UdpDiscoveryLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using (var udp = new UdpClient())
                    {
                        udp.Client.SetSocketOption(SocketOptionLevel.Socket,
                            SocketOptionName.ReuseAddress, true);
                        udp.Client.Bind(new IPEndPoint(IPAddress.Any, DISCOVERY_PORT));
                        udp.Client.ReceiveTimeout = 5000;

                        Log("[NET] UDP: Listening for admin broadcast on port " + DISCOVERY_PORT);

                        // Keep receiving until cancelled
                        while (!ct.IsCancellationRequested)
                        {
                            try
                            {
                                var remoteEp = new IPEndPoint(IPAddress.Any, 0);
                                // Non-async receive with timeout
                                byte[] data = udp.Receive(ref remoteEp);
                                string msg = Encoding.UTF8.GetString(data).Trim();

                                // A1: only honour a signed announcement. An unauthenticated
                                // or forged "RASD_ADMIN|..." is dropped with a log line.
                                if (!msg.StartsWith(DiscoverySecurity.REPLY_TAG + "|", StringComparison.Ordinal))
                                    continue;

                                byte[] key = DiscoverySecurity.LoadKey();
                                if (key == null)
                                {
                                    Log("[SECURITY] UDP: announcement received from " + remoteEp.Address +
                                        " but node.key is missing — cannot authenticate, ignoring.");
                                    continue;
                                }

                                if (!DiscoverySecurity.TryValidateAnnouncement(key, msg, out string ip)
                                    || !IsValidIp(ip))
                                {
                                    Log("[SECURITY] UDP: REJECTED announcement from " + remoteEp.Address +
                                        " — invalid HMAC (possible spoof).");
                                    continue;
                                }

                                if (ip != _discoveredIp)
                                {
                                    _discoveredIp = ip;
                                    _adminIp      = ip;
                                    Log("[NET] UDP: Discovered admin at " + ip + " (authenticated announcement)");
                                    TrySaveDiscoveredIp(ip);
                                }
                            }
                            catch (SocketException) { /* timeout — loop */ }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                        Log("[NET] UDP discovery error: " + ex.Message);
                }

                if (!ct.IsCancellationRequested)
                    await Task.Delay(3000, ct).ConfigureAwait(false);
            }
        }

        private static void TrySaveDiscoveredIp(string ip)
        {
            try
            {
                string dir = Path.GetDirectoryName(CONFIG_FILE);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(CONFIG_FILE, ip);
            }
            catch { }
        }

        // ====================================================================
        // ACTIVE LAN DISCOVERY — broadcast "RASD_DISCOVER" and wait for the
        // admin to answer "RASD_ADMIN:<ip>". This is what removes the need for
        // a hardcoded admin IP: it runs at startup when no cache exists, and
        // again whenever the cached IP stops working.
        // ====================================================================
        private static async Task<string> DiscoverAdminAsync(CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Phase 3: probe ONLY out of adapters the wired-only policy permits.
            var adapters = NetworkPolicy.UsableAdapters();
            if (adapters.Count == 0)
            {
                Log("[POLICY] DISCOVERY BLOCKED — no wired Ethernet adapter is up. " +
                    "Wi-Fi is not permitted for RASD traffic.");
                return null;
            }

            if (NetworkPolicy.WifiOverrideActive())
                Log("[POLICY] *** WARNING: allow_wifi.flag active — non-Ethernet adapters may be used.");

            // A1: probes are HMAC-signed with the shared node.key. Without the key
            // we cannot authenticate the admin, so we refuse to discover blindly.
            byte[] secKey = DiscoverySecurity.LoadKey();
            if (secKey == null)
            {
                Log("[SECURITY] DISCOVERY BLOCKED — node.key is missing. Cannot authenticate the " +
                    "admin, refusing to trust any discovery reply. Reinstall to provision the key.");
                return null;
            }

            string nonce = DiscoverySecurity.NewNonce();

            var sockets = new List<UdpClient>();
            try
            {
                byte[] probe  = Encoding.UTF8.GetBytes(DiscoverySecurity.BuildProbe(secKey, nonce));
                var    sentTo = new List<string>();

                foreach (var a in adapters)
                {
                    UdpClient udp = null;
                    try
                    {
                        udp = new UdpClient();
                        udp.Client.SetSocketOption(SocketOptionLevel.Socket,
                            SocketOptionName.ReuseAddress, true);
                        // Bind to THIS adapter's IP so the probe physically leaves the
                        // wired NIC rather than whichever route the OS prefers.
                        udp.Client.Bind(new IPEndPoint(a.Ip, 0));
                        udp.EnableBroadcast = true;

                        var targets = new List<IPAddress> { IPAddress.Broadcast };
                        var directed = a.DirectedBroadcast;
                        if (directed != null) targets.Add(directed);

                        foreach (var t in targets)
                        {
                            try
                            {
                                await udp.SendAsync(probe, probe.Length,
                                    new IPEndPoint(t, DISCOVERY_PORT)).ConfigureAwait(false);
                                sentTo.Add(a.Name + "->" + t);
                            }
                            catch (Exception ex)
                            {
                                Log(string.Format("[DISCOVERY] Send via {0} to {1} failed: {2}",
                                    a.Name, t, ex.Message));
                            }
                        }

                        Log(string.Format(
                            "[DISCOVERY] Probing adapter {0} [{1}] ip={2} srcPort={3} bcast={4}",
                            a.Name, a.Type, a.Ip,
                            ((IPEndPoint)udp.Client.LocalEndPoint).Port,
                            directed == null ? "255.255.255.255" : directed.ToString()));

                        sockets.Add(udp);
                    }
                    catch (Exception ex)
                    {
                        Log(string.Format("[DISCOVERY] Could not probe via {0}: {1}", a.Name, ex.Message));
                        try { if (udp != null) udp.Close(); } catch { }
                    }
                }

                if (sockets.Count == 0)
                {
                    Log(string.Format(
                        "[DISCOVERY] DISCOVERY FAILED — check firewall/UDP {0}. " +
                        "No allowed adapter could send a probe.", DISCOVERY_PORT));
                    return null;
                }

                Log(string.Format("[DISCOVERY] Sent signed {0} on {1} target(s) [{2}] — waiting {3}ms for a reply",
                    DiscoverySecurity.PROBE_TAG, sentTo.Count, string.Join(", ", sentTo.ToArray()), PROBE_WAIT_MS));

                // Wait for the first valid reply across every probing socket.
                var deadline = DateTime.UtcNow.AddMilliseconds(PROBE_WAIT_MS);
                var pending  = new List<Task<UdpReceiveResult>>();
                foreach (var s in sockets) pending.Add(s.ReceiveAsync());

                while (pending.Count > 0 && !ct.IsCancellationRequested)
                {
                    int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remaining <= 0) break;

                    var timeout   = Task.Delay(remaining, ct);
                    var completed = await Task.WhenAny(new List<Task>(pending.ToArray()) { timeout })
                                              .ConfigureAwait(false);
                    if (completed == timeout) break;

                    var recvTask = (Task<UdpReceiveResult>)completed;
                    int index    = pending.IndexOf(recvTask);
                    pending.Remove(recvTask);

                    string     msg;
                    IPEndPoint from;
                    try
                    {
                        var res = recvTask.Result;
                        msg  = Encoding.UTF8.GetString(res.Buffer).Trim();
                        from = res.RemoteEndPoint;
                    }
                    catch { continue; }

                    // A1: only accept a reply whose HMAC verifies AND whose nonce
                    // matches the one we sent. Anything else is a spoof or replay.
                    if (msg.StartsWith(DiscoverySecurity.REPLY_TAG + "|", StringComparison.Ordinal))
                    {
                        if (DiscoverySecurity.TryValidateReply(secKey, msg, nonce, out string ip)
                            && IsValidIp(ip))
                        {
                            Log(string.Format("[DISCOVERY] Authenticated admin reply from {0} — admin IP {1} (after {2}ms)",
                                from, ip, sw.ElapsedMilliseconds));
                            return ip;
                        }

                        Log(string.Format(
                            "[SECURITY] REJECTED discovery reply from {0} — bad HMAC or nonce mismatch " +
                            "(possible spoof/replay).", from));
                    }

                    // Not a usable reply — keep listening on that socket.
                    if (index >= 0 && index < sockets.Count)
                    {
                        try { pending.Add(sockets[index].ReceiveAsync()); } catch { }
                    }
                }

                Log(string.Format(
                    "[DISCOVERY] DISCOVERY FAILED — check firewall/UDP {0}. " +
                    "No reply after {1}ms across {2} target(s) on {3} allowed adapter(s).",
                    DISCOVERY_PORT, sw.ElapsedMilliseconds, sentTo.Count, sockets.Count));
            }
            catch (Exception ex)
            {
                Log(string.Format("[DISCOVERY] DISCOVERY FAILED — check firewall/UDP {0}. Error after {1}ms: {2}",
                    DISCOVERY_PORT, sw.ElapsedMilliseconds, ex.Message));
            }
            finally
            {
                foreach (var s in sockets) { try { s.Close(); } catch { } }
            }
            return null;
        }

        /// <summary>
        /// Resolves the admin IP: cached override first, then active discovery.
        /// <paramref name="forceDiscovery"/> skips the cache after repeated failures
        /// so a changed admin IP can never strand the agent permanently.
        /// </summary>
        private static async Task<string> ResolveAdminIpAsync(bool forceDiscovery, CancellationToken ct)
        {
            if (!forceDiscovery)
            {
                string cached = ReadAdminIpFromConfig();
                if (cached != null)
                {
                    Log("[DISCOVERY] Using cached/override admin IP: " + cached);
                    return cached;
                }
                Log("[DISCOVERY] No cached admin IP — starting LAN discovery.");
            }
            else
            {
                Log("[DISCOVERY] Cached admin IP is not working — forcing re-discovery.");
            }

            string found = await DiscoverAdminAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(found))
            {
                TrySaveDiscoveredIp(found);   // refresh the cache for next start
                return found;
            }

            // Last resort: sweep the subnet for anything listening on the relay port.
            Log("[DISCOVERY] Falling back to subnet scan on port " + ADMIN_TCP_PORT + "...");
            string scanned = await ScanForAdminAsync(ADMIN_TCP_PORT, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(scanned))
            {
                Log("[DISCOVERY] Admin found by subnet scan at " + scanned);
                TrySaveDiscoveredIp(scanned);
                return scanned;
            }

            return null;
        }

        // ====================================================================
        // STATUS SURFACE — a tiny ini the tray app polls. The connector runs as
        // LocalSystem and the tray as the logged-in user, so a file in
        // ProgramData is the simplest reliable channel between them.
        // ====================================================================
        private static void SetStatus(string status)
        {
            _statusText = status;
            WriteStatusFile();
        }

        private static void WriteStatusFile()
        {
            try
            {
                string dir = Path.GetDirectoryName(STATUS_FILE);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine("status=" + _statusText);
                sb.AppendLine("adminIp=" + (_adminIp ?? ""));
                sb.AppendLine("lastHeartbeat=" + (_lastHeartbeatUtc == DateTime.MinValue
                    ? "" : _lastHeartbeatUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
                sb.AppendLine("nodeId=" + (_nodeId ?? ""));
                sb.AppendLine("hostname=" + Environment.MachineName);
                // Phase 3 — surfaced by the tray app
                sb.AppendLine("adapter=" + (_adapterName ?? ""));
                sb.AppendLine("adapterType=" + (_adapterType ?? ""));
                sb.AppendLine("wiredBlocked=" + (_wiredBlocked ? "1" : "0"));
                sb.AppendLine("wifiOverride=" + (NetworkPolicy.WifiOverrideActive() ? "1" : "0"));
                sb.AppendLine("updated=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                File.WriteAllText(STATUS_FILE, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>True (and clears the flag) if the tray asked for a forced reconnect.</summary>
        private static bool ConsumeReconnectRequest()
        {
            try
            {
                if (!File.Exists(RECONNECT_FLAG)) return false;
                File.Delete(RECONNECT_FLAG);
                Log("[NET] Reconnect requested by the client tray app.");
                return true;
            }
            catch { return false; }
        }

        // ====================================================================
        // SUBNET SCAN FALLBACK — if UDP discovery finds no admin, probe the
        // local /24 for a host listening on the relay port (3001).
        // ====================================================================
        private static async Task<string> ScanForAdminAsync(int port, CancellationToken ct)
        {
            string localIp = GetLocalIp();
            if (string.IsNullOrEmpty(localIp) || localIp == "127.0.0.1") return null;
            int lastDot = localIp.LastIndexOf('.');
            if (lastDot < 0) return null;
            string prefix = localIp.Substring(0, lastDot + 1);

            var found = new TaskCompletionSource<string>();
            using (var throttle = new SemaphoreSlim(25))
            {
                var tasks = new List<Task>();
                for (int host = 1; host <= 254; host++)
                {
                    string ip = prefix + host;
                    if (ip == localIp) continue;
                    try { await throttle.WaitAsync(ct).ConfigureAwait(false); } catch { break; }
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            if (!found.Task.IsCompleted && await ProbeTcpAsync(ip, port, 400).ConfigureAwait(false))
                                found.TrySetResult(ip);
                        }
                        finally { throttle.Release(); }
                    }, ct));
                }
                await Task.WhenAny(found.Task, Task.WhenAll(tasks)).ConfigureAwait(false);
            }
            return found.Task.IsCompleted ? found.Task.Result : null;
        }

        private static async Task<bool> ProbeTcpAsync(string host, int port, int timeoutMs)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connectTask = client.ConnectAsync(host, port);
                    return await Task.WhenAny(connectTask, Task.Delay(timeoutMs)).ConfigureAwait(false) == connectTask
                           && !connectTask.IsFaulted && client.Connected;
                }
            }
            catch { return false; }
        }

        // ====================================================================
        // MAIN CONNECTION LOOP
        // ====================================================================
        private static async Task ConnectionLoop(CancellationToken ct)
        {
            int  backoff          = BACKOFF_MIN_MS;
            int  failuresInARow   = 0;
            bool forceRediscovery = false;

            while (!ct.IsCancellationRequested)
            {
                // ---- Phase 3 gate: refuse to run over anything but wired LAN ----
                var wired = NetworkPolicy.PreferredAdapter();
                if (wired == null)
                {
                    _wiredBlocked = true;
                    _adapterName  = "";
                    _adapterType  = "";
                    SetStatus("No LAN — connect Ethernet cable");
                    Log("[POLICY] BLOCKED — no wired Ethernet adapter is up. " +
                        "RASD will not fall back to Wi-Fi. Waiting for a cable...");

                    int waitMs = ApplyJitter(backoff);
                    if (await DelayOrWake(waitMs, ct).ConfigureAwait(false))
                        Log("[POLICY] Network change detected — re-checking adapters immediately.");
                    if (ct.IsCancellationRequested) break;

                    backoff = Math.Min(backoff * 2, BACKOFF_MAX_MS);
                    continue;
                }

                if (_wiredBlocked)
                {
                    Log("[POLICY] Wired adapter available again: " + wired.Name + " / " + wired.Ip);
                    backoff = BACKOFF_MIN_MS;
                }
                _wiredBlocked = false;
                _adapterName  = wired.Name;
                _adapterType  = wired.Type.ToString();

                if (NetworkPolicy.WifiOverrideActive())
                    Log("[POLICY] *** WARNING: allow_wifi.flag is present — wired-only enforcement is " +
                        "RELAXED for this connection (" + wired.Name + " / " + wired.Type + ").");

                // ---- Resolve the admin IP (cache/override first, then discovery) ----
                if (string.IsNullOrEmpty(_adminIp) || forceRediscovery)
                {
                    SetStatus("Searching");
                    string resolved = await ResolveAdminIpAsync(forceRediscovery, ct).ConfigureAwait(false);
                    forceRediscovery = false;

                    if (string.IsNullOrEmpty(resolved))
                    {
                        // Discovery gets the same exponential backoff as the
                        // connection loop, and every attempt is logged.
                        int wait = ApplyJitter(backoff);
                        Log(string.Format("[DISCOVERY] No admin found — retrying in {0:0.0}s", wait / 1000.0));
                        try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch { break; }
                        backoff = Math.Min(backoff * 2, BACKOFF_MAX_MS);
                        continue;
                    }

                    if (resolved != _adminIp)
                        Log("[DISCOVERY] Admin IP set to " + resolved);
                    _adminIp = resolved;
                    backoff  = BACKOFF_MIN_MS;   // a fresh target deserves a fresh backoff
                    WriteStatusFile();
                }

                TcpClient client  = null;
                CancellationTokenSource connCts = null;
                string    reason  = "unknown";

                try
                {
                    // Bind the outgoing socket to the wired adapter explicitly — the
                    // OS must not be allowed to pick a Wi-Fi route for RASD traffic.
                    client = new TcpClient(new IPEndPoint(wired.Ip, 0));
                    client.ReceiveTimeout = SOCKET_IO_TIMEOUT_MS;
                    client.SendTimeout    = SOCKET_IO_TIMEOUT_MS;
                    client.NoDelay        = true;
                    EnableKeepAlive(client.Client);

                    Log(string.Format("[NET] CONNECTING to Admin Watcher {0}:{1} from {2} [{3}] {4} ...",
                        _adminIp, ADMIN_TCP_PORT, wired.Name, wired.Type, wired.Ip));
                    await client.ConnectAsync(_adminIp, ADMIN_TCP_PORT).ConfigureAwait(false);

                    _client        = client;
                    _lastPongUtc   = DateTime.UtcNow;
                    backoff        = BACKOFF_MIN_MS;   // reset backoff on a successful connect
                    failuresInARow = 0;                // cached IP proven good
                    SetStatus("Connected");

                    // Full endpoint detail — required for two-machine diagnosis.
                    Log(string.Format("[NET] CONNECTED  local={0}  remote={1}  adapter={2}",
                        SafeEndpoint(client.Client.LocalEndPoint),
                        SafeEndpoint(client.Client.RemoteEndPoint),
                        GetAdapterNameForLocalEndpoint(client.Client.LocalEndPoint)));

                    ForensicsMonitor.AdminIp = _adminIp;

                    // Per-connection cancellation: guarantees the ping/heartbeat
                    // tasks for THIS socket die when THIS socket dies.
                    connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    // Register with API, pick up node_id and any queued commands
                    await SendHttpHeartbeat().ConfigureAwait(false);

                    var hbTask   = Task.Run(() => HeartbeatLoop(client, connCts.Token), connCts.Token);
                    var pingTask = Task.Run(() => PingLoop(client, connCts.Token), connCts.Token);

                    var stream = client.GetStream();
                    // A half-open socket now throws IOException instead of blocking forever.
                    try
                    {
                        stream.ReadTimeout  = SOCKET_IO_TIMEOUT_MS;
                        stream.WriteTimeout = SOCKET_IO_TIMEOUT_MS;
                    }
                    catch { }
                    var buffer = new byte[8192];

                    while (!connCts.IsCancellationRequested)
                    {
                        int read = await stream.ReadAsync(buffer, 0, buffer.Length, connCts.Token)
                                               .ConfigureAwait(false);
                        if (read == 0) { reason = "server closed connection"; break; }

                        ProcessIncomingMessage(Encoding.UTF8.GetString(buffer, 0, read));
                    }

                    if (reason == "unknown") reason = "receive loop ended";
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) { reason = "shutdown"; break; }
                    reason = "connection cancelled";
                }
                catch (Exception ex)
                {
                    reason = ex.Message;
                }
                finally
                {
                    try { connCts?.Cancel(); } catch { }   // always kill this socket's helper tasks
                    try { connCts?.Dispose(); } catch { }
                    try { client?.Close(); } catch { }
                    if (ReferenceEquals(_client, client)) _client = null;
                }

                if (ct.IsCancellationRequested) break;

                SetStatus("Disconnected");
                failuresInARow++;

                // Jitter 0.8x–1.2x so a fleet of agents does not reconnect in lockstep.
                int delayMs = ApplyJitter(backoff);

                Log(string.Format("[NET] DISCONNECTED ({0}) — reconnecting in {1:0.0}s (base {2:0.0}s, fail #{3})",
                    reason, delayMs / 1000.0, backoff / 1000.0, failuresInARow));

                // Wake early if an adapter change happens mid-backoff.
                if (await DelayOrWake(delayMs, ct).ConfigureAwait(false))
                    Log("[POLICY] Network change during backoff — retrying now.");
                if (ct.IsCancellationRequested) break;

                backoff = Math.Min(backoff * 2, BACKOFF_MAX_MS);   // 1,2,4,8,16,30,30...

                // The tray's "Reconnect now" button forces an immediate re-discovery.
                if (ConsumeReconnectRequest())
                {
                    forceRediscovery = true;
                    failuresInARow   = 0;
                    backoff          = BACKOFF_MIN_MS;
                    continue;
                }

                // Re-read the override file in case an admin deliberately changed it.
                string freshIp = ReadAdminIpFromConfig();
                if (freshIp != null && freshIp != _adminIp)
                {
                    Log("[NET] Admin IP override updated to: " + freshIp);
                    _adminIp       = freshIp;
                    backoff        = BACKOFF_MIN_MS;
                    failuresInARow = 0;
                    continue;
                }

                // The cached IP keeps failing — stop trusting it and search the LAN
                // again, so a changed admin IP can never strand this agent.
                if (failuresInARow >= REDISCOVER_AFTER_FAILURES)
                {
                    Log(string.Format("[DISCOVERY] {0} consecutive failures against {1} — re-discovering.",
                        failuresInARow, _adminIp));
                    forceRediscovery = true;
                    failuresInARow   = 0;
                }
            }

            Log("[NET] Connection loop exited.");
        }

        // ====================================================================
        // KEEP-ALIVE — PING every 20s; if no PONG within 60s, force reconnect
        // ====================================================================
        private static async Task PingLoop(TcpClient client, CancellationToken ct)
        {
            byte[] ping = Encoding.UTF8.GetBytes("PING\n");

            while (!ct.IsCancellationRequested && client.Connected)
            {
                try { await Task.Delay(PING_MS, ct).ConfigureAwait(false); } catch { return; }
                if (ct.IsCancellationRequested) return;

                try
                {
                    var stream = client.GetStream();
                    await stream.WriteAsync(ping, 0, ping.Length, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log("[NET] PING write failed (" + ex.Message + ") — dropping connection.");
                    try { client.Close(); } catch { }   // breaks the read loop -> reconnect
                    return;
                }

                double silentMs = (DateTime.UtcNow - _lastPongUtc).TotalMilliseconds;
                if (silentMs > PONG_TIMEOUT_MS)
                {
                    Log(string.Format("[NET] No PONG for {0}s (timeout {1}s) — dropping connection.",
                        (int)(silentMs / 1000), PONG_TIMEOUT_MS / 1000));
                    try { client.Close(); } catch { }
                    return;
                }
            }
        }

        // ====================================================================
        // HEARTBEAT LOOP — TCP + HTTP every 60 seconds
        // ====================================================================
        private static async Task HeartbeatLoop(TcpClient client, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && client.Connected)
            {
                try
                {
                    string hostname = Environment.MachineName;
                    string ip       = GetLocalIp();
                    string mac      = GetMacAddress();

                    // TCP heartbeat to Admin Watcher
                    string payload = string.Format(
                        "{{\"type\":\"heartbeat\",\"hostname\":\"{0}\"," +
                        "\"ip\":\"{1}\",\"mac\":\"{2}\",\"status\":\"online\"}}\n",
                        hostname, ip, mac);
                    byte[] data = Encoding.UTF8.GetBytes(payload);

                    var stream = client.GetStream();
                    await stream.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);

                    _lastHeartbeatUtc = DateTime.UtcNow;
                    WriteStatusFile();

                    // HTTP heartbeat to REST API (also drains any queued commands)
                    _ = SendHttpHeartbeat();
                }
                catch (Exception ex)
                {
                    // A failed heartbeat means the socket is gone — tear it down so
                    // ConnectionLoop reconnects, instead of silently going dark.
                    Log("[NET] Heartbeat failed (" + ex.Message + ") — dropping connection.");
                    try { client.Close(); } catch { }
                    return;
                }

                try { await Task.Delay(HEARTBEAT_MS, ct).ConfigureAwait(false); } catch { return; }
            }
        }

        // ====================================================================
        // HTTP HEARTBEAT — registers node and gets node_id
        // ====================================================================
        private static async Task SendHttpHeartbeat()
        {
            try
            {
                string hostname  = Environment.MachineName;
                string ip        = GetLocalIp();
                string mac       = GetMacAddress();
                string os        = Environment.OSVersion.ToString();
                string usbStatus = SystemController.GetCurrentUsbStatus();

                string url  = string.Format("http://{0}:3000/api/nodes/heartbeat", _adminIp);
                string json = string.Format(
                    "{{\"mac_address\":\"{0}\",\"ip_address\":\"{1}\"," +
                    "\"hostname\":\"{2}\",\"os_version\":\"{3}\"," +
                    "\"agent_version\":\"2.0.0\",\"usb_status\":\"{4}\"}}",
                    mac, ip, EscapeJson(hostname), EscapeJson(os), usbStatus);

                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method      = "POST";
                request.ContentType = "application/json";
                request.Timeout     = 8000;

                byte[] body = Encoding.UTF8.GetBytes(json);
                request.ContentLength = body.Length;

                using (var reqStream = request.GetRequestStream())
                    reqStream.Write(body, 0, body.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader   = new System.IO.StreamReader(response.GetResponseStream()))
                {
                    string resp = reader.ReadToEnd();
                    string nodeId = ExtractJsonString(resp, "node_id");
                    if (!string.IsNullOrEmpty(nodeId))
                    {
                        if (_nodeId != nodeId)
                            Log("[NET] Registered with API. Node ID: " + nodeId);
                        _nodeId = nodeId;
                        ForensicsMonitor.NodeId = nodeId;
                    }

                    // Deliver anything the server queued while we were offline
                    ExecutePendingCommands(resp);
                }
            }
            catch (Exception ex)
            {
                Log("[NET] HTTP heartbeat failed: " + ex.Message);
            }
        }

        // ====================================================================
        // INCOMING COMMAND PROCESSOR
        // ====================================================================
        private static void ProcessIncomingMessage(string message)
        {
            string[] lines = message.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                // ---- keep-alive ----
                if (line.Equals("PONG", StringComparison.OrdinalIgnoreCase))
                {
                    _lastPongUtc = DateTime.UtcNow;
                    continue;
                }
                if (line.Equals("PING", StringComparison.OrdinalIgnoreCase))
                {
                    TrySendRaw("PONG\n");   // relay may probe us too
                    continue;
                }

                if (!line.StartsWith("CMD:")) continue;

                string[] parts = line.Split(new[] { ':' }, 3);
                if (parts.Length < 2) continue;

                string commandType = parts[1].Trim();
                string json        = parts.Length > 2 ? parts[2] : "";
                string cmdId       = ExtractJsonString(json, "cmdId");

                Log(string.Format("[NET] COMMAND received: {0}{1}", commandType,
                    string.IsNullOrEmpty(cmdId) ? "" : " (cmdId " + cmdId + ")"));

                SystemController.ExecuteCommand(commandType);
                _ = AcknowledgeCommand(cmdId, commandType);
            }
        }

        private static void TrySendRaw(string text)
        {
            try
            {
                var c = _client;
                if (c == null || !c.Connected) return;
                byte[] b = Encoding.UTF8.GetBytes(text);
                var st = c.GetStream();
                st.Write(b, 0, b.Length);
                st.Flush();
            }
            catch { }
        }

        // ====================================================================
        // COMMAND ACK — marks the queued command DELIVERED server-side
        // ====================================================================
        private static async Task AcknowledgeCommand(string cmdId, string command)
        {
            if (string.IsNullOrEmpty(cmdId)) return;   // nothing to ack against
            try
            {
                string url  = string.Format("http://{0}:3000/api/command/{1}/ack", _adminIp, cmdId);
                string json = "{\"status\":\"DELIVERED\",\"output\":\"executed by agent\"}";
                await PostJsonAsync(url, json).ConfigureAwait(false);
                Log(string.Format("[NET] ACK sent for {0} (cmdId {1})", command, cmdId));
            }
            catch (Exception ex)
            {
                Log(string.Format("[NET] ACK failed for cmdId {0}: {1}", cmdId, ex.Message));
            }
        }

        // ====================================================================
        // QUEUED COMMANDS — executed when the heartbeat reports them pending
        // ====================================================================
        private static void ExecutePendingCommands(string responseJson)
        {
            try
            {
                int marker = responseJson.IndexOf("\"pending_commands\"", StringComparison.OrdinalIgnoreCase);
                if (marker < 0) return;
                int open  = responseJson.IndexOf('[', marker);
                int close = responseJson.IndexOf(']', open + 1);
                if (open < 0 || close < 0 || close <= open) return;

                string arr = responseJson.Substring(open + 1, close - open - 1);
                if (string.IsNullOrWhiteSpace(arr)) return;

                int depth = 0, objStart = -1;
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i] == '{')
                    {
                        if (depth == 0) objStart = i;
                        depth++;
                    }
                    else if (arr[i] == '}')
                    {
                        depth--;
                        if (depth == 0 && objStart >= 0)
                        {
                            string obj = arr.Substring(objStart, i - objStart + 1);
                            string id  = ExtractJsonString(obj, "id");
                            string cmd = ExtractJsonString(obj, "command");
                            objStart = -1;

                            if (string.IsNullOrEmpty(cmd)) continue;

                            Log(string.Format("[NET] QUEUED command delivered: {0} (cmdId {1})", cmd, id));
                            SystemController.ExecuteCommand(cmd);
                            _ = AcknowledgeCommand(id, cmd);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("[NET] Failed to process queued commands: " + ex.Message);
            }
        }

        private static async Task<string> PostJsonAsync(string url, string json)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method      = "POST";
            request.ContentType = "application/json";
            request.Timeout     = 8000;

            byte[] body = Encoding.UTF8.GetBytes(json);
            request.ContentLength = body.Length;

            using (var reqStream = request.GetRequestStream())
                reqStream.Write(body, 0, body.Length);

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader   = new StreamReader(response.GetResponseStream()))
                return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        // ====================================================================
        // CONNECTION HELPERS — jitter, OS keep-alive, endpoint diagnostics
        // ====================================================================

        /// <summary>Spreads reconnects across a fleet: base * 0.8..1.2.</summary>
        private static int ApplyJitter(int baseMs)
        {
            double factor;
            lock (_rng) { factor = 0.8 + (_rng.NextDouble() * 0.4); }
            int ms = (int)(baseMs * factor);
            return ms < 250 ? 250 : ms;
        }

        /// <summary>
        /// OS-level dead-peer detection underneath the application PING:
        /// probe after 30s idle, then every 5s.
        /// </summary>
        private static void EnableKeepAlive(Socket socket)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                // struct tcp_keepalive { u_long onoff; u_long keepalivetime; u_long keepaliveinterval; }
                byte[] inOptions = new byte[12];
                BitConverter.GetBytes((uint)1).CopyTo(inOptions, 0);      // on
                BitConverter.GetBytes((uint)30000).CopyTo(inOptions, 4);  // idle before first probe
                BitConverter.GetBytes((uint)5000).CopyTo(inOptions, 8);   // probe interval
                socket.IOControl(IOControlCode.KeepAliveValues, inOptions, null);
            }
            catch (Exception ex)
            {
                Log("[NET] SO_KEEPALIVE could not be configured: " + ex.Message);
            }
        }

        private static string SafeEndpoint(EndPoint ep)
        {
            try { return ep == null ? "?" : ep.ToString(); } catch { return "?"; }
        }

        /// <summary>Maps the bound local IP back to the NIC name it belongs to.</summary>
        private static string GetAdapterNameForLocalEndpoint(EndPoint ep)
        {
            try
            {
                var ipe = ep as IPEndPoint;
                if (ipe == null) return "?";
                string local = ipe.Address.ToString();

                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.ToString() == local)
                            return nic.Name + " (" + nic.NetworkInterfaceType + ")";
                    }
                }
            }
            catch { }
            return "?";
        }

        // ====================================================================
        // NETWORK HELPERS
        // ====================================================================
        public static string GetLocalIp()
        {
            // Prefer the wired adapter the policy selected, so heartbeats report the
            // same IP the TCP socket is actually bound to.
            try
            {
                var pref = NetworkPolicy.PreferredAdapter();
                if (pref != null && pref.Ip != null) return pref.Ip.ToString();
            }
            catch { }

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                            return addr.Address.ToString();
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        public static string GetMacAddress()
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

        private static bool IsValidIp(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            System.Net.IPAddress addr;
            return IPAddress.TryParse(ip, out addr)
                   && addr.AddressFamily == AddressFamily.InterNetwork
                   && ip != "0.0.0.0";
        }

        private static string ExtractJsonString(string json, string key)
        {
            string search = string.Format("\"{0}\":\"", key);
            int start = json.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return "";
            start += search.Length;
            int end = json.IndexOf('"', start);
            return end > start ? json.Substring(start, end - start) : "";
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }
}
