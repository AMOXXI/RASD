using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RASD_Connector
{
    /// <summary>
    /// Monitors the Downloads folder of every real user profile for new files.
    /// Calculates MD5 hash and reports to the RASD API server as a forensic event.
    ///
    /// The connector runs as LocalSystem, whose own profile has no Downloads
    /// folder, so each C:\Users\&lt;name&gt;\Downloads gets its own watcher. Profiles
    /// are re-scanned every 60 s so a user logging in for the first time is picked up.
    /// </summary>
    public static class ForensicsMonitor
    {
        private const string USERS_ROOT = @"C:\Users";
        private const int    RESCAN_MS  = 60000;
        private const int    HASH_ATTEMPTS = 3;
        private const int    HASH_RETRY_MS = 1000;

        // Built-in / shared profiles that are never watched
        private static readonly string[] SkippedProfiles =
            { "Default", "Default User", "Public", "All Users" };

        // One watcher per Downloads folder (guarded by _watchLock)
        private static readonly Dictionary<string, FileSystemWatcher> _watchers =
            new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _watchLock = new object();

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
            lock (_watchLock)
            {
                foreach (var w in _watchers.Values)
                {
                    try
                    {
                        w.EnableRaisingEvents = false;
                        w.Dispose();
                    }
                    catch { }
                }
                _watchers.Clear();
            }
        }

        private static void MonitorLoop()
        {
            NetworkClient.Log("[FORENSICS] Monitor started — watching Downloads of every user profile under " +
                USERS_ROOT + " (re-scan every " + (RESCAN_MS / 1000) + "s).");

            while (_running)
            {
                try
                {
                    ScanProfiles();
                }
                catch (Exception ex)
                {
                    NetworkClient.Log("[FORENSICS] Monitor error: " + ex.Message);
                }

                // Sleep until the next re-scan, but stop promptly on shutdown.
                for (int waited = 0; waited < RESCAN_MS && _running; waited += 1000)
                {
                    Thread.Sleep(1000);
                }
            }
        }

        /// <summary>Adds a watcher for every new profile Downloads folder; drops vanished ones.</summary>
        private static void ScanProfiles()
        {
            if (!Directory.Exists(USERS_ROOT))
            {
                NetworkClient.Log("[FORENSICS] Users folder not found: " + USERS_ROOT);
                return;
            }

            foreach (string profile in Directory.GetDirectories(USERS_ROOT))
            {
                string name = Path.GetFileName(profile);
                if (IsSkippedProfile(name)) continue;

                FileAttributes attr;
                try { attr = File.GetAttributes(profile); }
                catch { continue; }
                if ((attr & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                    continue;

                string downloads = Path.Combine(profile, "Downloads");
                if (!Directory.Exists(downloads)) continue;

                AddWatcher(downloads);
            }

            // A deleted profile leaves a dead watcher behind; remove it so the
            // folder can be watched again if it is ever re-created.
            lock (_watchLock)
            {
                var gone = new List<string>();
                foreach (string path in _watchers.Keys)
                {
                    if (!Directory.Exists(path)) gone.Add(path);
                }
                foreach (string path in gone)
                {
                    try
                    {
                        _watchers[path].EnableRaisingEvents = false;
                        _watchers[path].Dispose();
                    }
                    catch { }
                    _watchers.Remove(path);
                    NetworkClient.Log("[FORENSICS] Stopped watching (folder gone): " + path);
                }
            }
        }

        private static bool IsSkippedProfile(string name)
        {
            foreach (string skip in SkippedProfiles)
            {
                if (string.Equals(name, skip, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void AddWatcher(string downloadsPath)
        {
            lock (_watchLock)
            {
                if (!_running || _watchers.ContainsKey(downloadsPath)) return;

                try
                {
                    var watcher = new FileSystemWatcher(downloadsPath)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                        IncludeSubdirectories = false
                    };

                    watcher.Created += OnFileCreated;
                    // Browsers download to a temp name and rename it when finished.
                    watcher.Renamed += OnFileRenamed;
                    watcher.Error += (s, e) =>
                    {
                        NetworkClient.Log("[FORENSICS] Watcher error on " + downloadsPath + ": " +
                            e.GetException().Message);
                    };
                    watcher.EnableRaisingEvents = true;

                    _watchers[downloadsPath] = watcher;
                    NetworkClient.Log("[FORENSICS] Watching: " + downloadsPath);
                }
                catch (Exception ex)
                {
                    NetworkClient.Log("[FORENSICS] Could not watch " + downloadsPath + ": " + ex.Message);
                }
            }
        }

        private static void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            // e.FullPath / e.Name are the NEW name — same handling as a new file.
            OnFileCreated(sender, e);
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
                    if (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                        fileName.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) ||
                        fileName.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
                        fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("~"))
                    {
                        return;
                    }

                    // Wait until file is accessible (up to 5 seconds)
                    if (!WaitForFile(filePath, 5000))
                    {
                        NetworkClient.Log("[FORENSICS] File locked, skipping: " + filePath);
                        return;
                    }

                    NetworkClient.Log("[FORENSICS] New file detected: " + filePath);

                    // Calculate MD5 hash (short retry if the file is still locked)
                    string md5Hash = null;
                    for (int attempt = 1; attempt <= HASH_ATTEMPTS; attempt++)
                    {
                        md5Hash = CalculateMD5(filePath);
                        if (!string.IsNullOrEmpty(md5Hash)) break;
                        if (attempt < HASH_ATTEMPTS) Thread.Sleep(HASH_RETRY_MS);
                    }
                    if (string.IsNullOrEmpty(md5Hash))
                    {
                        NetworkClient.Log("[FORENSICS] Could not hash file: " + filePath);
                        return;
                    }

                    NetworkClient.Log("[FORENSICS] MD5: " + md5Hash);

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
                        NetworkClient.Log("[FORENSICS] *** MALICIOUS HASH DETECTED: " + fileName + " ***");
                    }

                    // Get file size
                    long fileSize = 0;
                    try { fileSize = new FileInfo(filePath).Length; } catch { }

                    // POST to API
                    ReportToApi(fileName, md5Hash, fileSize, severity, filePath);
                }
                catch (Exception ex)
                {
                    NetworkClient.Log("[FORENSICS] Error processing file: " + ex.Message);
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
                NetworkClient.Log("[FORENSICS] Hash error: " + ex.Message);
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
                NetworkClient.Log("[FORENSICS] No NodeId set, cannot report to API.");
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
                    NetworkClient.Log("[FORENSICS] Reported to API: " + response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                NetworkClient.Log("[FORENSICS] API report failed: " + ex.Message);
            }
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }
}
