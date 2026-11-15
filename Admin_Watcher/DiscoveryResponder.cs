using System;
using RASD.Shared;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RASD_AdminWatcher
{
    /// <summary>
    /// DiscoveryResponder — the admin half of LAN auto-discovery.
    ///
    /// Binds UDP 3002 and answers agent probes, so an endpoint never needs a
    /// hardcoded admin IP:
    ///
    ///     agent  --broadcast--> "RASD_DISCOVER"        (to 255.255.255.255:3002)
    ///     admin  --unicast---->  "RASD_ADMIN:&lt;ip&gt;"     (back to the agent's port)
    ///
    /// This complements NetworkManager.UdpBroadcastLoop, which announces the
    /// admin IP every 30s unprompted. The responder makes discovery immediate
    /// instead of making an agent wait for the next announcement window.
    /// </summary>
    public class DiscoveryResponder
    {
        public const int DISCOVERY_PORT = 3002;
        // Wire tags and HMAC format live in RASD.Shared.DiscoverySecurity.

        private readonly Action<string> _log;
        private readonly Func<string>   _resolveAdminIp;

        private CancellationTokenSource _cts;
        private int _started = 0;

        public DiscoveryResponder(Action<string> log, Func<string> resolveAdminIp)
        {
            _log            = log;
            _resolveAdminIp = resolveAdminIp;
        }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                _log("DiscoveryResponder: Start ignored — already running.");
                return;
            }

            _cts = new CancellationTokenSource();
            Task.Run(() => ListenLoop(_cts.Token));
        }

        public void Stop()
        {
            Interlocked.Exchange(ref _started, 0);
            try { if (_cts != null) _cts.Cancel(); } catch { }
        }

        // ----------------------------------------------------------------
        // LISTEN LOOP — rebinds on failure so a transient bind error or an
        // adapter change never permanently disables discovery.
        // ----------------------------------------------------------------
        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using (var udp = new UdpClient())
                    {
                        // ReuseAddress: a connector may also be installed on this
                        // machine and listening on the same port for announcements.
                        udp.Client.SetSocketOption(SocketOptionLevel.Socket,
                            SocketOptionName.ReuseAddress, true);
                        udp.Client.Bind(new IPEndPoint(IPAddress.Any, DISCOVERY_PORT));
                        udp.EnableBroadcast = true;

                        _log(string.Format(
                            "DiscoveryResponder: Listening for agent probes on 0.0.0.0:{0}", DISCOVERY_PORT));

                        while (!ct.IsCancellationRequested)
                        {
                            var result = await udp.ReceiveAsync().ConfigureAwait(false);
                            string msg    = Encoding.UTF8.GetString(result.Buffer).Trim();
                            string srcIp  = result.RemoteEndPoint.Address.ToString();

                            // Ignore our own announcements echoed back by the broadcast.
                            if (msg.StartsWith(DiscoverySecurity.REPLY_TAG + "|", StringComparison.Ordinal))
                                continue;

                            if (!msg.StartsWith(DiscoverySecurity.PROBE_TAG, StringComparison.Ordinal))
                                continue;

                            // A1: the probe must carry a valid HMAC over its nonce, or
                            // it is a spoof/mistake and is dropped with a log line.
                            byte[] key = DiscoverySecurity.LoadKey();
                            if (key == null)
                            {
                                _log("WARN: [SECURITY] Probe received but node.key is missing — " +
                                     "cannot authenticate discovery. Ignoring probe from " + srcIp + ".");
                                continue;
                            }

                            if (!DiscoverySecurity.TryValidateProbe(key, msg, out string nonce))
                            {
                                _log("WARN: [SECURITY] REJECTED discovery probe from " + srcIp +
                                     " — invalid or missing HMAC (possible spoof).");
                                continue;
                            }

                            string adminIp = null;
                            try { adminIp = _resolveAdminIp(); } catch { }

                            if (string.IsNullOrEmpty(adminIp))
                            {
                                _log(string.Format(
                                    "DiscoveryResponder: Authenticated probe from {0} but no wired IP to " +
                                    "advertise — ignoring.", result.RemoteEndPoint));
                                continue;
                            }

                            // A1: sign the reply over ip+nonce so the agent can prove it
                            // came from the real admin and matches the nonce it sent.
                            string replyMsg = DiscoverySecurity.BuildReply(key, adminIp, nonce);
                            byte[] reply    = Encoding.UTF8.GetBytes(replyMsg);
                            await udp.SendAsync(reply, reply.Length, result.RemoteEndPoint)
                                     .ConfigureAwait(false);

                            _log(string.Format(
                                "DiscoveryResponder: Authenticated probe from {0} — replied {1}|{2}|... (signed)",
                                result.RemoteEndPoint, DiscoverySecurity.REPLY_TAG, adminIp));
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    _log(string.Format(
                        "WARN: DiscoveryResponder error — {0}. Rebinding in 5s.", ex.Message));
                    try { await Task.Delay(5000, ct).ConfigureAwait(false); } catch { break; }
                }
            }

            _log("DiscoveryResponder: Stopped.");
        }
    }
}
