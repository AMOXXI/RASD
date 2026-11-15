using System;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace RASD_Connector
{
    /// <summary>
    /// RASD_Connector Windows Service.
    ///
    /// Runs as a proper ServiceBase so the SCM receives SERVICE_RUNNING and does
    /// not kill the process with error 1053. All real work (NetworkClient,
    /// ForensicsMonitor, WatchdogLink) is started on a background thread so
    /// OnStart returns to the SCM immediately.
    /// </summary>
    public class RASDConnectorService : ServiceBase
    {
        public const string SERVICE_NAME = "RASD_Enterprise_Connector";

        public RASDConnectorService()
        {
            ServiceName = SERVICE_NAME;
            CanStop     = true;
            CanShutdown = true;
            AutoLog     = true;
        }

        protected override void OnStart(string[] args)
        {
            // Never let an OnStart exception die silently — that is what made the
            // service fail invisibly at boot. Log it, then start the engine on a
            // background thread so OnStart returns to the SCM immediately.
            try
            {
                NetworkClient.Log("[SVC] OnStart — RASD_Enterprise_Connector starting.");
                Task.Run(() => StartEngine());
            }
            catch (Exception ex)
            {
                NetworkClient.Log("[SVC] OnStart FAILED: " + ex);
                throw;
            }
        }

        protected override void OnStop()     { NetworkClient.Log("[SVC] OnStop."); StopEngine(); }
        protected override void OnShutdown() { NetworkClient.Log("[SVC] OnShutdown."); StopEngine(); }

        internal static void StartEngine()
        {
            try
            {
                // Start the dual-process protection link
                WatchdogLink.StartProtectorMonitor();

                // Start forensics monitoring (Downloads folder watcher + MD5 hashing)
                ForensicsMonitor.Start();

                // Start the network client to connect to Admin Watcher on port 3001
                NetworkClient.Start();

                NetworkClient.Log("[SVC] Engine started (watchdog + forensics + network client).");
            }
            catch (Exception ex)
            {
                NetworkClient.Log("[SVC] StartEngine FAILED: " + ex);
            }
        }

        internal static void StopEngine()
        {
            try { NetworkClient.Stop(); }    catch { }
            try { ForensicsMonitor.Stop(); } catch { }
        }
    }

    /// <summary>
    /// Entry point. Under the SCM (non-interactive) it runs as a service;
    /// launched interactively it runs the same engine in a console loop for debugging.
    /// </summary>
    class Program
    {
        private static readonly ManualResetEvent _quitEvent = new ManualResetEvent(false);

        static void Main(string[] args)
        {
            // Catch unhandled exceptions — log instead of dying silently.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { NetworkClient.Log("[SVC] UNHANDLED: " + e.ExceptionObject); } catch { }
            };

            if (!Environment.UserInteractive)
            {
                // Running under the Windows Service Control Manager
                ServiceBase.Run(new RASDConnectorService());
            }
            else
            {
                // Interactive / debug mode — run the engine and block
                RASDConnectorService.StartEngine();
                _quitEvent.WaitOne();
            }
        }
    }
}
