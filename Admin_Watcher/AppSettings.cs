using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace RASD_AdminWatcher
{
    public class AppSettings
    {
        private const string SETTINGS_FILE  = @"C:\ProgramData\RASD\settings.ini";
        private const string STARTUP_KEY    = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string STARTUP_NAME   = "RASD";
        private const string RASD_EXE_PATH  = @"C:\Program Files\RASD\RASD.exe";

        private readonly Dictionary<string, string> _data =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public AppSettings() { Load(); }

        // ── Typed properties ──────────────────────────────────────────────
        public bool DarkMode
        {
            get => Read("darkMode", "true") == "true";
            set { Set("darkMode", value ? "true" : "false"); Save(); }
        }

        public bool MinimizeToTray
        {
            get => Read("minimizeToTray", "true") == "true";
            set { Set("minimizeToTray", value ? "true" : "false"); Save(); }
        }

        public bool LaunchOnStartup
        {
            get => IsStartupEnabled();
            set { SetStartup(value); }
        }

        // Enable auto-start once on first run; never override the user afterwards.
        public void EnsureStartupDefault()
        {
            if (Read("startupInitialized", "false") == "true") return;
            SetStartup(true);
            Set("startupInitialized", "true");
            Save();
        }

        // ── File I/O ──────────────────────────────────────────────────────
        private void Load()
        {
            try
            {
                if (!File.Exists(SETTINGS_FILE)) return;
                foreach (string line in File.ReadAllLines(SETTINGS_FILE, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 1) continue;
                    _data[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch { }
        }

        private void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(SETTINGS_FILE);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                foreach (var kv in _data) sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(SETTINGS_FILE, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private string Read(string key, string fallback) =>
            _data.TryGetValue(key, out string v) ? v : fallback;

        private void Set(string key, string value) => _data[key] = value;

        // ── Registry startup entry ────────────────────────────────────────
        private static bool IsStartupEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(STARTUP_KEY, false))
                    return key?.GetValue(STARTUP_NAME) != null;
            }
            catch { return false; }
        }

        private static void SetStartup(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(STARTUP_KEY, true))
                {
                    if (key == null) return;
                    if (enable)
                        key.SetValue(STARTUP_NAME, "\"" + RASD_EXE_PATH + "\"");
                    else
                        key.DeleteValue(STARTUP_NAME, false);
                }
            }
            catch { }
        }
    }
}
