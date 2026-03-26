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
        // ── noacljsonrfcadaptor — new app format (v12+) ────────────────────
        // Forwards to Java's own /noacljsonrfcadaptor endpoint (not /ValueXMW)
        [HttpPost, Route("noacljsonrfcadaptor")]
        public Task<HttpResponseMessage> NoAclJson() => ProxyNoAcl();

        [HttpGet, Route("noacljsonrfcadaptor")]
        public Task<HttpResponseMessage> NoAclJsonGet() => ProxyNoAcl();

        [HttpPost, Route("~/ValueXMW/{app}/{platform}/{version}")]
        public Task<HttpResponseMessage> ValueXMWRoot(string app, string platform, string version) => Proxy();

        [HttpGet, Route("appversion")]
        public HttpResponseMessage AppVersion()
            => Json($"{{\"upgrade\":\"force\",\"version\":\"{APK_VERSION}\",\"downloadLink\":\"{APK_URL}\"}}");

        [HttpGet, Route("ValueXMW/appversion")]
        public HttpResponseMessage AppVersionLegacy()
            => Json($"{{\"upgrade\":\"force\",\"version\":\"{APK_VERSION}\",\"downloadLink\":\"{APK_URL}\"}}");

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

        // ── Stats ──────────────────────────────────────────────────────────────
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

        private async Task<HttpResponseMessage> ProxyNoAcl()
        {
            // New HHT app v12+ calls /noacljsonrfcadaptor?bapiname=RFC_NAME
            // with JSON body { "bapiname":"RFC_NAME", "IM_USERID":"user", "IM_PASSWORD":"pass" }
            // Java server only has /ValueXMW — so we translate to old opcode format here.

            string javaBase = GetJavaBase();
            if (javaBase == null)
                return LogAndReturn(null, 0, "E#HC tunnel down", false, "?");

            string rawBody = await Request.Content.ReadAsStringAsync().ConfigureAwait(false);
            var sw = Stopwatch.StartNew();

            // Parse the JSON body
            string opcode  = "";
            string huser   = "";
            string hpass   = "";
            string hplant  = "1006"; // default plant

            try
            {
                var json = Newtonsoft.Json.Linq.JObject.Parse(rawBody);
                opcode = json["bapiname"]?.ToString() ?? "";
                huser  = json["IM_USERID"]?.ToString() ?? "";
                hpass  = json["IM_PASSWORD"]?.ToString() ?? "";
                if (json["IM_PLANT"] != null) hplant = json["IM_PLANT"].ToString();
            }
            catch { /* fall through with empty strings */ }

            // Also check query string for bapiname
            if (string.IsNullOrEmpty(opcode))
            {
                var qs = System.Web.HttpUtility.ParseQueryString(Request.RequestUri.Query);
                opcode = qs["bapiname"] ?? "";
            }

            if (string.IsNullOrEmpty(opcode))
                return LogAndReturn(null, 0, "E#missing bapiname", false, "?");

            // Build old-format form body for Java /ValueXMW
            string formBody = $"opcode={Uri.EscapeDataString(opcode)}" +
                              $"&Huser={Uri.EscapeDataString(huser)}" +
                              $"&Hpassword={Uri.EscapeDataString(hpass)}" +
                              $"&Hplant={Uri.EscapeDataString(hplant)}";

            string respBody;
            bool   sapOk;
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, javaBase + "/ValueXMW")
                {
                    Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded")
                };
                var resp = await _http.SendAsync(req).ConfigureAwait(false);
                sw.Stop();
                respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                sapOk    = IsInfraOk(respBody);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return LogAndReturn(null, (int)sw.ElapsedMilliseconds, "E#" + ex.Message, false, opcode);
            }

            RecordCall(opcode, (int)sw.ElapsedMilliseconds, sapOk);
            var response = Request.CreateResponse(System.Net.HttpStatusCode.OK);
            response.Content = new StringContent(respBody ?? "Response:null", Encoding.UTF8, "application/json");
            return response;
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
