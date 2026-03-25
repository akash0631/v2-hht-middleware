using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace V2HHTMiddleware.Controllers.HHT
{
    /// <summary>
    /// Discovers the Azure Hybrid Connection loopback alias for the Java middleware
    /// running on 192.168.144.200:9080.
    ///
    /// Azure HC creates a 127.0.0.x alias inside the App Service sandbox that
    /// forwards to the configured on-prem endpoint (192.168.144.200:9080).
    /// The specific IP is not fixed — we scan 127.0.0.1-254 to find it.
    /// Result is cached for the lifetime of the App Service instance.
    /// </summary>
    public static class JavaMWProxy
    {
        private static volatile string _cachedIP = null;
        private static readonly object _lock = new object();
        private const int JavaMWPort = 9080;

        public static string DiscoverIP()
        {
            if (_cachedIP != null) return _cachedIP;
            lock (_lock)
            {
                if (_cachedIP != null) return _cachedIP;

                var found = new ConcurrentBag<int>();
                var tasks = new List<Task>();

                for (int i = 1; i <= 254; i++)
                {
                    int idx = i;
                    tasks.Add(Task.Run(() =>
                    {
                        try
                        {
                            using (var sock = new Socket(
                                AddressFamily.InterNetwork,
                                SocketType.Stream,
                                ProtocolType.Tcp))
                            {
                                sock.Blocking = false;
                                try { sock.Connect($"127.0.0.{idx}", JavaMWPort); } catch { }
                                var write = new List<Socket> { sock };
                                var error = new List<Socket> { sock };
                                Socket.Select(null, write, error, 200000); // 200ms timeout
                                if (write.Count > 0 && error.Count == 0)
                                    found.Add(idx);
                            }
                        }
                        catch { }
                    }));
                }

                Task.WaitAll(tasks.ToArray(), 4000);

                // Take lowest index for determinism
                int best = 0;
                foreach (int idx in found)
                    if (best == 0 || idx < best) best = idx;

                _cachedIP = best > 0 ? $"127.0.0.{best}" : null;
                return _cachedIP;
            }
        }

        /// <summary>Force re-discovery on next call (e.g. after HC reconnect)</summary>
        public static void ResetCache() => _cachedIP = null;
    }
}
