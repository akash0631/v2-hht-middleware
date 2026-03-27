using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Newtonsoft.Json;


namespace V2HHTMiddleware.Controllers.HHT
{
    [RoutePrefix("api/hht")]
    public class HHTController : ApiController
    {
        // ── Constants ──────────────────────────────────────────────────────────
        private const string APK_VERSION = "12.098";
        private const string APK_URL     = "https://assets.eatnubo.com/hht/V2_HHT_Azure_Release.apk";
        private const string MW_VERSION  = "v2-hht-azure|5.0";

        // Persistent stats file — survives App Service restarts (D:\home is mounted storage)
        private static readonly string STATS_FILE =
            Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? @"D:\home",
                         "data", "hht_opcode_stats.json");

        // ── HTTP client ────────────────────────────────────────────────────────
        private static readonly HttpClient _http;
        private static volatile string _javaBase = null;
        private static readonly object _discoveryLock = new object();

        // ── In-memory ring buffer (last 1000 calls) ────────────────────────────
        private static readonly ConcurrentQueue<CallLog> _ring = new ConcurrentQueue<CallLog>();
        private const int RING_MAX = 1000;

        // ── Response cache — read-only opcodes with 60s TTL ────────────────────
        private sealed class CacheEntry { public string Body; public DateTime Expires; }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry>
            _cache = new System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan CACHE_TTL = TimeSpan.FromSeconds(60);
        // Only cache opcodes that return near-static master/reference data
        private static readonly HashSet<string> CACHEABLE = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "storegetbin", "storegetbin_v2", "zwm_store_get_bin", "zwm_store_get_bin_v2",
            "packgingmaterial", "packingmaterial",
            "zwm_store_get_major_cat", "zwm_store_get_major_cat_data",
            "zwm_get_msa_section_list", "zwm_get_packing_material",
            "getsloc", "validatesloc", "zwm_rfc_validate_dc_sloc",
            "zfms_screen", "zwm_get_grc_bins"
        };
        private static string CacheKey(string opcode, string store) => opcode + "|" + (store ?? "?");
        private static bool TryGetCache(string opcode, string store, out string body)
        {
            body = null;
            if (!CACHEABLE.Contains(opcode)) return false;
            if (_cache.TryGetValue(CacheKey(opcode, store), out var e) && e.Expires > DateTime.UtcNow)
            { body = e.Body; return true; }
            return false;
        }
        private static void SetCache(string opcode, string store, string body)
        {
            if (!CACHEABLE.Contains(opcode) || string.IsNullOrEmpty(body)) return;
            // Don't cache error responses
            if (body.Contains("E#") || body.Length < 10) return;
            _cache[CacheKey(opcode, store)] = new CacheEntry { Body = body, Expires = DateTime.UtcNow.Add(CACHE_TTL) };
            // Evict expired entries periodically (every ~100 cache writes)
            if (_cache.Count > 200)
            {
                var expired = _cache.Where(kv => kv.Value.Expires <= DateTime.UtcNow).Select(kv => kv.Key).ToList();
                foreach (var k in expired) _cache.TryRemove(k, out _);
            }
        }

        // Active device sessions: key=userId, value=last seen info
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DeviceSession> _sessions
            = new System.Collections.Concurrent.ConcurrentDictionary<string, DeviceSession>(StringComparer.OrdinalIgnoreCase);

        // ── In-flight deduplication for idempotent-risky write RFCs ────────────
        // Prevents double-tap creating two HUs when ZWM_CREATE_HU_AND_ASSIGN_TVS
        // is slow (5-17s) and the device operator taps again.
        // Key = bapiname|IM_EXIDV — second caller awaits the first result.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.Tasks.TaskCompletionSource<string>>
            _inFlight = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.Tasks.TaskCompletionSource<string>>(StringComparer.OrdinalIgnoreCase);

        // Write RFCs where duplicate execution causes real damage
        private static readonly System.Collections.Generic.HashSet<string> DEDUP_RFCS
            = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ZWM_CREATE_HU_AND_ASSIGN_TVS",
            "ZWM_CREATE_HU_AND_ASSIGN",
            "ZWM_DC_HUGRT_SAVE",
            "ZWM_INV_GRC_HUB_SAVE"
        };

        // Per-RFC timeout buckets (seconds)
        private static int RfcTimeout(string bapi)
        {
            if (string.IsNullOrEmpty(bapi)) return 35;
            string u = bapi.ToUpperInvariant();
            // Heavy creates — HU creation involves multiple SAP steps
            if (u.Contains("CREATE_HU") || u.Contains("INV_GRC_HUB") || u.Contains("DELIVERY_GET_DETAILS_PLP2"))
                return 50;
            // Write / save / post operations
            if (u.Contains("_SAVE") || u.Contains("_POST") || u.Contains("_PICKING")
                || u.Contains("_PUT") || u.Contains("_GRT_S") || u.Contains("_MOVEMENT"))
                return 35;
            // Default reads — validate calls, GET details
            return 15;
        }

        // ── Per-opcode stats — loaded from disk on startup, flushed every 60s ──
        private static readonly ConcurrentDictionary<string, OpcodeStats> _opcodeStats
            = new ConcurrentDictionary<string, OpcodeStats>(StringComparer.OrdinalIgnoreCase);

        // All 117 registered opcodes (for "registered vs active" display)
        private static readonly HashSet<string> ALL_OPCODES = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "scnrec","scnsel","storegetbin","zwm_store_get_bin","storegetbin_v2",
            "zwm_store_get_bin_v2","storegetbinstock","getstorestock","getstorestocktake",
            "getmatbinstock","getmatbinstockbtob","validatebin","validatesloc","getsloc",
            "store_get_mat_from_ean","zwm_store_get_mat_from_ean","validatestablestocktakeid",
            "validatestablestocktakeid_mc","validatestoreean","validatestoreean_v2",
            "articledetails","packgingmaterial","zwm_store_get_major_cat",
            "zwm_store_get_major_cat_data","zwm_store_bin_list_validation",
            "zwm_store_binconhu_get_details","zwm_save_empty_bin","zwm_validate_empty_bin",
            "zwm_vali_crate_emptybin","getstorepicklist","getstorepicklist_v2",
            "zwm_picklist_nos_disp","savedirectpicking","savedirectpicking_v2",
            "zhhtusr_del_picking_rfc","zwm_store_bin_con_picking_hu","get_v01_001s_post",
            "get_v01_001s_stock","hugetdetails","hudetails","gethus","savehus",
            "savehuassign","savehudetails","zwm_store_hu_validate","zwm_hu_quan",
            "zwm_validate_external_hu","savegrcputway","savefloorputway","savefloorputwaytake",
            "zwm_floor_puaway_new","zwm_store_floor_putway_hu","zwm_store_hu_putway_bin_con",
            "savegrtmsa","savegrtfromdisplay","zwm_grt_save","zwm_grt_putway_crate_validation",
            "zwm_grt_putway_post","zwm_store_get_grtstock","zwm_rfc_validate_crate",
            "zwm_get_grc_bins","zwm_save_grc_to_data","stocktakegetdetails","stocktakesavedata",
            "stockvalidatebarcode","zwm_rfc_stock_take_bin_vali","zwm_rfc_stock_take_arti_vali",
            "zwm_rfc_stock_take_crate_vali","zwm_rfc_stock_take_save_v11",
            "zwm_store_0001_stock_take","store_0001_stock_take","zwm_store_0001_reverse_stock",
            "zwm_rfc_store_ean_data_stk","zwm_rfc_stock_movement_v21","zwm_rfc_stock_validate_v21",
            "zwm_store_pushdatatosap_1total","zwm_store_pushdatatosap_1dis","pushdatatosap01stock",
            "zhwm_store_pushdatasap_1stock","savebtob","savesloctoslocwwm",
            "zwm_store_transfer_bin_to_bin","zwm_store_trf_0001_to_0010","store_trf_0001_to_0010",
            "storestidpost","storestidpost_mc","validategandola_mc","savecrate","validatecrateto",
            "zstore_discount_store_vali","zstore_discount_get_ean_data","zstore_discount_save_ean_data",
            "nitrec","nitupd","nitdel","disrec","scndelivery","zwm_get_sto_data",
            "zwm_validate_dc_sloc","zwm_dc_hu_grt_val","zwm_dc_hugrt_binhu_val",
            "zwm_dc_hugrt_hu_val","zwm_dc_hugrt_save","getgrdetails","createto",
            "zwm_to_get_details","zwm_to_scan_data_save","zwm_to_create_from_gr_data",
            "zwm_cla_palette_validate","zwm_cla_hu_validate","zwm_cla_bin_validate",
            "zwm_cla_hu_palette_save","zwm_cla_palette_bin_tag_save","zwm_huput31_save",
            "zrfc_sdc_put31","zrfc_sdc_put31_bin_validation","zwm_rfc_get_ean_stid_mc",
            "zwm_rfc_stock_movement_v21","zwm_store_get_grtstock",
            "pushdatatosap01stock","zhwm_store_pushdatasap_1stock"
        };

        private static readonly Timer _flushTimer;
        private static readonly object _fileLock = new object();
        private static bool _statsLoaded = false;

        static HHTController()
        {
            var handler = new HttpClientHandler
            {
                MaxConnectionsPerServer = 300,
                UseProxy = false,
                AllowAutoRedirect = false
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(55) };

            // Load persisted stats from disk immediately
            LoadStatsFromDisk();

            // Flush stats to disk every 60 seconds
            _flushTimer = new Timer(_ => FlushStatsToDisk(), null,
                TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ROUTES
        // ═══════════════════════════════════════════════════════════════════════

        [HttpPost, Route("")]
        public Task<HttpResponseMessage> Handle() => Proxy();

        [HttpPost, Route("ValueXMW")]
        public Task<HttpResponseMessage> ValueXMW() => Proxy();

        [HttpPost, Route("ValueXMW/{app}")]
        public Task<HttpResponseMessage> ValueXMWApp(string app) => Proxy();

        [HttpPost, Route("ValueXMW/{app}/{platform}/{version}")]
        public Task<HttpResponseMessage> ValueXMWFull(string app, string platform, string version) => Proxy();

        [HttpPost, Route("~/ValueXMW/{app}/{platform}/{version}")]
        public Task<HttpResponseMessage> ValueXMWRoot(string app, string platform, string version) => Proxy();


        // ── v12+ app ─────────────────────────────────────────────────────────
        [HttpPost, Route("noacljsonrfcadaptor")]
        public Task<HttpResponseMessage> NoAclJson()    => ProxyNoAcl();
        [HttpGet,  Route("noacljsonrfcadaptor")]
        public Task<HttpResponseMessage> NoAclJsonGet() => ProxyNoAcl();

        // ── index.jsp / ping — v12 IPActivity connectivity check ──────────
        [HttpGet, Route("index.jsp")]
        public HttpResponseMessage IndexJspGet()
        {
            return Json("ok");
        }

        [HttpGet, Route("ping")]
        public HttpResponseMessage Ping()
        {
            return Json("ok");
        }


        [HttpGet, Route("appversion")]
        public HttpResponseMessage AppVersion()
            => Json($"{{\"upgrade\":\"none\",\"version\":\"{APK_VERSION}\",\"downloadLink\":\"{APK_URL}\"}}");

        [HttpGet, Route("ValueXMW/appversion")]
        public HttpResponseMessage AppVersionLegacy()
            => Json($"{{\"upgrade\":\"none\",\"version\":\"{APK_VERSION}\",\"downloadLink\":\"{APK_URL}\"}}");

        // ── Health ─────────────────────────────────────────────────────────────
        [HttpGet, Route("health")]
        public async Task<HttpResponseMessage> Health()
        {
            string javaBase   = GetJavaBase();
            string javaStatus = "unreachable";
            if (javaBase != null)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    var r  = await _http.GetAsync(javaBase.Replace("/xmwgw", "") + "/index.jsp").ConfigureAwait(false);
                    sw.Stop();
                    javaStatus = $"ok:{(int)r.StatusCode}:{sw.ElapsedMilliseconds}ms";
                }
                catch (Exception ex)
                {
                    javaStatus = "err:" + ex.Message.Substring(0, Math.Min(60, ex.Message.Length)).Replace("\n", " ");
                }
            }

            int activeOpcodes     = _opcodeStats.Count;
            int registeredOpcodes = ALL_OPCODES.Count;
            long totalCalls       = _opcodeStats.Values.Sum(s => s.Count);

            return Txt(
                $"OK|{MW_VERSION}" +
                $"|apk={APK_VERSION}" +
                $"|java={javaBase ?? "not-discovered"}" +
                $"|java={javaStatus}" +
                $"|calls_total={totalCalls}" +
                $"|active_opcodes={activeOpcodes}" +
                $"|registered_opcodes={registeredOpcodes}" +
                $"|stats_persisted={_statsLoaded}" +
                $"|{DateTime.UtcNow:yyyy-MM-dd HH:mm}UTC"
            );
        }


        [HttpGet, Route("stats")]
        public HttpResponseMessage Stats()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== V2 HHT Azure Middleware — Live Stats ===");
            sb.AppendLine($"Time           : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"MW Version     : {MW_VERSION}");
            sb.AppendLine($"Java Base      : {_javaBase ?? "not-discovered"}");
            sb.AppendLine($"Stats persisted: {_statsLoaded} (file: {STATS_FILE})");
            sb.AppendLine();

            int    active     = _opcodeStats.Count;
            int    registered = ALL_OPCODES.Count;
            long   total      = _opcodeStats.Values.Sum(s => s.Count);
            long   errors     = _opcodeStats.Values.Sum(s => s.Errors);

            sb.AppendLine($"Registered opcodes : {registered}");
            sb.AppendLine($"Active opcodes     : {active} (called at least once)");
            sb.AppendLine($"Never-called       : {registered - active} (not yet used in current period)");
            sb.AppendLine($"Total RFC calls    : {total}");
            sb.AppendLine($"Infra errors       : {errors}");
            sb.AppendLine();

            // Active opcodes — full stats
            sb.AppendLine("ACTIVE OPCODE PERFORMANCE:");
            sb.AppendLine($"{"Opcode",-42} {"Calls",6} {"Errors",6} {"Avg ms",8} {"Min ms",8} {"Max ms",8} {"P95 ms",8} {"LastSeen",19}");
            sb.AppendLine(new string('-', 115));
            foreach (var kv in _opcodeStats.OrderByDescending(x => x.Value.Count))
            {
                var s = kv.Value;
                sb.AppendLine($"{kv.Key,-42} {s.Count,6} {s.Errors,6} {s.AvgMs,8:F0} {s.MinMs,8:F0} {s.MaxMs,8:F0} {s.P95Ms,8:F0} {s.LastSeen:yyyy-MM-dd HH:mm:ss}");
            }

            // Never-called opcodes
            sb.AppendLine();
            sb.AppendLine("NEVER-CALLED OPCODES (registered but 0 calls this period):");
            var neverCalled = ALL_OPCODES.Where(o => !_opcodeStats.ContainsKey(o)).OrderBy(o => o).ToList();
            sb.AppendLine(string.Join(", ", neverCalled));

            // Recent calls
            sb.AppendLine();
            sb.AppendLine("LAST 500 CALLS:");
            sb.AppendLine($"{"Timestamp",-20} {"User",-8} {"Opcode",-35} {"Store",-6} {"Ms",6} {"OK",4} {"Resp",40}");
            sb.AppendLine(new string('-', 125));
            var recent = _ring.ToArray();
            int ringCount = Math.Min(recent.Length, 500);
            for (int i = recent.Length - 1; i >= recent.Length - ringCount; i--)
            {
                var c = recent[i];
                string uid = string.IsNullOrEmpty(c.UserId) ? "-" : c.UserId;
                sb.AppendLine($"{c.Timestamp.ToString("HH:mm:ss.fff"),-20} {uid,-8} {c.Opcode,-35} {c.Store,-6} {c.ElapsedMs,6} {(c.SapOk?"✅":"❌"),4}  {c.ResponseSnippet,-40}");
            }

            return Txt(sb.ToString());
        }

        // ── Cache stats ───────────────────────────────────────────────────────
        [HttpGet, Route("cache/stats")]
        public HttpResponseMessage CacheStats()
        {
            var now = DateTime.UtcNow;
            var live  = _cache.Where(kv => kv.Value.Expires > now).ToList();
            var data  = live.Select(kv => new {
                key     = kv.Key,
                expires = kv.Value.Expires.ToString("HH:mm:ss"),
                ttl_sec = (int)(kv.Value.Expires - now).TotalSeconds
            }).OrderBy(x => x.key).ToList();
            return Json(Newtonsoft.Json.JsonConvert.SerializeObject(new {
                live_entries   = live.Count,
                total_entries  = _cache.Count,
                cacheable_ops  = CACHEABLE.Count,
                ttl_seconds    = (int)CACHE_TTL.TotalSeconds,
                entries        = data
            }));
        }

        [HttpPost, Route("cache/clear")]
        public HttpResponseMessage CacheClear()
        {
            _cache.Clear();
            return Json(@"{""cleared"":true}");
        }

        // ── Active device sessions ────────────────────────────────────────────
        [HttpGet, Route("sessions")]
        public HttpResponseMessage Sessions()
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-30);
            var active = _sessions.Values
                .Where(s => s.LastSeen >= cutoff)
                .OrderByDescending(s => s.LastSeen)
                .Select(s => new {
                    user_id        = s.UserId,
                    store          = s.Store,
                    last_opcode    = s.LastOpcode,
                    last_seen      = s.LastSeen.ToString("HH:mm:ss"),
                    last_seen_mins = (int)(DateTime.UtcNow - s.LastSeen).TotalMinutes,
                    call_count     = s.CallCount,
                    active         = (DateTime.UtcNow - s.LastSeen).TotalMinutes < 5
                }).ToList();
            return Json(Newtonsoft.Json.JsonConvert.SerializeObject(new { sessions = active, total = active.Count }));
        }

        // ── Per-opcode drill-down ──────────────────────────────────────────────
        [HttpGet, Route("stats/{opcode}")]
        public HttpResponseMessage StatsOpcode(string opcode)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Opcode: {opcode} ===");
            sb.AppendLine($"Registered: {(ALL_OPCODES.Contains(opcode) ? "YES" : "NO — not in router")}");
            if (_opcodeStats.TryGetValue(opcode, out var s))
            {
                sb.AppendLine($"Status     : ACTIVE");
                sb.AppendLine($"Total calls: {s.Count}");
                sb.AppendLine($"Errors     : {s.Errors}");
                sb.AppendLine($"Avg latency: {s.AvgMs:F0}ms");
                sb.AppendLine($"Min latency: {s.MinMs:F0}ms");
                sb.AppendLine($"Max latency: {s.MaxMs:F0}ms");
                sb.AppendLine($"P95 latency: {s.P95Ms:F0}ms");
                sb.AppendLine($"Last error : {s.LastError ?? "none"}");
                sb.AppendLine($"Last seen  : {s.LastSeen:yyyy-MM-dd HH:mm:ss} UTC");
            }
            else
            {
                sb.AppendLine($"Status     : NEVER CALLED (0 calls in current period)");
            }
            sb.AppendLine();
            sb.AppendLine("Recent calls:");
            var calls = _ring.ToArray();
            int shown = 0;
            for (int i = calls.Length - 1; i >= 0 && shown < 30; i--)
            {
                var c = calls[i];
                if (!c.Opcode.Equals(opcode, StringComparison.OrdinalIgnoreCase)) continue;
                sb.AppendLine($"  {c.Timestamp:HH:mm:ss}  Store={c.Store}  {c.ElapsedMs}ms  {(c.SapOk?"✅":"❌")}  {c.ResponseSnippet}");
                shown++;
            }
            if (shown == 0) sb.AppendLine("  No recent calls in ring buffer.");
            return Txt(sb.ToString());
        }

        // ── Manual flush ───────────────────────────────────────────────────────
        [HttpPost, Route("stats/flush")]
        public HttpResponseMessage FlushStats()
        {
            FlushStatsToDisk();
            return Txt($"Flushed {_opcodeStats.Count} opcodes to {STATS_FILE}");
        }

        // ── Reset stats ────────────────────────────────────────────────────────
        [HttpPost, Route("stats/reset")]
        public HttpResponseMessage ResetStats()
        {
            _opcodeStats.Clear();
            while (_ring.TryDequeue(out _)) { }
            FlushStatsToDisk();
            return Txt("Stats reset and file cleared.");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // PROXY
        // ═══════════════════════════════════════════════════════════════════════

        private async Task<HttpResponseMessage> Proxy()
        {
            string javaBase = GetJavaBase();
            if (javaBase == null)
                return LogAndReturn(null, 0, "E#HC tunnel down — cannot reach Server 200", false, "?");

            string body   = await Request.Content.ReadAsStringAsync().ConfigureAwait(false);

            // v12 app posts JSON to /ValueXMW — route to ProxyNoAcl which returns SAP JSON
            if (body.TrimStart().StartsWith("{"))
                return await ProxyNoAcl(body).ConfigureAwait(false);

            string opcode  = ExtractOpcode(body);
            string store   = ExtractStore(body);
            string userId  = ExtractUserId(body);

            // Cache check — serve cached response for read-only opcodes
            if (TryGetCache(opcode, store, out string cachedBody))
            {
                LogAndReturn(opcode, 0, cachedBody, true, store, userId);
                var cachedResp = Request.CreateResponse(System.Net.HttpStatusCode.OK);
                cachedResp.Content = new StringContent(cachedBody, Encoding.UTF8, "application/json");
                cachedResp.Headers.Add("X-Cache", "HIT");
                return cachedResp;
            }

            var    sw      = Stopwatch.StartNew();
            string respBody;
            bool   sapOk;

            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, javaBase + "/ValueXMW")
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/plain")
                };
                foreach (var h in Request.Headers)
                    if (h.Key.StartsWith("X-HHT-", StringComparison.OrdinalIgnoreCase))
                        req.Headers.TryAddWithoutValidation(h.Key, h.Value);

                var resp  = await _http.SendAsync(req).ConfigureAwait(false);
                sw.Stop();
                respBody  = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                sapOk     = IsInfraOk(respBody);
            }
            catch (TaskCanceledException)
            {
                sw.Stop();
                respBody = "E#SAP timeout — RFC did not respond in 55s";
                sapOk    = false;
            }
            catch (Exception ex)
            {
                sw.Stop();
                respBody = "E#Proxy error: " + ex.Message.Replace("\n", " ");
                sapOk    = false;
            }

            // ── SAP lock retry (Proxy/legacy path) ───────────────────────────────
            // Transient object-lock errors — retry once after 600ms.
            // Only for non-write opcodes to avoid creating duplicates.
            bool _isWrite = opcode != null && (
                opcode.Contains("save") || opcode.Contains("post") || opcode.Contains("create") ||
                opcode.Contains("del_pick") || opcode.Contains("movement"));
            if (!sapOk && !_isWrite && respBody != null &&
                (respBody.IndexOf("locked", System.StringComparison.OrdinalIgnoreCase) >= 0))
            {
                System.Threading.Thread.Sleep(600);
                try
                {
                    var retReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, GetJavaBase() + "/ValueXMW")
                        { Content = new System.Net.Http.StringContent(body, Encoding.UTF8, "text/plain") };
                    var retResp = _http.SendAsync(retReq).GetAwaiter().GetResult();
                    var retBody = retResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!string.IsNullOrEmpty(retBody) && !retBody.StartsWith("E#"))
                    { respBody = retBody; sapOk = true; }
                }
                catch { /* retry failed — return original error */ }
            }

            // Cache successful responses for cacheable opcodes
            if (sapOk) SetCache(opcode, store, respBody);

            return LogAndReturn(opcode, sw.ElapsedMilliseconds, respBody, sapOk, store, userId);
        }

        // ═══════════════════════════════════════════════════════════════════════
        // LOGGING
        // ═══════════════════════════════════════════════════════════════════════

        private HttpResponseMessage LogAndReturn(string opcode, long ms, string resp, bool ok, string store, string userId = "")
        {
            if (opcode != null)
            {
                // Ring buffer
                var entry = new CallLog
                {
                    Timestamp       = DateTime.UtcNow,
                    Opcode          = opcode,
                    Store           = store ?? "?",
                    UserId          = userId ?? "",
                    ElapsedMs       = ms,
                    SapOk           = ok,
                    ResponseSnippet = (resp ?? "").Length > 60
                        ? resp.Substring(0, 60) : resp ?? ""
                };
                _ring.Enqueue(entry);
                while (_ring.Count > RING_MAX) _ring.TryDequeue(out _);

                // Update active sessions
                if (!string.IsNullOrEmpty(userId))
                {
                    _sessions.AddOrUpdate(userId,
                        _ => new DeviceSession { UserId=userId, Store=store??"?", LastOpcode=opcode, LastSeen=DateTime.UtcNow, CallCount=1 },
                        (_, s) => { s.Store=store??"?"; s.LastOpcode=opcode; s.LastSeen=DateTime.UtcNow; s.CallCount++; return s; });
                }

                // Opcode stats (persisted)
                _opcodeStats.AddOrUpdate(opcode,
                    _ => new OpcodeStats(ms, ok,
                        ok ? null : (resp ?? "").Substring(0, Math.Min(80, (resp ?? "").Length))),
                    (_, existing) =>
                    {
                        existing.Record(ms, ok,
                            ok ? null : (resp ?? "").Substring(0, Math.Min(80, (resp ?? "").Length)));
                        return existing;
                    });

                // Structured log → stdout → App Insights
                var snip = (resp ?? "").Replace("\n"," ").Replace("|",":");
                if (snip.Length > 80) snip = snip.Substring(0, 80);
                Console.WriteLine($"[HHT] {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}|{opcode}|{store}|{ms}|{(ok?"OK":"ERR")}|{snip}");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(resp ?? "", Encoding.UTF8, "text/plain")
            };
        }

        // ═══════════════════════════════════════════════════════════════════════
        // PERSISTENCE — JSON file in D:\home\data (survives restarts)
        // ═══════════════════════════════════════════════════════════════════════

        private static void LoadStatsFromDisk()
        {
            try
            {
                if (!File.Exists(STATS_FILE)) { _statsLoaded = false; return; }
                var json = File.ReadAllText(STATS_FILE);
                var dict = JsonConvert.DeserializeObject<Dictionary<string, PersistedStats>>(json);
                if (dict == null) { _statsLoaded = false; return; }
                foreach (var kv in dict)
                {
                    var p = kv.Value;
                    var s = new OpcodeStats((long)p.MinMs, true, null);
                    s.RestoreFrom(p);
                    _opcodeStats[kv.Key] = s;
                }
                _statsLoaded = true;
                Console.WriteLine($"[HHT-PERSIST] Loaded {dict.Count} opcodes from {STATS_FILE}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HHT-PERSIST] Load error: {ex.Message}");
                _statsLoaded = false;
            }
        }

        private static void FlushStatsToDisk()
        {
            try
            {
                var dir = Path.GetDirectoryName(STATS_FILE);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var dict = new Dictionary<string, PersistedStats>();
                foreach (var kv in _opcodeStats)
                {
                    var s = kv.Value;
                    dict[kv.Key] = new PersistedStats
                    {
                        Count     = s.Count,
                        Errors    = s.Errors,
                        MinMs     = s.MinMs,
                        MaxMs     = s.MaxMs,
                        AvgMs     = s.AvgMs,
                        P95Ms     = s.P95Ms,
                        LastError = s.LastError,
                        LastSeen  = s.LastSeen.ToString("o")
                    };
                }

                var json = JsonConvert.SerializeObject(dict);
                lock (_fileLock)
                {
                    File.WriteAllText(STATS_FILE + ".tmp", json);
                    if (File.Exists(STATS_FILE)) File.Delete(STATS_FILE);
                    File.Move(STATS_FILE + ".tmp", STATS_FILE);
                }
                Console.WriteLine($"[HHT-PERSIST] Flushed {dict.Count} opcodes → {STATS_FILE}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HHT-PERSIST] Flush error: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // HC DISCOVERY
        // ═══════════════════════════════════════════════════════════════════════

        private static string GetJavaBase()
        {
            if (_javaBase != null) return _javaBase;
            lock (_discoveryLock)
            {
                if (_javaBase != null) return _javaBase;
                var found = new ConcurrentBag<int>();
                var tasks = new List<Task>();
                for (int i = 1; i <= 254; i++)
                {
                    int idx = i;
                    tasks.Add(Task.Run(() =>
                    {
                        try
                        {
                            using (var sock = new System.Net.Sockets.Socket(
                                System.Net.Sockets.AddressFamily.InterNetwork,
                                System.Net.Sockets.SocketType.Stream,
                                System.Net.Sockets.ProtocolType.Tcp))
                            {
                                sock.Blocking = false;
                                try { sock.Connect($"127.0.0.{idx}", 9080); } catch { }
                                var w = new List<System.Net.Sockets.Socket> { sock };
                                var e = new List<System.Net.Sockets.Socket> { sock };
                                System.Net.Sockets.Socket.Select(null, w, e, 200000);
                                if (w.Count > 0 && e.Count == 0) found.Add(idx);
                            }
                        }
                        catch { }
                    }));
                }
                Task.WaitAll(tasks.ToArray(), 4000);
                int best = int.MaxValue;
                foreach (var x in found) if (x < best) best = x;
                _javaBase = best < int.MaxValue ? $"http://127.0.0.{best}:9080/xmwgw" : null;
                return _javaBase;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════════════════

        private static string ExtractOpcode(string body)
        {
            if (string.IsNullOrEmpty(body)) return "unknown";
            int idx = body.IndexOf('#');
            return idx > 0 ? body.Substring(0, idx).Trim().ToLowerInvariant() : body.Trim().ToLowerInvariant();
        }

        private static string ExtractStore(string body)
        {
            if (string.IsNullOrEmpty(body)) return "?";
            // JSON body (v12 app): {"bapiname":"RFC","IM_WERKS":"DH24",...}
            if (body.TrimStart().StartsWith("{"))
            {
                try {
                    var j = Newtonsoft.Json.Linq.JObject.Parse(body);
                    var werks = j["IM_WERKS"]?.ToString() ?? j["im_werks"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(werks)) return werks;
                    // Fallback: any field that looks like a plant code
                    foreach (var kv in j)
                        if (kv.Value?.ToString().Length >= 3 && kv.Value.ToString().Length <= 6
                            && (kv.Value.ToString().StartsWith("H") || kv.Value.ToString().StartsWith("DH")))
                            return kv.Value.ToString();
                } catch { }
                return "?";
            }
            // Legacy body: opcode#user#password#store#...
            var parts = body.Split('#');
            if (parts.Length >= 4 && parts[0].Equals("scnrec", StringComparison.OrdinalIgnoreCase)) return parts[3];
            if (parts.Length >= 2 && parts[1].Length >= 3 && parts[1].Length <= 6
                && (parts[1].StartsWith("H") || parts[1].StartsWith("DH") || parts[1].StartsWith("h")))
                return parts[1];
            return "?";
        }

        private static string ExtractUserId(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            // JSON body: {"bapiname":"RFC","im_userid":"250","IM_USERID":"250",...}
            if (body.TrimStart().StartsWith("{"))
            {
                try {
                    var j = Newtonsoft.Json.Linq.JObject.Parse(body);
                    return j["im_userid"]?.ToString() ?? j["IM_USERID"]?.ToString()
                        ?? j["im_password"]?.ToString() ?? j["IM_PASSWORD"]?.ToString() ?? "";
                } catch { }
                return "";
            }
            // Legacy: opcode#user#password#store → parts[1]=user
            var parts = body.Split('#');
            return parts.Length >= 2 ? parts[1] : "";
        }

        // ── Task with timeout helper (not extension — HHTController is not static) ──
        private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout)
        {
            var delay = Task.Delay(timeout);
            var done  = await Task.WhenAny(task, delay).ConfigureAwait(false);
            if (done == delay) throw new OperationCanceledException("Dedup wait timed out");
            return await task.ConfigureAwait(false);
        }

        // Infrastructure errors only (tunnel/proxy failures) — SAP business errors are OK
        private static bool IsInfraOk(string resp)
        {
            if (string.IsNullOrEmpty(resp)) return true;
            var r = resp.TrimStart();
            return !r.StartsWith("E#HC tunnel") && !r.StartsWith("E#Proxy error") &&
                   !r.StartsWith("E#SAP timeout") && !r.StartsWith("E#not-discovered");
        }

        private static HttpResponseMessage Txt(string s) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(s, Encoding.UTF8, "text/plain") };

        private static HttpResponseMessage Json(string s) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(s, Encoding.UTF8, "application/json") };

        // ═══════════════════════════════════════════════════════════════════════
        // DATA MODELS
        // ═══════════════════════════════════════════════════════════════════════

        private class CallLog
        {
            public DateTime Timestamp       { get; set; }
            public string   Opcode          { get; set; }
            public string   Store           { get; set; }
            public string   UserId          { get; set; }
            public long     ElapsedMs       { get; set; }
            public bool     SapOk           { get; set; }
            public string   ResponseSnippet { get; set; }
        }

        class DeviceSession
        {
            public string   UserId      { get; set; }
            public string   Store       { get; set; }
            public string   LastOpcode  { get; set; }
            public DateTime LastSeen    { get; set; }
            public int      CallCount   { get; set; }
        }

        public class PersistedStats
        {
            public long   Count     { get; set; }
            public long   Errors    { get; set; }
            public double MinMs     { get; set; }
            public double MaxMs     { get; set; }
            public double AvgMs     { get; set; }
            public double P95Ms     { get; set; }
            public string LastError { get; set; }
            public string LastSeen  { get; set; }
        }


        private async Task<HttpResponseMessage> ProxyNoAcl(string preReadBody = null)
        {
            // v12 app sends: POST /noacljsonrfcadaptor?bapiname=RFC_NAME
            // Body: {"bapiname":"RFC","IM_PARAM1":"val",...}
            //
            // Two-path strategy:
            //   Path A: Try Java /noacljsonrfcadaptor with strict application/json
            //           -> returns native SAP JSON for ALL new RFCs (ZGRT_*, ZFM_*, etc.)
            //   Path B: Fall back to Java /ValueXMW if content-type rejected
            //           -> works for older RFCs that exist in ValueXMW handler

            string javaBase = GetJavaBase();
            if (javaBase == null)
                return LogAndReturn("noacl", 0, "E#HC tunnel down", false, "?");

            string rawBody = preReadBody ?? await Request.Content.ReadAsStringAsync().ConfigureAwait(false);

            // Parse bapi name and IM_ params
            string bapi  = "";
            var imVals   = new System.Collections.Generic.List<string>();
            try
            {
                var jobj = Newtonsoft.Json.Linq.JObject.Parse(rawBody);
                bapi = jobj["bapiname"]?.ToString() ?? "";
                foreach (var kv in jobj)
                    if (kv.Key.StartsWith("IM_", System.StringComparison.OrdinalIgnoreCase))
                        imVals.Add(kv.Value?.ToString() ?? "");
            }
            catch { }

            var qs = System.Web.HttpUtility.ParseQueryString(Request.RequestUri?.Query ?? "");
            if (string.IsNullOrEmpty(bapi)) bapi = qs["bapiname"] ?? "noacl";

            // Extract store (WERKS) from JSON body for logging
            string store = "?";
            try {
                var jWerks = Newtonsoft.Json.Linq.JObject.Parse(rawBody);
                store = jWerks["IM_WERKS"]?.ToString() ?? jWerks["im_werks"]?.ToString() ?? "?";
            } catch { }

            string opcode = bapi.Equals("ZWM_USER_AUTHORITY_CHECK", System.StringComparison.OrdinalIgnoreCase)
                            ? "scnrec" : bapi.ToLower();

            // ── Response cache check — same logic as Proxy() ──────────────────
            // CACHEABLE set covers read-only opcodes (storegetbin_v2, packgingmaterial, etc.)
            // Cache key: opcode|store (store extracted from IM_WERKS above)
            if (TryGetCache(opcode, store, out string noaclCached))
            {
                LogAndReturn(opcode, 0, noaclCached, true, store);
                var cachedResp = Request.CreateResponse(System.Net.HttpStatusCode.OK);
                cachedResp.Content = new StringContent(noaclCached, Encoding.UTF8, "application/json");
                cachedResp.Headers.Add("X-Cache", "HIT");
                return cachedResp;
            }

            var sw = Stopwatch.StartNew();

            // ── PATH A: Java /noacljsonrfcadaptor (native SAP JSON response) ────
            try
            {
                string noaclUrl = javaBase.Replace("/xmwgw", "/xmwgw/noacljsonrfcadaptor")
                                  + "?" + (qs.Count > 0 ? qs.ToString() : "bapiname=" + bapi + "&aclclientid=android");

                // CRITICAL: set Content-Type as MediaTypeHeaderValue (no charset suffix)
                // Java's noacljsonrfcadaptor checks for exact "application/json"
                var noaclContent = new StringContent(rawBody, Encoding.UTF8);
                noaclContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                // ── In-flight deduplication for risky write RFCs ──────────────
                // If two requests for the same HU creation arrive simultaneously,
                // the second waits for the first result instead of hitting SAP twice.
                string dedupKey = null;
                if (DEDUP_RFCS.Contains(bapi))
                {
                    var jobj2 = Newtonsoft.Json.Linq.JObject.Parse(rawBody);
                    var exidv  = jobj2["IM_EXIDV"]?.ToString() ?? jobj2["im_exidv"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(exidv))
                        dedupKey = bapi.ToUpperInvariant() + "|" + exidv;
                }

                if (dedupKey != null)
                {
                    var tcs = new System.Threading.Tasks.TaskCompletionSource<string>();
                    if (!_inFlight.TryAdd(dedupKey, tcs))
                    {
                        // Another request for same HU is in-flight — wait for its result
                        if (_inFlight.TryGetValue(dedupKey, out var existing))
                        {
                            try
                            {
                                string dedupResult = await WithTimeout(existing.Task,
                                    TimeSpan.FromSeconds(55)).ConfigureAwait(false);
                                sw.Stop();
                                LogAndReturn(opcode, (long)sw.ElapsedMilliseconds, dedupResult, true, store);
                                var dedupResp = Request.CreateResponse(System.Net.HttpStatusCode.OK);
                                dedupResp.Content = new StringContent(dedupResult, Encoding.UTF8, "application/json");
                                dedupResp.Headers.Add("X-Dedup", "true");
                                return dedupResp;
                            }
                            catch { /* timed out waiting — fall through to SAP */ }
                        }
                    }
                }

                // ── Per-RFC timeout — not flat 55s for everything ─────────────
                int timeoutSec = RfcTimeout(bapi);
                string noaclRaw;
                try
                {
                    using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec)))
                    {
                        var noaclReq  = new HttpRequestMessage(HttpMethod.Post, noaclUrl) { Content = noaclContent };
                        var noaclResp = await _http.SendAsync(noaclReq, cts.Token).ConfigureAwait(false);
                        noaclRaw = await noaclResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
                catch (System.OperationCanceledException)
                {
                    noaclRaw = $"E#RFC timeout after {timeoutSec}s — SAP did not respond";
                    if (dedupKey != null && _inFlight.TryRemove(dedupKey, out var tcs2)) tcs2.TrySetResult(noaclRaw);
                    sw.Stop();
                    return LogAndReturn(opcode, (long)sw.ElapsedMilliseconds, noaclRaw, false, store);
                }

                // Java accepted the request if response is valid JSON (not the content-type error string)
                if (!string.IsNullOrEmpty(noaclRaw) &&
                    !noaclRaw.Contains("Only Applicaton/Json") &&
                    !noaclRaw.Contains("Content Type Not supported") &&
                    noaclRaw.TrimStart().StartsWith("{"))
                {
                    sw.Stop();
                    bool ok = IsInfraOk(noaclRaw);
                    LogAndReturn(opcode, (long)sw.ElapsedMilliseconds, noaclRaw, ok, opcode);
                    if (ok) SetCache(opcode, store, noaclRaw);
                    // Resolve any waiters on this dedup key
                    if (dedupKey != null && _inFlight.TryRemove(dedupKey, out var tcsOk)) tcsOk.TrySetResult(noaclRaw);
                    var nativeResp = Request.CreateResponse(System.Net.HttpStatusCode.OK);
                    nativeResp.Content = new StringContent(noaclRaw, Encoding.UTF8, "application/json");
                    nativeResp.Headers.Add("X-Cache", "MISS");
                    return nativeResp;
                }
                // Java rejected content type — fall through to Path B
            }
            catch { /* fall through to ValueXMW */ }

            // ── PATH B: Java /ValueXMW with old opcode format ────────────────────
            // Translate: bapiname + IM_ values → "opcode#val1#val2#...#<eol>"
            var legacySb = new System.Text.StringBuilder(opcode);
            foreach (var v in imVals) legacySb.Append("#").Append(v);
            legacySb.Append("#<eol>");

            string respBody; bool sapOk;
            try
            {
                var legReq = new HttpRequestMessage(HttpMethod.Post, javaBase + "/ValueXMW")
                {
                    Content = new StringContent(legacySb.ToString(), Encoding.UTF8, "application/json")
                };
                var legResp = await _http.SendAsync(legReq).ConfigureAwait(false);
                sw.Stop();
                respBody = await legResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                sapOk    = IsInfraOk(respBody);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return LogAndReturn(opcode, (long)sw.ElapsedMilliseconds, "E#" + ex.Message, false, opcode);
            }

            // Translate old response format → SAP JSON for v12 app
            string jsonOut = BuildSapJson(bapi, respBody ?? "");
            LogAndReturn(opcode, (long)sw.ElapsedMilliseconds, respBody, sapOk, opcode);
            var httpOut = Request.CreateResponse(System.Net.HttpStatusCode.OK);
            httpOut.Content = new StringContent(jsonOut, Encoding.UTF8, "application/json");
            return httpOut;
        }

        // Translate Java ValueXMW "Response:X#p1#p2#..." → SAP JSON for v12 app
        //
        // Java response formats:
        //   Response:1#data   = success with data
        //   Response:0        = failure (auth or general)
        //   Response:E#msg    = SAP explicit error with message
        //   Response:S#data   = SAP success with structured data
        //   Response:null     = opcode unknown to Java
        private string BuildSapJson(string bapi, string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw.Trim().Equals("Response:null", StringComparison.OrdinalIgnoreCase))
            {
                var e0 = new Newtonsoft.Json.Linq.JObject();
                e0["EX_RETURN"] = new Newtonsoft.Json.Linq.JObject(
                    new Newtonsoft.Json.Linq.JProperty("TYPE",    "E"),
                    new Newtonsoft.Json.Linq.JProperty("MESSAGE", "Operation not supported. Please update the app.")
                );
                return e0.ToString(Newtonsoft.Json.Formatting.None);
            }

            // Strip "Response:" prefix
            string payload = raw.StartsWith("Response:") ? raw.Substring(9) : raw;

            // Trim trailing #<eol> or <eol>
            if (payload.EndsWith("<eol>")) payload = payload.Substring(0, payload.Length - 5);
            payload = payload.TrimEnd('#').Trim();

            string[] parts  = payload.Split('#');
            string   status = parts.Length > 0 ? parts[0].Trim() : "";

            var obj = new Newtonsoft.Json.Linq.JObject();

            // ── Explicit SAP error (Response:E#message) ──────────────────────
            if (status.Equals("E", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("0"))
            {
                string msg = parts.Length > 1
                    ? string.Join(" ", parts, 1, parts.Length - 1).Trim()
                    : (status == "0" ? "Authentication failed. Check your SAP credentials." : "SAP returned an error.");
                obj["EX_RETURN"] = new Newtonsoft.Json.Linq.JObject(
                    new Newtonsoft.Json.Linq.JProperty("TYPE",    "E"),
                    new Newtonsoft.Json.Linq.JProperty("MESSAGE", msg)
                );
                return obj.ToString(Newtonsoft.Json.Formatting.None);
            }

            // ── Success (Response:1#... or Response:S#...) ───────────────────
            obj["EX_RETURN"] = new Newtonsoft.Json.Linq.JObject(
                new Newtonsoft.Json.Linq.JProperty("TYPE",    "S"),
                new Newtonsoft.Json.Linq.JProperty("MESSAGE", "")
            );

            if (bapi.Equals("ZWM_USER_AUTHORITY_CHECK", StringComparison.OrdinalIgnoreCase))
            {
                // Login: Response:1#WERKS
                // Derive EX_GROUP from WERKS (same logic as v11.83 app):
                //   DH* plants = DC/Warehouse
                //   DH25       = Ecomm
                //   everything else = Store
                string werks = parts.Length > 1 ? parts[1].Trim() : "";
                string group = werks.StartsWith("DH", StringComparison.OrdinalIgnoreCase) ? "DC" : "";
                if (werks.Equals("DH25", StringComparison.OrdinalIgnoreCase)) group = "";
                obj["EX_WERKS"] = werks;
                obj["EX_GROUP"] = group;
            }
            else
            {
                // All other RFCs: pass the raw response through as a data field
                // The app fragments check EX_RETURN.TYPE first, then read their
                // own specific EX_ fields — for now pass raw in EX_RETURN.MESSAGE
                // so at minimum it doesn't crash, and we can map fields later
                obj["EX_RETURN"]["MESSAGE"] = raw;

                // Also put full raw response in ET_DATA for fragments that read it
                var arr = new Newtonsoft.Json.Linq.JArray();
                var row = new Newtonsoft.Json.Linq.JObject();
                row["RESPONSE"] = raw;
                arr.Add(row);
                obj["EX_RETURN"]["ET_DATA"] = arr;
            }

            return obj.ToString(Newtonsoft.Json.Formatting.None);
        }



        private class OpcodeStats
        {
            private readonly object _lock = new object();
            private readonly List<long> _samples = new List<long>(200);

            public long     Count     { get; private set; }
            public long     Errors    { get; private set; }
            public double   MinMs     { get; private set; }
            public double   MaxMs     { get; private set; }
            public double   AvgMs     { get; private set; }
            public double   P95Ms     { get; private set; }
            public string   LastError { get; private set; }
            public DateTime LastSeen  { get; private set; }

            public OpcodeStats(long ms, bool ok, string err)
            {
                MinMs = MaxMs = AvgMs = P95Ms = ms;
                Count = 1; Errors = ok ? 0 : 1; LastError = err;
                LastSeen = DateTime.UtcNow; _samples.Add(ms);
            }

            public void Record(long ms, bool ok, string err)
            {
                lock (_lock)
                {
                    Count++;
                    if (!ok) { Errors++; if (err != null) LastError = err; }
                    if (ms < MinMs) MinMs = ms;
                    if (ms > MaxMs) MaxMs = ms;
                    AvgMs = (AvgMs * (Count - 1) + ms) / Count;
                    LastSeen = DateTime.UtcNow;
                    if (_samples.Count >= 200) _samples.RemoveAt(0);
                    _samples.Add(ms);
                    var sorted = new List<long>(_samples); sorted.Sort();
                    P95Ms = sorted[Math.Max(0, (int)Math.Ceiling(sorted.Count * 0.95) - 1)];
                }
            }

            // Restore from persisted data (on startup)
            public void RestoreFrom(PersistedStats p)
            {
                lock (_lock)
                {
                    Count     = p.Count;
                    Errors    = p.Errors;
                    MinMs     = p.MinMs;
                    MaxMs     = p.MaxMs;
                    AvgMs     = p.AvgMs;
                    P95Ms     = p.P95Ms;
                    LastError = p.LastError;
                    DateTime.TryParse(p.LastSeen, out var dt);
                    LastSeen  = dt;
                    // Seed samples with AvgMs for percentile continuity
                    _samples.Clear();
                    for (int i = 0; i < Math.Min(10, (int)p.Count); i++)
                        _samples.Add((long)p.AvgMs);
                }
            }
        }
    }
}
