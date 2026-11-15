using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RASD_AdminWatcher
{
    /// <summary>
    /// Watchdog — Pings the Node.js API every 100 seconds.
    /// If the API fails to respond, it invokes NetworkManager to force-restart Node.
    /// </summary>
    public class Watchdog
    {
        private const int PING_INTERVAL_MS  = 100000;   // 100 seconds
        private const int HTTP_TIMEOUT_MS   = 8000;
        private const string HEALTH_ENDPOINT = "http://localhost:3000/api/health";

        private readonly Action<string>          _log;
        private readonly Action<bool>            _updateApiCard;
        private readonly NetworkManager          _netManager;
        private readonly Action<AlertType, string, string, string> _showAlert;

        private CancellationTokenSource _cts;
        private int  _tickCount  = 0;
        private int  _failStreak = 0;
        private const int MAX_FAIL_STREAK = 2;

        public Watchdog(Action<string> log, Action<bool> updateApiCard,
            NetworkManager netManager,
            Action<AlertType, string, string, string> showAlert = null)
        {
            _log           = log;
            _updateApiCard = updateApiCard;
            _netManager    = netManager;
            _showAlert     = showAlert;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => WatchdogLoop(_cts.Token));
            _log("Watchdog: 100s heartbeat monitor started.");
        }

        public void Stop()
        {
            if (_cts != null) _cts.Cancel();
            _log("Watchdog: Stopped.");
        }

        // ----------------------------------------------------------------
        // WATCHDOG LOOP
        // ----------------------------------------------------------------
        private async Task WatchdogLoop(CancellationToken ct)
        {
            // Wait 5 seconds before first ping (let Node fully start)
            await Task.Delay(5000, ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                _tickCount++;
                string tickLabel = string.Format("TICK #{0}", _tickCount);

                try
                {
                    _log(string.Format("Watchdog {0}: pinging {1}...", tickLabel, HEALTH_ENDPOINT));
                    bool alive = await PingApiAsync();

                    if (alive)
                    {
                        _failStreak = 0;
                        _updateApiCard(true);
                        _log(string.Format("Watchdog {0}: API ONLINE — OK", tickLabel));
                    }
                    else
                    {
                        _failStreak++;
                        _updateApiCard(false);
                        _log(string.Format("WARN: Watchdog {0}: API OFFLINE (fail streak: {1}/{2})", tickLabel, _failStreak, MAX_FAIL_STREAK));
                        ErrorLogger.Warn(string.Format("API offline — fail streak {0}/{1}", _failStreak, MAX_FAIL_STREAK));

                        // Show alert on first failure
                        if (_failStreak == 1)
                        {
                            _showAlert?.Invoke(AlertType.Warning,
                                "API Server Unreachable",
                                "Node.js health endpoint did not respond at " + HEALTH_ENDPOINT,
                                "Watchdog will auto-restart Node.js after " + MAX_FAIL_STREAK + " consecutive failures.");
                        }

                        if (_failStreak >= MAX_FAIL_STREAK)
                        {
                            string restartMsg = string.Format("API down for {0} consecutive checks — force-restarting Node.js.", _failStreak);
                            _log("ERROR: " + restartMsg);
                            ErrorLogger.Error(restartMsg);
                            _showAlert?.Invoke(AlertType.Error,
                                "API Server Down — Auto-Restart",
                                restartMsg,
                                "If this keeps happening: check Node.js is installed and C:\\Program Files\\RASD\\API_Server\\server.js exists.");
                            _failStreak = 0;
                            _netManager.RestartNodeApi();
                            await Task.Delay(15000, ct).ConfigureAwait(false);
                            continue;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log(string.Format("ERROR: Watchdog loop exception — {0}", ex.Message));
                }

                await Task.Delay(PING_INTERVAL_MS, ct).ConfigureAwait(false);
            }
        }

        // ----------------------------------------------------------------
        // HTTP PING
        // ----------------------------------------------------------------
        private static async Task<bool> PingApiAsync()
        {
            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromMilliseconds(HTTP_TIMEOUT_MS);
                    var resp = await http.GetAsync(HEALTH_ENDPOINT).ConfigureAwait(false);
                    return resp.IsSuccessStatusCode;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
