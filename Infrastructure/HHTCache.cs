using System;
using System.Collections.Concurrent;

namespace V2HHTMiddleware.Infrastructure
{
    /// <summary>
    /// Pure in-memory cache — no external dependencies.
    /// Thread-safe. Keyed with TTL. Works across 1000 devices per instance.
    ///
    /// We have 2 Azure instances; each builds its own warm cache after first
    /// request per key. Good enough — bins don't change during a shift.
    ///
    /// CACHED (rarely changes):
    ///   Bin lists, bin-material maps, EAN data, packing mats, SLOC, major category
    ///
    /// NOT CACHED: Stock quantities, picklists, any writes
    /// </summary>
    public static class HHTCache
    {
        private class Entry { public string Value; public DateTime Expiry; }

        private static readonly ConcurrentDictionary<string, Entry> _cache =
            new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public static string Get(string key)
        {
            if (_cache.TryGetValue(key, out var e) && e.Expiry > DateTime.UtcNow)
                return e.Value;
            _cache.TryRemove(key, out _);
            return null;
        }

        public static void Set(string key, string value, TimeSpan ttl)
        {
            _cache[key] = new Entry { Value = value, Expiry = DateTime.UtcNow.Add(ttl) };
        }

        // Typed helpers
        public static string GetBins(string key)      => Get($"bins:{key}");
        public static void   SetBins(string key, string v) => Set($"bins:{key}", v, TimeSpan.FromMinutes(10));

        public static string GetBinMc(string werks, bool qa = false) => Get($"binmc:{(qa?"qa:":"")}{werks}");
        public static void   SetBinMc(string werks, string v, bool qa = false) => Set($"binmc:{(qa?"qa:":"")}{werks}", v, TimeSpan.FromMinutes(10));

        public static string GetEan(string ean11)     => Get($"ean:{ean11}");
        public static void   SetEan(string ean11, string v) => Set($"ean:{ean11}", v, TimeSpan.FromMinutes(30));

        public static string GetPackMats(string lgnum) => Get($"packmats:{lgnum}");
        public static void   SetPackMats(string lgnum, string v) => Set($"packmats:{lgnum}", v, TimeSpan.FromMinutes(60));

        public static string GetSloc(string key)      => Get($"sloc:{key}");
        public static void   SetSloc(string key, string v) => Set($"sloc:{key}", v, TimeSpan.FromMinutes(60));

        public static string GetMajorCat(string key)  => Get($"majorcat:{key}");
        public static void   SetMajorCat(string key, string v) => Set($"majorcat:{key}", v, TimeSpan.FromMinutes(60));
    }
}
