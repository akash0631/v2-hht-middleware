using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace V2HHTMiddleware.Controllers.HHT
{
    /// <summary>
    /// Returns a code→name map of all V2 Retail plant codes (stores, hubs, DCs).
    /// Source: Supabase store_plant_master_aka (v2srm project).
    /// Data is fetched from Supabase ONCE and cached in memory.
    /// All Android devices call this endpoint — Supabase is never called from devices.
    ///
    /// GET  /api/hht/plantnames          → { "DW01": "KOLKATA-RDC", "HB05": "PTN-1...", ... }
    /// POST /api/hht/plantnames/refresh  → force re-fetch from Supabase
    /// </summary>
    [RoutePrefix("api/hht")]
    public class PlantNamesController : ApiController
    {
        // ── Supabase connection ───────────────────────────────────────────────
        private const string SUPABASE_URL  = "https://pymdqnnwwxrgeolvgvgv.supabase.co";
        private const string SUPABASE_KEY  = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InB5bWRxbm53d3hyZ2VvbHZndmd2Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTMzMzU0NzYsImV4cCI6MjA2ODkxMTQ3Nn0.jUrb0jIg6qjj2Rlh9DxYesSnbstoD4uoDCswqOqAkUM";
        private const string SUPABASE_ENDPOINT =
            SUPABASE_URL + "/rest/v1/store_plant_master_aka" +
            "?select=STORE-CODE,STORE-NAME&limit=1000";

        // ── In-memory cache ───────────────────────────────────────────────────
        private static readonly ConcurrentDictionary<string, string> _cache
            = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static DateTime   _lastFetched = DateTime.MinValue;
        private static bool       _loading     = false;
        private static readonly SemaphoreSlim _sem = new SemaphoreSlim(1, 1);

        // Refresh cache after 12 hours (covers a full working day without re-fetching)
        private static readonly TimeSpan CACHE_TTL = TimeSpan.FromHours(12);

        // ── GET /api/hht/plantnames ───────────────────────────────────────────
        [HttpGet]
        [Route("plantnames")]
        public async Task<IHttpActionResult> GetPlantNames()
        {
            await EnsureCacheAsync();
            // Return as plain object: { "CODE": "Name", ... }
            return Ok(_cache);
        }

        // ── POST /api/hht/plantnames/refresh ─────────────────────────────────
        [HttpPost]
        [Route("plantnames/refresh")]
        public async Task<IHttpActionResult> ForceRefresh()
        {
            _lastFetched = DateTime.MinValue; // invalidate
            await EnsureCacheAsync(force: true);
            return Ok(new { refreshed = true, count = _cache.Count,
                             fetchedAt = _lastFetched.ToString("o") });
        }

        // ── Internal fetch ────────────────────────────────────────────────────
        private static async Task EnsureCacheAsync(bool force = false)
        {
            bool needsRefresh = force
                || _cache.IsEmpty
                || (DateTime.UtcNow - _lastFetched) > CACHE_TTL;

            if (!needsRefresh) return;

            // Only one thread fetches at a time
            bool acquired = await _sem.WaitAsync(5000);
            if (!acquired) return;

            try
            {
                // Double-check after acquiring lock
                if (!force && !_cache.IsEmpty
                    && (DateTime.UtcNow - _lastFetched) <= CACHE_TTL)
                    return;

                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(15);
                    client.DefaultRequestHeaders.Add("apikey",        SUPABASE_KEY);
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", SUPABASE_KEY);

                    var response = await client.GetAsync(SUPABASE_ENDPOINT);
                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync();
                    var rows = JArray.Parse(json);

                    var temp = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var row in rows)
                    {
                        var code = row["STORE-CODE"]?.ToString()?.Trim();
                        var name = row["STORE-NAME"]?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(name))
                            temp[code.ToUpper()] = name;
                    }

                    _cache.Clear();
                    foreach (var kv in temp)
                        _cache[kv.Key] = kv.Value;

                    _lastFetched = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                // Log but don't crash — devices will just show codes without names
                System.Diagnostics.Debug.WriteLine(
                    $"[PlantNamesController] Supabase fetch failed: {ex.Message}");
            }
            finally
            {
                _sem.Release();
            }
        }
    }
}
