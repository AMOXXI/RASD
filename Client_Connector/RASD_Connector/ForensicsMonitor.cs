using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RASD_Connector
{
    /// <summary>
    /// Monitors the user's Downloads folder for new files.
    /// Calculates MD5 hash and reports to the RASD API server as a forensic event.
    /// </summary>
    public static class ForensicsMonitor
    {
        private static FileSystemWatcher _watcher;
        private static bool _running;
        private static Thread _thread;

        // Set by NetworkClient when connection is established
        public static string AdminIp { get; set; } = "127.0.0.1";
        public static string NodeId { get; set; } = "";

        // Known malicious MD5 hashes (test set for demo)
        private static readonly string[] MaliciousHashes = new string[]
        {
            "44d88612fea8a8f36de82e1278abb02f", // EICAR test file
            "e99a18c428cb38d5f260853678922e03", // Common test hash
            "5d41402abc4b2a76b9719d911017c592", // "hello" hash
            "098f6bcd4621d373cade4e832627b4f6", // "test" hash
            "d8e8fca2dc0f896fd7cb4cb0031ba249"  // "test\n" hash
        };

        public static void Start()
        {
            if (_running) return;
            _running = true;

            _thread = new Thread(MonitorLoop)
            {
                IsBackground = true,
                Name = "ForensicsMonitor"
            };
            _thread.Start();
        }

        public static void Stop()
        {
            _running = false;
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        private static void MonitorLoop()
        {
            try
            {
                string downloadsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads"
                );

                if (!Directory.Exists(downloadsPath))
                {
                    Console.WriteLine("[FORENSICS] Downloads folder not found: " + downloadsPath);
                    return;
                }

                Console.WriteLine("[FORENSICS] Monitoring: " + downloadsPath);

                _watcher = new FileSystemWatcher(downloadsPath)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };

                _watcher.Created += OnFileCreated;
                _watcher.Error += (s, e) =>
                {
                    Console.WriteLine("[FORENSICS] Watcher error: " + e.GetException().Message);
                };

                // Keep thread alive
                while (_running)
                {
                    Thread.Sleep(1000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FORENSICS] Monitor error: " + ex.Message);
            }
        }

        private static void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            // Run in a separate thread to not block the watcher
            ThreadPool.QueueUserWorkItem((_) =>
            {
                try
                {
                    string filePath = e.FullPath;
                    string fileName = e.Name;

                    // Wait for file to finish writing
                    Thread.Sleep(500);

                    // Skip temporary/partial download files
                    if (fileName.EndsWith(".tmp") || fileName.EndsWith(".crdownload") ||
                        fileName.EndsWith(".partial") || fileName.StartsWith("~"))
                    {
                        return;
                    }

                    // Wait until file is accessible (up to 5 seconds)
                    if (!WaitForFile(filePath, 5000))
                    {
                        Console.WriteLine("[FORENSICS] File locked, skipping: " + fileName);
                        return;
                    }

                    Console.WriteLine("[FORENSICS] New file detected: " + fileName);

                    // Calculate MD5 hash
                    string md5Hash = CalculateMD5(filePath);
                    if (string.IsNullOrEmpty(md5Hash))
                    {
                        Console.WriteLine("[FORENSICS] Could not hash file: " + fileName);
                        return;
                    }

                    Console.WriteLine("[FORENSICS] MD5: " + md5Hash);

                    // Determine severity
                    string severity = "INFO";
                    bool isMalicious = false;
                    foreach (string knownHash in MaliciousHashes)
                    {
                        if (md5Hash.Equals(knownHash, StringComparison.OrdinalIgnoreCase))
                        {
                            severity = "CRITICAL";
                            isMalicious = true;
                            break;
                        }
                    }

                    if (isMalicious)
                    {
                        Console.WriteLine("[FORENSICS] *** MALICIOUS HASH DETECTED: " + fileName + " ***");
                    }

                    // Get file size
                    long fileSize = 0;
                    try { fileSize = new FileInfo(filePath).Length; } catch { }

                    // POST to API
                    ReportToApi(fileName, md5Hash, fileSize, severity, filePath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[FORENSICS] Error processing file: " + ex.Message);
                }
            });
        }

        private static string CalculateMD5(string filePath)
        {
            try
            {
                using (var md5 = MD5.Create())
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] hashBytes = md5.ComputeHash(stream);
                    StringBuilder sb = new StringBuilder(32);
                    for (int i = 0; i < hashBytes.Length; i++)
                    {
                        sb.Append(hashBytes[i].ToString("x2"));
                    }
                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FORENSICS] Hash error: " + ex.Message);
                return null;
            }
        }

        private static bool WaitForFile(string path, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        return true;
                    }
                }
                catch (IOException)
                {
                    Thread.Sleep(200);
                    waited += 200;
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        private static void ReportToApi(string fileName, string md5Hash, long fileSize, string severity, string filePath)
        {
            if (string.IsNullOrEmpty(NodeId))
            {
                Console.WriteLine("[FORENSICS] No NodeId set, cannot report to API.");
                return;
            }

            try
            {
                string url = string.Format("http://{0}:3000/api/nodes/{1}/forensic", AdminIp, NodeId);
                string json = string.Format(
                    "{{\"event_type\":\"FILE_DOWNLOAD\",\"file_name\":\"{0}\",\"file_hash\":\"{1}\",\"file_size\":{2},\"severity\":\"{3}\",\"details\":\"Detected in Downloads: {4}\"}}",
                    EscapeJson(fileName),
                    md5Hash,
                    fileSize,
                    severity,
                    EscapeJson(filePath)
                );

                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 5000;

                byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
                request.ContentLength = bodyBytes.Length;

                using (var stream = request.GetRequestStream())
                {
                    stream.Write(bodyBytes, 0, bodyBytes.Length);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    Console.WriteLine("[FORENSICS] Reported to API: " + response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FORENSICS] API report failed: " + ex.Message);
            }
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }
}
