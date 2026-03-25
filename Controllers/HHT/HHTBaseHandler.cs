using SAP.Middleware.Connector;
using System.Linq;
using System;
using System.Configuration;
using System.Text;

namespace V2HHTMiddleware.Controllers.HHT
{
    /// <summary>
    /// Abstract base for all HHT opcode handlers.
    ///
    /// Android HHT Protocol:
    ///   Request:  POST body = "opcode#param1#param2#..."  (# delimited, rows as CSV within a param)
    ///   Response: "S#message"  on success
    ///             "E#message"  on error
    ///             "1#WERKS"    for login success (scnrec)
    ///             "0"          for login failure
    ///
    /// SAP connections use NCo connection pool — single static destination per env.
    /// Pool is sized for 1000+ concurrent devices via SAP_POOL_SIZE / SAP_PEAK_LIMIT app settings.
    /// </summary>
    public abstract class HHTBaseHandler
    {
        // ── Request parts ─────────────────────────────────────────────────────
        protected string[] Parts { get; private set; }

        /// <summary>Safely get part by index. Returns "" if out of range.</summary>
        protected string P(int i) => (Parts != null && i < Parts.Length) ? Parts[i] : "";

        public void SetRequest(string rawBody)
        {
            Parts = (rawBody ?? "").Split('#');
        }

        public abstract string Execute();

        // ── SAP connection pool ───────────────────────────────────────────────
        private static readonly object _initLock = new object();
        private static bool _initialized = false;

        public static void InitializeSapPool()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;

                var sapHost = Cfg("SAP_HOST", "auto");
                if (sapHost == "auto" || string.IsNullOrEmpty(sapHost))
                {
                    sapHost = DiscoverHCProxyIP(3302) ?? "127.0.0.1";
                }

                // ── PROD destination ──────────────────────────────────────────
                // Client-only connection: AppServerHost + SystemNumber + credentials.
                // DO NOT set GatewayHost/GatewayService on a client destination —
                // those params trigger SAP's REMOTE_GATEWAY handshake (SAP tries to
                // call back to the caller's gateway), which causes WSAECONNRESET
                // across the Hybrid Connection tunnel.
                var prodProps = new RfcConfigParameters();
                prodProps.Add(RfcConfigParameters.Name,          "HHT_PROD");
                prodProps.Add(RfcConfigParameters.AppServerHost, sapHost);
                prodProps.Add(RfcConfigParameters.Client,         Cfg("SAP_CLIENT",  "600"));
                prodProps.Add(RfcConfigParameters.SystemNumber,   Cfg("SAP_SYSNUM",  "02"));
                prodProps.Add(RfcConfigParameters.User,           Cfg("SAP_USER",    "BATCHUSER"));
                prodProps.Add(RfcConfigParameters.Password,       Cfg("SAP_PASS",    ""));
                prodProps.Add(RfcConfigParameters.Language,       "EN");
                prodProps.Add(RfcConfigParameters.PoolSize,       Cfg("SAP_POOL_SIZE",  "50"));
                prodProps.Add(RfcConfigParameters.MaxPoolSize,    Cfg("SAP_PEAK_LIMIT", "300"));

                // ── QA destination ────────────────────────────────────────────
                var qaProps = new RfcConfigParameters();
                qaProps.Add(RfcConfigParameters.Name,          "HHT_QA");
                qaProps.Add(RfcConfigParameters.AppServerHost,  Cfg("SAP_QA_HOST",   "192.168.144.179"));
                qaProps.Add(RfcConfigParameters.Client,         Cfg("SAP_QA_CLIENT", "600"));
                qaProps.Add(RfcConfigParameters.SystemNumber,   Cfg("SAP_QA_SYSNUM", "00"));
                qaProps.Add(RfcConfigParameters.User,           Cfg("SAP_QA_USER",   "BATCHUSER"));
                qaProps.Add(RfcConfigParameters.Password,       Cfg("SAP_QA_PASS",   ""));
                qaProps.Add(RfcConfigParameters.Language,       "EN");
                qaProps.Add(RfcConfigParameters.PoolSize,       "5");
                qaProps.Add(RfcConfigParameters.MaxPoolSize,    "20");

                // Warm pool — absorbs latency from first device request
                try { RfcDestinationManager.GetDestination(prodProps); } catch { }
                try { RfcDestinationManager.GetDestination(qaProps);  } catch { }

                _initialized = true;
            }
        }

        private static string Cfg(string key, string def = "")
            => ConfigurationManager.AppSettings[key] ?? def;

        // ── Get destination ───────────────────────────────────────────────────

        protected static RfcDestination Prod() => RfcDestinationManager.GetDestination("HHT_PROD");
        protected static RfcDestination QA()   => RfcDestinationManager.GetDestination("HHT_QA");

        // ── Response builders ─────────────────────────────────────────────────

        protected static string Ok(string msg = "")         => "S#" + msg;
        protected static string Err(string msg = "")        => "E#" + msg;
        protected static string TypeMsg(IRfcStructure ret)  => ret.GetString("TYPE") + "#" + ret.GetString("MESSAGE");

        /// <summary>Returns "S#msg" or "E#msg" based on EX_RETURN structure TYPE field.</summary>
        protected static string OkOrErr(IRfcStructure ret, string successSuffix = "")
        {
            string t = ret.GetString("TYPE"), m = ret.GetString("MESSAGE");
            return t == "E" ? "E#" + m : "S#" + m + successSuffix;
        }

        // ── Table serialisers ─────────────────────────────────────────────────

        protected static string Tbl(IRfcTable t, params string[] fields)
        {
            if (t == null || t.RowCount == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < t.RowCount; i++)
                foreach (var f in fields)
                { sb.Append(t[i].GetString(f)); sb.Append('#'); }
            return sb.ToString();
        }

        protected static string EanData(IRfcTable t)
            => Tbl(t, "MANDT", "MATNR", "EAN11", "UMREZ", "EANNR");

        // ── HC proxy discovery ────────────────────────────────────────────────
        // Azure Hybrid Connection creates a loopback alias (127.0.0.x) that forwards
        // to the on-prem endpoint. We scan to find it since the IP is not fixed.
        // Result is cached for the lifetime of the App Service instance.

        private static volatile string _cachedHCProxyIP = null;
        private static readonly object _cacheLock = new object();

        private static string DiscoverHCProxyIP(int port)
        {
            if (_cachedHCProxyIP != null) return _cachedHCProxyIP;
            lock (_cacheLock)
            {
                if (_cachedHCProxyIP != null) return _cachedHCProxyIP;

                var found = new System.Collections.Concurrent.ConcurrentBag<int>();
                var tasks = new System.Collections.Generic.List<System.Threading.Tasks.Task>();

                for (int i = 1; i <= 254; i++)
                {
                    int idx = i;
                    tasks.Add(System.Threading.Tasks.Task.Run(() =>
                    {
                        string ip = $"127.0.0.{idx}";
                        try
                        {
                            using (var sock = new System.Net.Sockets.Socket(
                                System.Net.Sockets.AddressFamily.InterNetwork,
                                System.Net.Sockets.SocketType.Stream,
                                System.Net.Sockets.ProtocolType.Tcp))
                            {
                                sock.Blocking = false;
                                try { sock.Connect(ip, port); } catch { }
                                var write = new System.Collections.Generic.List<System.Net.Sockets.Socket> { sock };
                                var error = new System.Collections.Generic.List<System.Net.Sockets.Socket> { sock };
                                System.Net.Sockets.Socket.Select(null, write, error, 150000);
                                if (write.Count > 0 && error.Count == 0)
                                    found.Add(idx);
                            }
                        }
                        catch { }
                    }));
                }

                System.Threading.Tasks.Task.WaitAll(tasks.ToArray(), 3000);
                // Take lowest index for determinism (HC typically uses a stable alias)
                int best = found.OrderBy(x => x).FirstOrDefault();
                _cachedHCProxyIP = best > 0 ? $"127.0.0.{best}" : null;
                return _cachedHCProxyIP;
            }
        }
    }
}
