using System;
using System.Collections.Concurrent;

namespace V2HHTMiddleware.Infrastructure
{
    /// <summary>
    /// In-memory cache for HHT middleware (per-instance).
    /// 
    /// CACHED (rarely changes during a shift):
    ///   Bin lists, bin-material maps, EAN data, packing mats, SLOC, major category
    /// 
    /// NOT CACHED: stock levels, picklists, writes — always fresh from SAP.
    /// 
    /// Each Azure instance has its own cache. With 2 instances, worst case is
    /// 2 SAP calls per TTL window per opcode — totally acceptable.
    /// </summary>
    public static class HHTCache
    {
        private class CacheEntry
        {
            public string Data;
            public DateTime Expiry;
        }

        private static readonly ConcurrentDictionary<string, CacheEntry> _cache
            = new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        public static string Get(string key)
        {
            if (_cache.TryGetValue(key, out var entry) && entry.Expiry > DateTime.UtcNow)
                return entry.Data;
            return null;
        }

        public static void Set(string key, string value, TimeSpan ttl)
        {
            _cache[key] = new CacheEntry { Data = value, Expiry = DateTime.UtcNow.Add(ttl) };
        }

        // ── Typed helpers ─────────────────────────────────────────────────────

        public static string GetBins(string werks)       => Get($"bins:{werks}");
        public static void   SetBins(string w, string v) => Set($"bins:{w}", v, TimeSpan.FromMinutes(10));

        public static string GetBinMc(string werks, bool qa = false) => Get($"binmc:{(qa?"qa:":"")}{werks}");
        public static void   SetBinMc(string w, string v, bool qa = false) => Set($"binmc:{(qa?"qa:":"")}{w}", v, TimeSpan.FromMinutes(10));

        public static string GetEan(string ean11)        => Get($"ean:{ean11}");
        public static void   SetEan(string e, string v)  => Set($"ean:{e}", v, TimeSpan.FromMinutes(30));

        public static string GetPackMats(string lgnum)   => Get($"packmats:{lgnum}");
        public static void   SetPackMats(string l, string v) => Set($"packmats:{l}", v, TimeSpan.FromMinutes(60));

        public static string GetSloc(string werks)       => Get($"sloc:{werks}");
        public static void   SetSloc(string w, string v) => Set($"sloc:{w}", v, TimeSpan.FromMinutes(60));

        public static string GetMajorCat(string werks)   => Get($"majorcat:{werks}");
        public static void   SetMajorCat(string w, string v) => Set($"majorcat:{w}", v, TimeSpan.FromMinutes(60));
    }
}
