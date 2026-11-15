using System;
using System.IO;
using System.Text;

namespace RASD_AdminWatcher
{
    public static class ErrorLogger
    {
        private const string LOG_DIR  = @"C:\Program Files\RASD\Logs";
        private const string LOG_FILE = LOG_DIR + @"\RASD_errors.log";
        private const long   MAX_BYTES = 5 * 1024 * 1024; // rotate at 5 MB

        private static readonly object _lock = new object();

        public static void Error(string msg) => Write("ERROR", msg);
        public static void Warn(string msg)  => Write("WARN",  msg);
        public static void Info(string msg)  => Write("INFO",  msg);

        public static void Write(string level, string message)
        {
            try
            {
                if (!Directory.Exists(LOG_DIR))
                    Directory.CreateDirectory(LOG_DIR);

                RotateIfNeeded();

                string line = string.Format("[{0}] [{1}] {2}{3}",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    level.PadRight(5), message, Environment.NewLine);

                lock (_lock)
                    File.AppendAllText(LOG_FILE, line, Encoding.UTF8);
            }
            catch { /* logging must never crash the app */ }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                if (!File.Exists(LOG_FILE)) return;
                if (new FileInfo(LOG_FILE).Length < MAX_BYTES) return;

                string archive = LOG_FILE.Replace(".log",
                    "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
                File.Move(LOG_FILE, archive);
            }
            catch { }
        }
    }
}
