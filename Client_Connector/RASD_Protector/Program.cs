using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RASD_Protector
{
    /// <summary>
    /// RASD_Protector: The Watchdog Process
    /// 
    /// This is the secondary process in the dual-process persistence architecture.
    /// Its ONLY job is to monitor RASD_Connector.exe. If RASD_Connector.exe is killed
    /// via Task Manager, this process immediately restarts it.
    /// </summary>
    class Program
    {
        private const string CONNECTOR_EXE = "RASD_Connector.exe";
        private const int POLL_MS = 2000;

        static void Main(string[] args)
        {
            // Catch unhandled exceptions
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { };

            MonitorConnectorLoop();
        }

        private static void MonitorConnectorLoop()
        {
            string connectorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CONNECTOR_EXE);

            while (true)
            {
                try
                {
                    bool isRunning = false;
                    foreach (var proc in Process.GetProcessesByName("RASD_Connector"))
                    {
                        isRunning = true;
                        break;
                    }

                    if (!isRunning && File.Exists(connectorPath))
                    {
                        // Connector is dead. Resurrect it.
                        var psi = new ProcessStartInfo(connectorPath)
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

                Thread.Sleep(POLL_MS);
            }
        }
    }
}
