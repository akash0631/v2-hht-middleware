using System;
using System.Collections.Concurrent;

namespace V2HHTMiddleware.Infrastructure
{
    /// <summary>
    /// In-memory cache for HHT middleware (per-instance).
    /// 
    /// CACHED (rarely changes during a shift):
    ///   Bins, SLOC, EAN→material, packing mats, major category,
    ///   bin validations, delivery headers, PO details, stock-take IDs,
    ///   gondola mapping, DC SLOC, GRC bins, HU existence checks.
    /// 
    /// NOT CACHED: stock levels, picking quantities, scan progress (REMAIN_QTY),
    ///   any write operation response. Always fresh from SAP.
    /// 
    /// Each Azure instance has its own in-memory cache. With 2 instances, worst
    /// case is 2 SAP calls per TTL window per key — totally acceptable.
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

        // ── Helpers for cache invalidation / stats ────────────────────────────
        public static int Count => _cache.Count;
        public static void Clear() => _cache.Clear();

        // ── Typed helpers ─────────────────────────────────────────────────────

        // Bin lists per store (10 min — bins don't get created/deleted mid-shift)
        public static string GetBins(string key)        => Get($"bins:{key}");
        public static void   SetBins(string k, string v) => Set($"bins:{k}", v, TimeSpan.FromMinutes(10));

        // Bin-material map for DC (10 min — warehouse layout is stable during shift)
        public static string GetBinMc(string werks, bool qa = false) => Get($"binmc:{(qa?"qa:":"")}{werks}");
        public static void   SetBinMc(string w, string v, bool qa = false) => Set($"binmc:{(qa?"qa:":"")}{w}", v, TimeSpan.FromMinutes(10));

        // EAN → material mapping (30 min — article EAN codes never change)
        public static string GetEan(string key)         => Get($"ean:{key}");
        public static void   SetEan(string k, string v)  => Set($"ean:{k}", v, TimeSpan.FromMinutes(30));

        // Packing materials (60 min — master data, changes at most daily)
        public static string GetPackMats(string lgnum)    => Get($"packmats:{lgnum}");
        public static void   SetPackMats(string l, string v) => Set($"packmats:{l}", v, TimeSpan.FromMinutes(60));

        // SLOC list per store (60 min — storage locations don't change during shift)
        public static string GetSloc(string key)          => Get($"sloc:{key}");
        public static void   SetSloc(string k, string v)  => Set($"sloc:{k}", v, TimeSpan.FromMinutes(60));

        // Major category hierarchy (60 min — category master, changes at most daily)
        public static string GetMajorCat(string key)      => Get($"majorcat:{key}");
        public static void   SetMajorCat(string k, string v) => Set($"majorcat:{k}", v, TimeSpan.FromMinutes(60));

        // Bin existence validation (10 min — bins don't disappear during shift)
        public static string GetValidateBin(string key)   => Get($"vbin:{key}");
        public static void   SetValidateBin(string k, string v) => Set($"vbin:{k}", v, TimeSpan.FromMinutes(10));

        // Stock-take ID validation (5 min — STID is assigned at start of count)
        public static string GetValidateStid(string key)  => Get($"vstid:{key}");
        public static void   SetValidateStid(string k, string v) => Set($"vstid:{k}", v, TimeSpan.FromMinutes(5));

        // Gondola/gandola mapping (10 min — store layout, static during shift)
        public static string GetValidateGandola(string key) => Get($"vgandola:{key}");
        public static void   SetValidateGandola(string k, string v) => Set($"vgandola:{k}", v, TimeSpan.FromMinutes(10));

        // PO / inward receipt details (3 min — PO header doesn't change mid-receive)
        public static string GetNitDel(string key)        => Get($"nitdel:{key}");
        public static void   SetNitDel(string k, string v) => Set($"nitdel:{k}", v, TimeSpan.FromMinutes(3));

        // Delivery header + line items for scndelivery (2 min — items static, REMAIN_QTY not included)
        public static string GetScnDelivery(string key)   => Get($"scndel:{key}");
        public static void   SetScnDelivery(string k, string v) => Set($"scndel:{k}", v, TimeSpan.FromMinutes(2));

        // DC stock-take article barcode validation (30 min — EAN→article, never changes)
        public static string GetStockArti(string key)     => Get($"stkarti:{key}");
        public static void   SetStockArti(string k, string v) => Set($"stkarti:{k}", v, TimeSpan.FromMinutes(30));

        // DC stock-take bin validation (10 min — bin existence, stable)
        public static string GetStockBinVal(string key)   => Get($"stkbin:{key}");
        public static void   SetStockBinVal(string k, string v) => Set($"stkbin:{k}", v, TimeSpan.FromMinutes(10));

        // DC stock-take crate validation (5 min — crate registered once per count)
        public static string GetStockCrateVal(string key) => Get($"stkcrate:{key}");
        public static void   SetStockCrateVal(string k, string v) => Set($"stkcrate:{k}", v, TimeSpan.FromMinutes(5));

        // DC SLOC validation (10 min — SLOC is master data)
        public static string GetDcSloc(string key)        => Get($"dcsloc:{key}");
        public static void   SetDcSloc(string k, string v) => Set($"dcsloc:{k}", v, TimeSpan.FromMinutes(10));

        // GRC bin list per GR document (2 min — assigned at GR creation)
        public static string GetGrcBins(string key)       => Get($"grcbins:{key}");
        public static void   SetGrcBins(string k, string v) => Set($"grcbins:{k}", v, TimeSpan.FromMinutes(2));

        // External HU existence validation (1 min — HU can move between calls)
        public static string GetExtHuVal(string key)      => Get($"exthu:{key}");
        public static void   SetExtHuVal(string k, string v) => Set($"exthu:{k}", v, TimeSpan.FromMinutes(1));

        // Crate validation (2 min — crate registered during inward)
        public static string GetCrateVal(string key)      => Get($"crate:{key}");
        public static void   SetCrateVal(string k, string v) => Set($"crate:{k}", v, TimeSpan.FromMinutes(2));

        // DC HU-GRT validation results (2 min — SLOC mapping, master data)
        public static string GetDcHuGrtVal(string key)    => Get($"dcgrtval:{key}");
        public static void   SetDcHuGrtVal(string k, string v) => Set($"dcgrtval:{k}", v, TimeSpan.FromMinutes(2));

        // DC individual HU validation (1 min — HU status can change)
        public static string GetDcHuVal(string key)       => Get($"dchuval:{key}");
        public static void   SetDcHuVal(string k, string v) => Set($"dchuval:{k}", v, TimeSpan.FromMinutes(1));
    }
}
