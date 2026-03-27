using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace V2HHTMiddleware.Controllers.HHT
{
    /// <summary>
    /// Discovers the Azure Hybrid Connection loopback alias for the Java middleware
    /// running on 192.168.144.200:9080.
    ///
    /// Azure HC creates a 127.0.0.x alias inside the App Service sandbox that
    /// forwards to the configured on-prem endpoint. The specific IP is not fixed —
    /// we scan 127.0.0.1-254 to find it.
    ///
    /// The discovered IP is persisted to disk so restarts skip the 4s cold scan.
    /// </summary>
    public static class JavaMWProxy
    {
        private static volatile string _cachedIP = null;
        private static readonly object _lock     = new object();
        private const int  JavaMWPort   = 9080;
        private const int  ScanTimeout  = 200000; // 200ms per IP, microseconds
        private const int  WaitTimeout  = 4000;   // 4s max total scan
        private const int  VerifyMs     = 1000;   // re-verify cached IP every 60s
        private static DateTime _lastVerified = DateTime.MinValue;

        // Persist to Azure App Service writable storage
        private static readonly string PersistPath =
            Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? @"D:\home", "data", "hc_ip.txt");

        // ── Public: get the Java base URL ─────────────────────────────────────
        public static string DiscoverIP()
        {
            // 1. In-memory cache — still valid
            if (_cachedIP != null && (DateTime.UtcNow - _lastVerified).TotalSeconds < 60)
                return _cachedIP;

            lock (_lock)
            {
                // Re-check inside lock
                if (_cachedIP != null && (DateTime.UtcNow - _lastVerified).TotalSeconds < 60)
                    return _cachedIP;

                // 2. Try disk-persisted IP first (avoids 4s cold scan on restart)
                if (_cachedIP == null)
                {
                    string diskIP = ReadPersistedIP();
                    if (!string.IsNullOrEmpty(diskIP) && VerifyIP(diskIP))
                    {
                        _cachedIP     = diskIP;
                        _lastVerified = DateTime.UtcNow;
                        return _cachedIP;
                    }
                }

                // 3. Full parallel scan (cold start or disk IP stale)
                string found = ScanAllIPs();
                if (!string.IsNullOrEmpty(found))
                {
                    _cachedIP     = found;
                    _lastVerified = DateTime.UtcNow;
                    PersistIP(found); // write for next restart
                }
                else
                {
                    _cachedIP = null; // signal failure
                }

                return _cachedIP;
            }
        }

        /// <summary>Force re-discovery on next call (e.g. after HC reconnect)</summary>
        public static void ResetCache()
        {
            _cachedIP     = null;
            _lastVerified = DateTime.MinValue;
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>Quick TCP connect to verify a known IP is still reachable.</summary>
        private static bool VerifyIP(string ip)
        {
            try
            {
                using (var sock = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp))
                {
                    sock.Blocking = false;
                    try { sock.Connect(ip, JavaMWPort); } catch { }
                    var write = new List<System.Net.Sockets.Socket> { sock };
                    var error = new List<System.Net.Sockets.Socket> { sock };
                    System.Net.Sockets.Socket.Select(null, write, error, ScanTimeout);
                    return write.Count > 0 && error.Count == 0;
                }
            }
            catch { return false; }
        }

        /// <summary>Parallel scan of 127.0.0.1-254 — same as original logic.</summary>
        private static string ScanAllIPs()
        {
            var found = new ConcurrentBag<int>();
            var tasks = new List<Task>();

            for (int i = 1; i <= 254; i++)
            {
                int idx = i;
                tasks.Add(Task.Run(() =>
                {
                    if (VerifyIP($"127.0.0.{idx}")) found.Add(idx);
                }));
            }

            Task.WaitAll(tasks.ToArray(), WaitTimeout);

            // Pick lowest index for determinism
            int best = 0;
            foreach (int idx in found)
                if (best == 0 || idx < best) best = idx;

            return best > 0 ? $"127.0.0.{best}" : null;
        }

        private static string ReadPersistedIP()
        {
            try
            {
                if (!File.Exists(PersistPath)) return null;
                string line = File.ReadAllText(PersistPath).Trim();
                // Basic sanity: must look like 127.0.0.x
                return line.StartsWith("127.0.0.") ? line : null;
            }
            catch { return null; }
        }

        private static void PersistIP(string ip)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PersistPath));
                File.WriteAllText(PersistPath, ip);
            }
            catch { /* non-fatal */ }
        }
    }
}
