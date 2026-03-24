using System;
using System.Configuration;
using StackExchange.Redis;

namespace V2HHTMiddleware.Infrastructure
{
    /// <summary>
    /// Redis cache for read-heavy HHT opcodes.
    /// 
    /// CACHED (rarely changes during a shift):
    ///   Bin lists, bin-material maps, EAN data, packing mats, SLOC, major category
    /// 
    /// NOT CACHED (transactional / always fresh):
    ///   Stock quantities, picklists, any writes, scnrec login
    /// 
    /// FAILSAFE: Redis down → returns null → handler calls SAP directly.
    ///           Zero app downtime if Redis is unavailable.
    /// </summary>
    public static class HHTCache
    {
        private static readonly object _initLock = new object();
        private static ConnectionMultiplexer _conn;
        private static bool _initialized;

        private static IDatabase Db
        {
            get
            {
                if (!_initialized)
                {
                    lock (_initLock)
                    {
                        if (!_initialized)
                        {
                            _initialized = true;
                            try
                            {
                                var host = ConfigurationManager.AppSettings["REDIS_HOST"] ?? "";
                                var key  = ConfigurationManager.AppSettings["REDIS_KEY"]  ?? "";
                                var ssl  = (ConfigurationManager.AppSettings["REDIS_SSL"] ?? "true") == "true";

                                if (string.IsNullOrEmpty(host) || key == "PENDING" || string.IsNullOrEmpty(key))
                                    return null;

                                var cfg = new ConfigurationOptions
                                {
                                    Password         = key,
                                    Ssl              = ssl,
                                    ConnectTimeout   = 3000,
                                    SyncTimeout      = 2000,
                                    AbortOnConnectFail = false
                                };
                                cfg.EndPoints.Add(host, ssl ? 6380 : 6379);
                                _conn = ConnectionMultiplexer.Connect(cfg);
                            }
                            catch { _conn = null; }
                        }
                    }
                }
                try { return _conn?.GetDatabase(); }
                catch { return null; }
            }
        }

        // ── Core get/set ──────────────────────────────────────────────────────

        public static string Get(string key)
        {
            try
            {
                var db = Db; if (db == null) return null;
                var v = db.StringGet(key);
                return v.HasValue ? (string)v : null;
            }
            catch { return null; }
        }

        public static void Set(string key, string value, TimeSpan ttl)
        {
            try { Db?.StringSet(key, value, ttl); }
            catch { }
        }

        // ── Typed helpers ─────────────────────────────────────────────────────

        public static string GetBins(string werks)        => Get($"bins:{werks}");
        public static void   SetBins(string werks, string v)  => Set($"bins:{werks}", v, TimeSpan.FromMinutes(10));

        public static string GetBinMc(string werks, bool qa = false) => Get($"binmc:{(qa?"qa:":"")}{werks}");
        public static void   SetBinMc(string werks, string v, bool qa = false) => Set($"binmc:{(qa?"qa:":"")}{werks}", v, TimeSpan.FromMinutes(10));

        public static string GetEan(string ean11)         => Get($"ean:{ean11}");
        public static void   SetEan(string ean11, string v)   => Set($"ean:{ean11}", v, TimeSpan.FromMinutes(30));

        public static string GetPackMats(string lgnum)    => Get($"packmats:{lgnum}");
        public static void   SetPackMats(string lgnum, string v) => Set($"packmats:{lgnum}", v, TimeSpan.FromMinutes(60));

        public static string GetSloc(string werks)        => Get($"sloc:{werks}");
        public static void   SetSloc(string werks, string v)  => Set($"sloc:{werks}", v, TimeSpan.FromMinutes(60));

        public static string GetMajorCat(string werks)    => Get($"majorcat:{werks}");
        public static void   SetMajorCat(string werks, string v) => Set($"majorcat:{werks}", v, TimeSpan.FromMinutes(60));
    }
}
