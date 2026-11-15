using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RASD_Connector
{
    /// <summary>
    /// Dual-Process Watchdog Link:
    /// Monitors RASD_Protector.exe. If it is killed, it instantly restarts it.
    /// </summary>
    public static class WatchdogLink
    {
        private const string PROTECTOR_EXE = "RASD_Protector.exe";
        private const int POLL_MS = 2000;

        public static void StartProtectorMonitor()
        {
            Task.Run(() => MonitorLoop());
        }

        private static async Task MonitorLoop()
        {
            string protectorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PROTECTOR_EXE);

            while (true)
            {
                try
                {
                    bool isRunning = false;
                    foreach (var proc in Process.GetProcessesByName("RASD_Protector"))
                    {
                        isRunning = true;
                        break;
                    }

                    if (!isRunning && File.Exists(protectorPath))
                    {
                        // Protector is dead. Resurrect it.
                        var psi = new ProcessStartInfo(protectorPath)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        Process.Start(psi);
                    }
                }
                catch
                {
                    // Silent catch
                }

                await Task.Delay(POLL_MS).ConfigureAwait(false);
            }
        }
    }
}
