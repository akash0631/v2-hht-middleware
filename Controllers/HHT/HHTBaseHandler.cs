using SAP.Middleware.Connector;
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
        // Destinations are registered once at startup and reused for all requests.
        // NCo pool handles thread safety and connection reuse internally.

        private static readonly object _initLock = new object();
        private static bool _initialized = false;

        /// <summary>
        /// Register SAP destinations with NCo connection pool.
        /// Called once at application startup.
        /// Settings read from Azure App Settings (override Web.config defaults).
        /// </summary>
        public static void InitializeSapPool()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;

                // Register Prod destination
                var prodProps = new RfcConfigParameters();
                prodProps.Add(RfcConfigParameters.Name,          "HHT_PROD");
                var sapHost = Cfg("SAP_HOST", "auto");
                if (sapHost == "auto" || string.IsNullOrEmpty(sapHost))
                {
                    sapHost = DiscoverHCProxyIP(3302) ?? "127.0.0.1";
                }
                prodProps.Add(RfcConfigParameters.AppServerHost, sapHost);
                prodProps.Add(RfcConfigParameters.Client,         Cfg("SAP_CLIENT",  "600"));
                prodProps.Add(RfcConfigParameters.SystemNumber,   Cfg("SAP_SYSNUM",  "02"));
                // SystemID removed - prevents SAP remote gateway callback to caller
                prodProps.Add(RfcConfigParameters.User,           Cfg("SAP_USER",    "PIUSER"));
                prodProps.Add(RfcConfigParameters.Password,       Cfg("SAP_PASS",    ""));
                prodProps.Add(RfcConfigParameters.Language,       "EN");
                prodProps.Add(RfcConfigParameters.PoolSize,       Cfg("SAP_POOL_SIZE",  "50"));
                prodProps.Add(RfcConfigParameters.MaxPoolSize,    Cfg("SAP_PEAK_LIMIT", "300"));

                // Register QA destination (for testing with X-HHT-Env: QA header)
                var qaProps = new RfcConfigParameters();
                qaProps.Add(RfcConfigParameters.Name,          "HHT_QA");
                qaProps.Add(RfcConfigParameters.AppServerHost,  Cfg("SAP_QA_HOST",   "192.168.144.179"));
                qaProps.Add(RfcConfigParameters.Client,         Cfg("SAP_QA_CLIENT", "600"));
                qaProps.Add(RfcConfigParameters.SystemNumber,   Cfg("SAP_QA_SYSNUM", "00"));
                qaProps.Add(RfcConfigParameters.User,           Cfg("SAP_QA_USER",   "PIUSER"));
                qaProps.Add(RfcConfigParameters.Password,       Cfg("SAP_QA_PASS",   ""));
                qaProps.Add(RfcConfigParameters.Language,       "EN");
                qaProps.Add(RfcConfigParameters.PoolSize,       "5");
                qaProps.Add(RfcConfigParameters.MaxPoolSize,    "20");

                // Warm the pool — pre-creates connections so first device request isn't slow
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

        /// <summary>Serialize an IRfcTable to "#"-delimited string. Each row: field1#field2#...</summary>
        protected static string Tbl(IRfcTable t, params string[] fields)
        {
            if (t == null || t.RowCount == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < t.RowCount; i++)
                foreach (var f in fields)
                { sb.Append(t[i].GetString(f)); sb.Append('#'); }
            return sb.ToString();
        }

        /// <summary>Standard EAN_DATA table — appears in almost every store opcode.</summary>
        protected static string EanData(IRfcTable t)
            => Tbl(t, "MANDT", "MATNR", "EAN11", "UMREZ", "EANNR");

        /// <summary>
        /// Discovers the Azure Hybrid Connection local proxy IP by scanning 127.0.0.x range.
        /// HC creates a loopback alias that forwards to the on-prem endpoint.
        /// </summary>
        private static string DiscoverHCProxyIP(int port)
        {
            for (int i = 1; i <= 254; i++)
            {
                string ip = $"127.0.0.{i}";
                try
                {
                    using (var client = new System.Net.Sockets.TcpClient())
                    {
                        var result = client.BeginConnect(ip, port, null, null);
                        bool success = result.AsyncWaitHandle.WaitOne(100);
                        if (success && client.Connected)
                        {
                            client.EndConnect(result);
                            return ip;
                        }
                        try { client.EndConnect(result); } catch { }
                    }
                }
                catch { }
            }
            return null;
        }
    }
}
