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

        // ── Response cache ──────────────────────────────────────────────────────
        // Caches read-only RFC responses to reduce SAP load by ~40%
        // Key: "opcode:body_hash"  Value: (json_response, expiry_utc)
        private static readonly ConcurrentDictionary<string, (string Body, DateTime Expiry)>
            _cache = new ConcurrentDictionary<string, (string, DateTime)>(StringComparer.OrdinalIgnoreCase);

        // Read-only opcodes whose responses can be safely cached
        private static readonly HashSet<string> CACHEABLE = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // DC get/list operations (60s TTL)
            "zgrt_pick_get_to_list","zgrt_pick_get_to_list_ptl","zgrt_pick_get_to_list_ptl_v3",
            "zgrt_pick_get_pick_data","zgrt_pick_get_pick_data_v4",
            "zwm_get_msa_section_list","zwm_get_grc_bins","zwm_get_packing_material",
            "zwm_get_stock_bin","zwm_get_stock_take_id","zwm_store_grt_category",
            "zwm_store_get_major_cat","zwm_store_get_major_cat_data",
            "zwm_ptl_get_zone","zwm_ptl_get_zone_station_v3","zwm_ptl_hubstn_data_rfc_v3",
            "zwm_ptl_get_to_details","zwm_picklist_pppn",
            "zfms_screen","zwm_get_hhtuser_delivery",
            // Validate ops that are effectively lookups (30s TTL)
            "zwm_rfc_validate_dc_sloc","zwm_validate_dc_sloc",
            "zwm_gr_get_details","zwm_store_get_stock",
            // Stock take lookups
            "zwm_rfc_stock_take_get_details","stocktakegetdetails","stockvalidatebarcode"
        };

        // Persist last-known Java proxy IP for fast startup
        private static readonly string JAVA_IP_FILE =
            Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? @"D:\home", "data", "java_proxy_ip.txt");

        // ── In-memory ring buffer (last 1000 calls) ────────────────────────────
        private static readonly ConcurrentQueue<CallLog> _ring = new ConcurrentQueue<CallLog>();
        private const int RING_MAX = 1000;

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
            // SocketsHttpHandler for connection pooling + keep-alive + gzip
            var handler = new System.Net.Http.SocketsHttpHandler
            {
                MaxConnectionsPerServer    = 300,
                PooledConnectionLifetime   = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout= TimeSpan.FromMinutes(2),
                UseProxy                   = false,
                AllowAutoRedirect          = false,
                AutomaticDecompression     = System.Net.DecompressionMethods.GZip
                                           | System.Net.DecompressionMethods.Deflate
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(55) };
            _http.DefaultRequestHeaders.Add("Connection", "keep-alive");
            _http.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate");

            // Load persisted stats from disk immediately
            LoadStatsFromDisk();

            // Flush stats to disk every 60 seconds
            _flushTimer = new Timer(_ => FlushStatsToDisk(), null,
                TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

            // Pre-warm Java proxy IP from last-known file
            if (File.Exists(JAVA_IP_FILE))
            {
                try { _javaBase = File.ReadAllText(JAVA_IP_FILE).Trim(); }
                catch { }
            }

            // Cache cleanup every 5 minutes
            new Timer(_ => {
                var now = DateTime.UtcNow;
                foreach (var k in _cache.Keys)
                    if (_cache.TryGetValue(k, out var v) && v.Expiry < now)
                        _cache.TryRemove(k, out _);
            }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
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
            sb.AppendLine("LAST 20 CALLS:");
            sb.AppendLine($"{"Timestamp",-20} {"Opcode",-35} {"Store",-6} {"Ms",6} {"SAP_OK",6} {"Resp",35}");
            sb.AppendLine(new string('-', 110));
            var recent = _ring.ToArray();
            int start  = Math.Max(0, recent.Length - 20);
            for (int i = recent.Length - 1; i >= start; i--)
            {
                var c = recent[i];
                sb.AppendLine($"{c.Timestamp:HH:mm:ss.fff}         {c.Opcode,-35} {c.Store,-6} {c.ElapsedMs,6} {(c.SapOk?"✅":"❌"),6}  {c.ResponseSnippet,-35}");
            }

            return Txt(sb.ToString());
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

            string opcode = ExtractOpcode(body);
            string store  = ExtractStore(body);
            var    sw     = Stopwatch.StartNew();
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

            return LogAndReturn(opcode, sw.ElapsedMilliseconds, respBody, sapOk, store);
        }

        // ═══════════════════════════════════════════════════════════════════════
        // LOGGING
        // ═══════════════════════════════════════════════════════════════════════

        private HttpResponseMessage LogAndReturn(string opcode, long ms, string resp, bool ok, string store)
        {
            if (opcode != null)
            {
                // Ring buffer
                var entry = new CallLog
                {
                    Timestamp       = DateTime.UtcNow,
                    Opcode          = opcode,
                    Store           = store ?? "?",
                    ElapsedMs       = ms,
                    SapOk           = ok,
                    ResponseSnippet = (resp ?? "").Length > 60
                        ? resp.Substring(0, 60) : resp ?? ""
                };
                _ring.Enqueue(entry);
                while (_ring.Count > RING_MAX) _ring.TryDequeue(out _);

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
            var parts = body.Split('#');
            if (parts.Length >= 4 && parts[0].Equals("scnrec", StringComparison.OrdinalIgnoreCase)) return parts[3];
            if (parts.Length >= 2 && parts[1].Length >= 4 && parts[1].Length <= 6) return parts[1];
            return "?";
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
            public long     ElapsedMs       { get; set; }
            public bool     SapOk           { get; set; }
            public string   ResponseSnippet { get; set; }
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

            string opcode = bapi.Equals("ZWM_USER_AUTHORITY_CHECK", System.StringComparison.OrdinalIgnoreCase)
                            ? "scnrec" : bapi.ToLower();

            var sw = Stopwatch.StartNew();

            // ── Cache check ──────────────────────────────────────────────────────
            // Return cached response for read-only RFCs (avoids redundant SAP calls)
            string cacheKey = null;
            if (CACHEABLE.Contains(opcode))
            {
                // Key = opcode + hash of IM_ params (same user+params = same result)
                int bodyHash = rawBody.GetHashCode();
                cacheKey = opcode + ":" + bodyHash;
                if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expiry > DateTime.UtcNow)
                {
                    // Cache hit — return immediately, no SAP call needed
                    LogAndReturn(opcode, 0, cached.Body, true, opcode);
                    var cachedResp = Request.CreateResponse(System.Net.HttpStatusCode.OK);
                    cachedResp.Content = new StringContent(cached.Body, Encoding.UTF8, "application/json");
                    cachedResp.Headers.Add("X-Cache", "HIT");
                    return cachedResp;
                }
            }

            // ── PATH A: Java /noacljsonrfcadaptor (native SAP JSON response) ────
            try
            {
                string noaclUrl = javaBase.Replace("/xmwgw", "/xmwgw/noacljsonrfcadaptor")
                                  + "?" + (qs.Count > 0 ? qs.ToString() : "bapiname=" + bapi + "&aclclientid=android");

                // CRITICAL: set Content-Type as MediaTypeHeaderValue (no charset suffix)
                // Java's noacljsonrfcadaptor checks for exact "application/json"
                var noaclContent = new StringContent(rawBody, Encoding.UTF8);
                noaclContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                var noaclReq  = new HttpRequestMessage(HttpMethod.Post, noaclUrl) { Content = noaclContent };
                var noaclResp = await _http.SendAsync(noaclReq).ConfigureAwait(false);
                string noaclRaw = await noaclResp.Content.ReadAsStringAsync().ConfigureAwait(false);

                // Java accepted the request if response is valid JSON (not the content-type error string)
                if (!string.IsNullOrEmpty(noaclRaw) &&
                    !noaclRaw.Contains("Only Applicaton/Json") &&
                    !noaclRaw.Contains("Content Type Not supported") &&
                    noaclRaw.TrimStart().StartsWith("{"))
                {
                    sw.Stop();
                    bool ok = IsInfraOk(noaclRaw);
                    LogAndReturn(opcode, (long)sw.ElapsedMilliseconds, noaclRaw, ok, opcode);
                    // Store in cache if this is a cacheable opcode
                    if (cacheKey != null)
                    {
                        int ttlSec = CACHEABLE.Contains(opcode) ? 60 : 30;
                        _cache[cacheKey] = (noaclRaw, DateTime.UtcNow.AddSeconds(ttlSec));
                    }
                    var nativeResp = Request.CreateResponse(System.Net.HttpStatusCode.OK);
                    nativeResp.Content = new StringContent(noaclRaw, Encoding.UTF8, "application/json");
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
