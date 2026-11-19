using System;
using RASD.Shared;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Net;

namespace RASD_AdminWatcher
{
    /// <summary>
    /// RelayEngine — Dual-layer command bridge:
    ///   LAYER 1: Connects to the Node.js Socket.IO server via WebSocket.
    ///            Receives commands (BLOCK_USB, RESTART, EXEC_PS, etc.)
    ///            destined for managed nodes.
    ///
    ///   LAYER 2: Maintains a TCP listener on port 3001.
    ///            Managed node Agents connect here. When the Watcher
    ///            receives a command from Layer 1, it forwards it to
    ///            the appropriate agent TCP socket by node IP.
    /// </summary>
    public class RelayEngine
    {
        // ----------------------------------------------------------------
        // CONFIGURATION
        // ----------------------------------------------------------------
        private const string WS_URL            = "ws://localhost:3000/socket.io/?EIO=4&transport=websocket&role=watcher";
        private const int    TCP_PORT          = 3001;
        // NOTE: There is deliberately NO client ping interval. In Engine.IO v4 the
        // SERVER sends ping ("2") and the CLIENT replies pong ("3"). A client that
        // sends its own "2" is killed by engine.io with "invalid heartbeat
        // direction" (engine.io/build/socket.js onPacket, case "ping"), which is
        // what caused the exact ~26s disconnect cycle.
        private const int    RECONNECT_MIN_MS  = 1000;    // 1s, doubling
        private const int    RECONNECT_MAX_MS  = 30000;   // capped at 30s
        private const int    AGENT_IO_TIMEOUT_MS = 30000; // agent socket read/write timeout
        private const int    MAX_PENDING_CHARS   = 65536; // max unfinished line from an agent

        // ----------------------------------------------------------------
        // STATE
        // ----------------------------------------------------------------
        private readonly Action<string>            _log;
        private readonly Action<string, bool>      _updateRelayCard;
        private readonly Action<string, string>    _onClientDisconnected; // (ip, reason)

        private CancellationTokenSource _cts;
        private ClientWebSocket          _ws;

        // TCP agent connections: key = remote IP string
        private readonly ConcurrentDictionary<string, TcpClient> _agents
            = new ConcurrentDictionary<string, TcpClient>();

        private TcpListener _tcpServer;
        private int  _started  = 0;   // single-flight guard
        private long _agentSeq = 0;   // unique key per agent connection

        // Raw Engine.IO frame tracing (non-heartbeat frames are capped per connection)
        private const int FRAME_TRACE_LIMIT = 40;
        private int _frameTrace = 0;

        public RelayEngine(Action<string> log, Action<string, bool> updateRelayCard,
            Action<string, string> onClientDisconnected = null)
        {
            _log                  = log;
            _updateRelayCard      = updateRelayCard;
            _onClientDisconnected = onClientDisconnected;
        }

        // ----------------------------------------------------------------
        // START / STOP
        // ----------------------------------------------------------------
        public void Start()
        {
            // Never run two WebSocket loops / two TCP listeners at once.
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                _log("RelayEngine: Start ignored — already running.");
                return;
            }

            _cts = new CancellationTokenSource();
            Task.Run(() => WebSocketLoop(_cts.Token));
            Task.Run(() => TcpServerLoop(_cts.Token));
            _log("RelayEngine: WebSocket bridge + TCP listener started.");
            _updateRelayCard("STARTING", false);
        }

        public void Stop()
        {
            Interlocked.Exchange(ref _started, 0);
            if (_cts != null) _cts.Cancel();
            try { if (_ws != null) _ws.Abort(); } catch { }
            try { if (_tcpServer != null) _tcpServer.Stop(); } catch { }
            foreach (var kv in _agents)
                try { if (kv.Value != null) kv.Value.Close(); } catch { }
            _agents.Clear();
            _updateRelayCard("IDLE", false);
            _log("RelayEngine: Stopped.");
        }

        // ================================================================
        // LAYER 1: SOCKET.IO WEBSOCKET CLIENT
        // ================================================================
        private async Task WebSocketLoop(CancellationToken ct)
        {
            int backoff = RECONNECT_MIN_MS;

            while (!ct.IsCancellationRequested)
            {
                var ws = new ClientWebSocket();
                _ws = ws;
                CancellationTokenSource connCts = null;
                string reason = "unknown";

                try
                {
                    _log(string.Format("RelayEngine: Connecting to Socket.IO — {0}", WS_URL));
                    await ws.ConnectAsync(new Uri(WS_URL), ct).ConfigureAwait(false);
                    _log("OK: RelayEngine connected to Socket.IO WebSocket.");
                    _updateRelayCard("CONNECTED", true);
                    backoff     = RECONNECT_MIN_MS;   // reset backoff on a successful connect
                    _frameTrace = 0;                  // fresh frame trace for this connection

                    // Socket.IO namespace connect
                    await SendRawAsync(ws, "40{\"role\":\"watcher\"}", ct).ConfigureAwait(false);

                    // Per-connection cancellation so this socket's helper work can
                    // never outlive it and leak into the next connection.
                    connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    var buffer  = new byte[8192];
                    var pending = new StringBuilder();

                    while (ws.State == WebSocketState.Open && !connCts.IsCancellationRequested)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), connCts.Token)
                                             .ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            reason = string.Format("server closed WebSocket [{0}] {1}",
                                ws.CloseStatus, ws.CloseStatusDescription);
                            break;
                        }

                        // A single Engine.IO packet can span multiple WS frames.
                        pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        if (!result.EndOfMessage) continue;

                        string frame = pending.ToString();
                        pending.Clear();

                        await ProcessSocketIoMessageAsync(ws, frame, connCts.Token).ConfigureAwait(false);
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
                    _updateRelayCard("RECONNECTING", false);
                }
                finally
                {
                    // ALWAYS stop this connection's ping loop, on every exit path.
                    try { connCts?.Cancel(); } catch { }
                    try { connCts?.Dispose(); } catch { }
                    try { ws.Abort(); ws.Dispose(); } catch { }
                    if (ReferenceEquals(_ws, ws)) _ws = null;
                }

                if (ct.IsCancellationRequested) break;

                _log(string.Format("WARN: RelayEngine WS disconnected ({0}) — reconnecting in {1}s",
                    reason, backoff / 1000.0));
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch { break; }
                backoff = Math.Min(backoff * 2, RECONNECT_MAX_MS);   // 1,2,4,8,16,30,30...
            }
        }

        // ---- Socket.IO message parser ----
        private async Task ProcessSocketIoMessageAsync(ClientWebSocket ws, string raw, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(raw)) return;

            LogFrame("RECV", raw);

            // Engine.IO v4 heartbeat: the SERVER pings ("2"), we MUST pong ("3").
            // Awaited on THIS socket so the reply is ordered and can never land on
            // a replaced connection. Failing to pong = dropped after pingTimeout.
            if (raw == "2")
            {
                try
                {
                    await SendRawAsync(ws, "3", ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log("ERROR: [EIO] pong send failed — " + ex.Message);
                }
                return;
            }

            // Engine.IO pong from server (only seen if the server is on protocol 3)
            if (raw == "3") return;

            // Engine.IO open packet (server sends session info)
            if (raw.StartsWith("0{")) return;

            // Socket.IO event: 42["eventName", {payload}]
            if (raw.StartsWith("42"))
            {
                string json = raw.Substring(2);
                _log(string.Format("[WS→RELAY] Received: {0}", json));

                // Parse event name and data
                // Format: ["command", {"cmdId":"...", "command":"BLOCK_USB", "payload":{...}, "nodeId":"..."}]
                // Simple manual parsing — no external JSON library needed
                if (json.Contains("\"command\""))
                {
                    string cmdType  = ExtractJsonString(json, "command");
                    string nodeId   = ExtractJsonString(json, "nodeId");
                    string cmdId    = ExtractJsonString(json, "cmdId");

                    _log(string.Format("[RELAY] Command received: {0} → Node {1} (cmdId: {2})", cmdType, nodeId, cmdId));
                    RelayCommandToAgent(nodeId, cmdType, json);
                }
                return;
            }

            // Ignore Socket.IO ACKs and other packets
        }

        // ---- Forward command to target agent via TCP ----
        private void RelayCommandToAgent(string nodeId, string command, string fullJson)
        {
            // We look up agent by nodeId. In production the agent would register its nodeId.
            // Here we broadcast to all connected agents and let them self-filter.
            if (_agents.IsEmpty)
            {
                _log(string.Format("WARN: [RELAY] No agents connected — command {0} queued in API.", command));
                return;
            }

            byte[] payload = Encoding.UTF8.GetBytes(string.Format("CMD:{0}:{1}\n", command, fullJson));
            int sent = 0;

            foreach (var kv in _agents)
            {
                try
                {
                    var stream = kv.Value.GetStream();
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush();
                    sent++;
                }
                catch (Exception ex)
                {
                    _log(string.Format("WARN: [RELAY] Failed to send to agent {0} — {1}", kv.Key, ex.Message));
                    TcpClient removed;
                    _agents.TryRemove(kv.Key, out removed);
                }
            }

            if (sent > 0)
                _log(string.Format("OK: [RELAY] Command {0} delivered to {1} agent(s).", command, sent));
        }

        // ---- Raw Engine.IO send, bound to ONE socket instance ----
        // Takes the socket explicitly so a late write can never be delivered on a
        // newer connection that replaced _ws.
        private async Task SendRawAsync(ClientWebSocket ws, string message, CancellationToken ct)
        {
            if (ws == null || ws.State != WebSocketState.Open) return;
            LogFrame("SENT", message);
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct)
                    .ConfigureAwait(false);
        }

        // ---- Raw Engine.IO frame trace ----
        // Heartbeat frames ("2"/"3") are always logged: two lines per 25s is cheap
        // and it is the only direct evidence that the pong handshake is healthy.
        // Other frames are traced up to a cap so a busy relay cannot flood the log.
        private void LogFrame(string direction, string frame)
        {
            bool heartbeat = frame == "2" || frame == "3";
            if (!heartbeat && Interlocked.Increment(ref _frameTrace) > FRAME_TRACE_LIMIT) return;

            string shown = frame.Length > 200 ? frame.Substring(0, 200) + "...(" + frame.Length + "B)" : frame;
            string tag   = heartbeat ? (frame == "2" ? " (server ping)" : " (our pong)") : "";
            _log(string.Format("[EIO {0}] {1}{2}", direction, shown, tag));
        }

        // ================================================================
        // LAYER 2: TCP SERVER — Agent connections
        // ================================================================
        private async Task TcpServerLoop(CancellationToken ct)
        {
            try
            {
                _tcpServer = new TcpListener(IPAddress.Any, TCP_PORT);
                _tcpServer.Start();
                _log(string.Format("RelayEngine: TCP agent listener started on 0.0.0.0:{0}", TCP_PORT));

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var client = await AcceptAsync(_tcpServer, ct).ConfigureAwait(false);
                        if (client == null) break;

                        ConfigureAgentSocket(client);   // OS keep-alive + I/O timeouts

                        var remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();

                        // Phase 3: refuse agent connections that arrived on a
                        // non-Ethernet local interface. The listener binds
                        // 0.0.0.0, so a connection can land on the Wi-Fi IP; the
                        // LOCAL endpoint address tells us which adapter accepted it.
                        var localIp  = ((IPEndPoint)client.Client.LocalEndPoint).Address;
                        var remoteAddr = ((IPEndPoint)client.Client.RemoteEndPoint).Address;

                        // A4: the loopback exemption exists only for the admin's own
                        // 127.0.0.1 self-test. Verify BOTH ends are genuine loopback —
                        // a remote host cannot reach us on 127.0.0.0/8 (the OS drops
                        // such packets), but we assert it explicitly and log every use
                        // so the exemption can never silently widen.
                        if (IPAddress.IsLoopback(localIp))
                        {
                            if (IPAddress.IsLoopback(remoteAddr))
                            {
                                _log(string.Format(
                                    "[POLICY] Loopback self-test connection accepted ({0} -> {1}).",
                                    remoteAddr, localIp));
                            }
                            else
                            {
                                _log(string.Format(
                                    "WARN: [POLICY] REJECTED connection from {0} claiming loopback local {1} " +
                                    "— non-loopback remote on the loopback interface is not permitted.",
                                    remoteAddr, localIp));
                                try { client.Close(); } catch { }
                                continue;
                            }
                        }
                        else if (!NetworkPolicy.IsAllowedLocalIp(localIp, out string ifName))
                        {
                            if (NetworkPolicy.WifiOverrideActive())
                            {
                                _log(string.Format(
                                    "WARN: [POLICY] Agent from {0} arrived on non-Ethernet {1} — " +
                                    "ALLOWED only by allow_wifi.flag override.", remoteIp, ifName));
                            }
                            else
                            {
                                _log(string.Format(
                                    "WARN: [POLICY] REJECTED agent from {0} — arrived on non-Ethernet " +
                                    "interface {1}. Wired LAN is required.", remoteIp, ifName));
                                try { client.Close(); } catch { }
                                continue;
                            }
                        }

                        // Key by a unique per-connection id, NOT by IP: a reconnect from
                        // the same IP used to overwrite the entry, and the old socket's
                        // cleanup then removed the NEW connection.
                        string agentId = Interlocked.Increment(ref _agentSeq).ToString();
                        _agents[agentId] = client;

                        _log(string.Format("OK: [TCP] Agent connected from {0} [conn #{1}] (total: {2})",
                            remoteIp, agentId, _agents.Count));
                        _updateRelayCard(string.Format("LIVE ({0} agents)", _agents.Count), true);

                        // Handle agent in background
                        var _ = Task.Run(() => HandleAgentAsync(client, agentId, remoteIp, ct), ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        if (!ct.IsCancellationRequested)
                            _log(string.Format("WARN: [TCP] Accept error — {0}", ex.Message));
                    }
                }
            }
            catch (Exception ex)
            {
                _log(string.Format("ERROR: [TCP] Server failed to start on port {0} — {1}", TCP_PORT, ex.Message));
            }
        }

        private async Task HandleAgentAsync(TcpClient client, string agentId, string remoteIp, CancellationToken ct)
        {
            var buffer = new byte[4096];
            byte[] pong = Encoding.UTF8.GetBytes("PONG\n");

            // TCP is a byte stream: keep an unfinished line until its newline arrives,
            // and decode UTF-8 statefully so a character split across reads survives.
            var decoder = Encoding.UTF8.GetDecoder();
            var chars   = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
            var pending = new StringBuilder();

            try
            {
                var stream = client.GetStream();
                while (client.Connected && !ct.IsCancellationRequested)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                    if (read == 0) break; // Client disconnected

                    pending.Append(chars, 0, decoder.GetChars(buffer, 0, read, chars, 0));
                    string text = pending.ToString();
                    int lastNewline = text.LastIndexOf('\n');
                    if (lastNewline < 0)
                    {
                        if (pending.Length > MAX_PENDING_CHARS)
                        {
                            _log(string.Format("WARN: [TCP] Agent {0} sent {1}+ characters without a newline — discarded.",
                                remoteIp, MAX_PENDING_CHARS));
                            pending.Clear();
                        }
                        continue;
                    }
                    string chunk = text.Substring(0, lastNewline + 1);
                    pending.Clear();
                    pending.Append(text, lastNewline + 1, text.Length - lastNewline - 1);

                    foreach (var rawLine in chunk.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string line = rawLine.Trim();
                        if (line.Length == 0) continue;

                        // Keep-alive: answer the agent's PING so it can detect a
                        // half-open socket instead of sitting on a dead connection.
                        if (line.Equals("PING", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                await stream.WriteAsync(pong, 0, pong.Length, ct).ConfigureAwait(false);
                                await stream.FlushAsync(ct).ConfigureAwait(false);
                            }
                            catch { }
                            continue;
                        }

                        _log(string.Format("[TCP←AGENT:{0}] {1}", remoteIp, line));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log(string.Format("WARN: [TCP] Agent {0} [conn #{1}] error — {2}", remoteIp, agentId, ex.Message));
            }
            finally
            {
                TcpClient removed;
                _agents.TryRemove(agentId, out removed);   // remove THIS connection only
                try { client.Close(); } catch { }
                _log(string.Format("[TCP] Agent {0} [conn #{1}] disconnected. Remaining: {2}",
                    remoteIp, agentId, _agents.Count));
                _updateRelayCard(_agents.Count > 0 ? string.Format("LIVE ({0} agents)", _agents.Count) : "LISTENING", _agents.Count > 0);
                ErrorLogger.Info(string.Format("Agent disconnected: {0}", remoteIp));

                // Notify main window so it can run a connectivity diagnostic
                _onClientDisconnected?.Invoke(remoteIp, "TCP connection closed");
            }
        }

        // ---- Configure an accepted agent socket ----
        // OS-level dead-peer detection (probe after 30s idle, then every 5s) plus
        // I/O timeouts so a half-open agent socket cannot block a handler forever.
        private void ConfigureAgentSocket(TcpClient client)
        {
            try
            {
                var s = client.Client;
                s.NoDelay = true;
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                // struct tcp_keepalive { u_long onoff; u_long keepalivetime; u_long keepaliveinterval; }
                byte[] cfg = new byte[12];
                BitConverter.GetBytes((uint)1).CopyTo(cfg, 0);
                BitConverter.GetBytes((uint)30000).CopyTo(cfg, 4);
                BitConverter.GetBytes((uint)5000).CopyTo(cfg, 8);
                s.IOControl(IOControlCode.KeepAliveValues, cfg, null);

                client.ReceiveTimeout = AGENT_IO_TIMEOUT_MS;
                client.SendTimeout    = AGENT_IO_TIMEOUT_MS;
            }
            catch (Exception ex)
            {
                _log("WARN: [TCP] keep-alive setup failed — " + ex.Message);
            }
        }

        // Async accept wrapper
        private static async Task<TcpClient> AcceptAsync(TcpListener listener, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<TcpClient>();
            ct.Register(() => tcs.TrySetCanceled());
            listener.BeginAcceptTcpClient(ar =>
            {
                try { tcs.TrySetResult(listener.EndAcceptTcpClient(ar)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }, null);
            return await tcs.Task.ConfigureAwait(false);
        }

        // ================================================================
        // JSON HELPER — Manual string extraction (no Newtonsoft needed)
        // ================================================================
        private static string ExtractJsonString(string json, string key)
        {
            string search = string.Format("\"{0}\":\"", key);
            int start = json.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return "";
            start += search.Length;
            int end = json.IndexOf('"', start);
            return end > start ? json.Substring(start, end - start) : "";
        }
    }
}
