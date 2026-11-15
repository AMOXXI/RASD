using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RASD.Shared
{
    /// <summary>
    /// Phase-4 fix A1 — authenticated LAN discovery.
    ///
    /// Before this, any host on the LAN could answer a "RASD_DISCOVER" probe with
    /// "RASD_ADMIN:&lt;ip&gt;" and hijack every agent (MITM). Discovery is now signed
    /// with a shared secret so agents only ever accept a reply from the real admin.
    ///
    /// KEY DISTRIBUTION (chosen approach — the simpler of the two options):
    /// the 32-byte key is generated once at BUILD time and embedded in Payload.zip
    /// as "node.key". Every machine installed from the same installer therefore
    /// shares the key: the admin install writes it to C:\ProgramData\RASD\node.key
    /// and each client install writes the same file from the same payload. No
    /// pairing code is required. The key file is ACL-locked (Admins/SYSTEM only)
    /// by the installer so a standard user cannot read or replace it.
    ///
    /// WIRE FORMAT (pipe-delimited, base64 fields):
    ///   probe : RASD_DISCOVER|&lt;nonceB64&gt;|&lt;HMAC(nonce)&gt;
    ///   reply : RASD_ADMIN|&lt;ip&gt;|&lt;nonceB64&gt;|&lt;HMAC(ip + "|" + nonce)&gt;
    /// The agent rejects any reply whose HMAC fails or whose nonce differs from the
    /// one it sent (blocks both spoofing and replay).
    /// </summary>
    public static class DiscoverySecurity
    {
        public const string KEY_FILE      = @"C:\ProgramData\RASD\node.key";
        public const string PROBE_TAG     = "RASD_DISCOVER";
        public const string REPLY_TAG     = "RASD_ADMIN";

        private static readonly object _keyLock = new object();
        private static byte[] _cachedKey;

        /// <summary>Loads the shared key from disk (cached). Returns null if absent/invalid.</summary>
        public static byte[] LoadKey()
        {
            lock (_keyLock)
            {
                if (_cachedKey != null) return _cachedKey;
                try
                {
                    if (!File.Exists(KEY_FILE)) return null;
                    string b64 = File.ReadAllText(KEY_FILE).Trim();
                    if (b64.Length == 0) return null;
                    byte[] key = Convert.FromBase64String(b64);
                    if (key.Length < 16) return null;   // too short to be the 32-byte key
                    _cachedKey = key;
                    return _cachedKey;
                }
                catch { return null; }
            }
        }

        public static bool KeyAvailable() { return LoadKey() != null; }

        /// <summary>Generates a fresh 32-byte key encoded as base64 (used at build/install time).</summary>
        public static string GenerateKeyBase64()
        {
            byte[] key = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(key);
            return Convert.ToBase64String(key);
        }

        public static string NewNonce()
        {
            byte[] n = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(n);
            return Convert.ToBase64String(n);
        }

        private static string Hmac(byte[] key, string message)
        {
            using (var h = new HMACSHA256(key))
                return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(message)));
        }

        // ---- Agent: build a signed probe ----
        public static string BuildProbe(byte[] key, string nonce)
        {
            return PROBE_TAG + "|" + nonce + "|" + Hmac(key, nonce);
        }

        // ---- Admin: validate a probe, returning the nonce to echo ----
        public static bool TryValidateProbe(byte[] key, string message, out string nonce)
        {
            nonce = null;
            if (key == null || string.IsNullOrEmpty(message)) return false;

            string[] p = message.Split('|');
            if (p.Length != 3 || p[0] != PROBE_TAG) return false;

            string candidateNonce = p[1];
            string mac            = p[2];
            if (!FixedTimeEquals(mac, Hmac(key, candidateNonce))) return false;

            nonce = candidateNonce;
            return true;
        }

        // ---- Admin: build a signed reply ----
        public static string BuildReply(byte[] key, string ip, string nonce)
        {
            return REPLY_TAG + "|" + ip + "|" + nonce + "|" + Hmac(key, ip + "|" + nonce);
        }

        // ---- Agent: validate a reply against the nonce we sent ----
        public static bool TryValidateReply(byte[] key, string message, string expectedNonce, out string ip)
        {
            ip = null;
            if (key == null || string.IsNullOrEmpty(message)) return false;

            string[] p = message.Split('|');
            if (p.Length != 4 || p[0] != REPLY_TAG) return false;

            string candidateIp    = p[1];
            string candidateNonce = p[2];
            string mac            = p[3];

            // Nonce must match the one we just sent (anti-replay).
            if (!string.IsNullOrEmpty(expectedNonce) && candidateNonce != expectedNonce) return false;
            if (!FixedTimeEquals(mac, Hmac(key, candidateIp + "|" + candidateNonce))) return false;

            ip = candidateIp;
            return true;
        }

        /// <summary>
        /// Validate an UNPROMPTED announcement (the admin's periodic broadcast has no
        /// agent nonce). The HMAC still covers ip+nonce, so an attacker cannot forge a
        /// different IP; replay of the genuine admin's own announcement is harmless.
        /// </summary>
        public static bool TryValidateAnnouncement(byte[] key, string message, out string ip)
        {
            return TryValidateReply(key, message, null, out ip);
        }

        /// <summary>Constant-time comparison to avoid timing side channels on the MAC.</summary>
        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            byte[] ba = Encoding.UTF8.GetBytes(a);
            byte[] bb = Encoding.UTF8.GetBytes(b);
            if (ba.Length != bb.Length) return false;
            int diff = 0;
            for (int i = 0; i < ba.Length; i++) diff |= ba[i] ^ bb[i];
            return diff == 0;
        }
    }
}
