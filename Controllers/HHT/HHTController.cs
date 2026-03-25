using System;
using System.Collections.Concurrent;
using System.Configuration;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;

namespace V2HHTMiddleware.Controllers.HHT
{
    [RoutePrefix("api/hht")]
    public class HHTController : ApiController
    {
        // ── Constants ──────────────────────────────────────────────────────────
        private const string APK_VERSION      = "12.098";
        private const string APK_URL          = "https://assets.eatnubo.com/hht/V2_HHT_Azure_Release.apk";
        private const string MW_VERSION       = "v2-hht-azure|4.0";

        // ── Singleton HttpClient ───────────────────────────────────────────────
        private static readonly HttpClient _http;
        private static volatile string _javaBase = null;
        private static readonly object _discoveryLock = new object();

        // ── In-memory ring buffer for live stats (last 1000 calls) ─────────────
        // Flushed to /api/hht/stats and App Insights telemetry
        private static readonly ConcurrentQueue<CallLog> _ring = new ConcurrentQueue<CallLog>();
        private const int RING_MAX = 1000;

        // ── Per-opcode accumulator for performance profiling ───────────────────
        private static readonly ConcurrentDictionary<string, OpcodeStats> _opcodeStats
            = new ConcurrentDictionary<string, OpcodeStats>(StringComparer.OrdinalIgnoreCase);

        static HHTController()
        {
            var handler = new HttpClientHandler
            {
                MaxConnectionsPerServer = 300,
                UseProxy              = false,
                AllowAutoRedirect     = false
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(55) };
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

        // ── App version ────────────────────────────────────────────────────────
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

            return Txt(
                $"OK|{MW_VERSION}" +
                $"|apk={APK_VERSION}" +
                $"|java={javaBase ?? "not-discovered"}" +
                $"|java={javaStatus}" +
                $"|calls_total={TotalCalls()}" +
                $"|{DateTime.UtcNow:yyyy-MM-dd HH:mm}UTC"
            );
        }

        // ── Stats dashboard ────────────────────────────────────────────────────
        [HttpGet, Route("stats")]
        public HttpResponseMessage Stats()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== V2 HHT Azure Middleware — Live Stats ===");
            sb.AppendLine($"Time      : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"MW Version: {MW_VERSION}");
            sb.AppendLine($"Java Base : {_javaBase ?? "not-discovered"}");
            sb.AppendLine();

            // Opcode performance table
            sb.AppendLine("OPCODE PERFORMANCE (all-time since last restart):");
            sb.AppendLine($"{"Opcode",-40} {"Calls",6} {"Errors",6} {"Avg ms",8} {"Min ms",8} {"Max ms",8} {"P95 ms",8}");
            sb.AppendLine(new string('-', 90));

            foreach (var kv in _opcodeStats)
            {
                var s = kv.Value;
                sb.AppendLine($"{kv.Key,-40} {s.Count,6} {s.Errors,6} {s.AvgMs,8:F0} {s.MinMs,8:F0} {s.MaxMs,8:F0} {s.P95Ms,8:F0}");
            }

            sb.AppendLine();

            // Last 20 calls
            sb.AppendLine("LAST 20 CALLS:");
            sb.AppendLine($"{"Timestamp",-20} {"Opcode",-35} {"Store",-6} {"Ms",6} {"SAP_OK",6} {"Resp",35}");
            sb.AppendLine(new string('-', 110));

            var recent = _ring.ToArray();
            int start  = Math.Max(0, recent.Length - 20);
            for (int i = recent.Length - 1; i >= start; i--)
            {
                var c = recent[i];
                string icon = c.SapOk ? "✅" : "❌";
                sb.AppendLine($"{c.Timestamp:HH:mm:ss.fff}         {c.Opcode,-35} {c.Store,-6} {c.ElapsedMs,6} {icon,6}  {c.ResponseSnippet,-35}");
            }

            return Txt(sb.ToString());
        }

        // ── Per-opcode drill-down ──────────────────────────────────────────────
        [HttpGet, Route("stats/{opcode}")]
        public HttpResponseMessage StatsOpcode(string opcode)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Opcode Drill-down: {opcode} ===");

            if (_opcodeStats.TryGetValue(opcode, out var s))
            {
                sb.AppendLine($"Total calls : {s.Count}");
                sb.AppendLine($"Errors      : {s.Errors}");
                sb.AppendLine($"Avg latency : {s.AvgMs:F0}ms");
                sb.AppendLine($"Min latency : {s.MinMs:F0}ms");
                sb.AppendLine($"Max latency : {s.MaxMs:F0}ms");
                sb.AppendLine($"P95 latency : {s.P95Ms:F0}ms");
                sb.AppendLine($"Last error  : {s.LastError ?? "none"}");
                sb.AppendLine($"Last seen   : {s.LastSeen:yyyy-MM-dd HH:mm:ss} UTC");
            }
            else
            {
                sb.AppendLine("No data yet for this opcode.");
            }

            sb.AppendLine();
            sb.AppendLine("Recent calls for this opcode:");
            var calls = _ring.ToArray();
            int shown = 0;
            for (int i = calls.Length - 1; i >= 0 && shown < 30; i--)
            {
                var c = calls[i];
                if (!c.Opcode.Equals(opcode, StringComparison.OrdinalIgnoreCase)) continue;
                string icon = c.SapOk ? "✅" : "❌";
                sb.AppendLine($"  {c.Timestamp:HH:mm:ss}  Store={c.Store}  {c.ElapsedMs}ms  {icon}  {c.ResponseSnippet}");
                shown++;
            }

            return Txt(sb.ToString());
        }

        // ═══════════════════════════════════════════════════════════════════════
        // CORE PROXY
        // ═══════════════════════════════════════════════════════════════════════

        private async Task<HttpResponseMessage> Proxy()
        {
            string javaBase = GetJavaBase();
            if (javaBase == null)
                return LogAndReturn(null, 0, "E#HC tunnel down — cannot reach Server 200",
                    false, "E#HC tunnel down — cannot reach Server 200");

            string body    = await Request.Content.ReadAsStringAsync().ConfigureAwait(false);
            string opcode  = ExtractOpcode(body);
            string store   = ExtractStore(body);
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

                var resp = await _http.SendAsync(req).ConfigureAwait(false);
                sw.Stop();
                respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                sapOk    = IsSapOk(respBody);
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
        // LOGGING ENGINE
        // ═══════════════════════════════════════════════════════════════════════

        private HttpResponseMessage LogAndReturn(
            string opcode, long elapsedMs, string respBody, bool sapOk, string store)
        {
            if (opcode != null)
            {
                // 1. Ring buffer
                var entry = new CallLog
                {
                    Timestamp       = DateTime.UtcNow,
                    Opcode          = opcode,
                    Store           = store ?? "?",
                    ElapsedMs       = elapsedMs,
                    SapOk           = sapOk,
                    ResponseSnippet = (respBody ?? "").Length > 60
                        ? respBody.Substring(0, 60)
                        : respBody ?? ""
                };
                _ring.Enqueue(entry);
                while (_ring.Count > RING_MAX) _ring.TryDequeue(out _);

                // 2. Opcode stats
                _opcodeStats.AddOrUpdate(opcode,
                    _ => new OpcodeStats(elapsedMs, sapOk,
                        sapOk ? null : (respBody ?? "").Substring(0, Math.Min(80, (respBody ?? "").Length))),
                    (_, existing) =>
                    {
                        existing.Record(elapsedMs, sapOk,
                            sapOk ? null : (respBody ?? "").Substring(0, Math.Min(80, (respBody ?? "").Length)));
                        return existing;
                    });

                // 3. Structured log line to stdout → App Insights picks this up automatically
                //    Format: [HHT] TIMESTAMP|OPCODE|STORE|MS|OK|RESP_SNIPPET
                var snippet = (respBody ?? "").Replace("\n", " ").Replace("|", ":");
                if (snippet.Length > 80) snippet = snippet.Substring(0, 80);
                Console.WriteLine(
                    $"[HHT] {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}|{opcode}|{store}|{elapsedMs}|{(sapOk?"OK":"ERR")}|{snippet}");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respBody ?? "", Encoding.UTF8, "text/plain")
            };
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
                var tasks = new System.Collections.Generic.List<Task>();

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
                                var w = new System.Collections.Generic.List<System.Net.Sockets.Socket> { sock };
                                var e = new System.Collections.Generic.List<System.Net.Sockets.Socket> { sock };
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

        // Extract opcode from request body: "opcode#param1#..."
        private static string ExtractOpcode(string body)
        {
            if (string.IsNullOrEmpty(body)) return "unknown";
            int idx = body.IndexOf('#');
            return idx > 0 ? body.Substring(0, idx).Trim().ToLowerInvariant() : body.Trim().ToLowerInvariant();
        }

        // Extract store/plant (typically param index 1 for store opcodes, varies)
        private static string ExtractStore(string body)
        {
            if (string.IsNullOrEmpty(body)) return "?";
            var parts = body.Split('#');
            // Most store opcodes: opcode#STORE#...
            // Auth: scnrec#USER#PASS#STORE
            if (parts.Length >= 4 && parts[0].Equals("scnrec", StringComparison.OrdinalIgnoreCase))
                return parts[3];
            if (parts.Length >= 2 && parts[1].Length >= 4 && parts[1].Length <= 6)
                return parts[1];
            return "?";
        }

        // SAP response is OK if it starts with S#, 1#, 0, or contains real data
        private static bool IsSapOk(string resp)
        {
            if (string.IsNullOrEmpty(resp)) return true; // empty = no error
            var r = resp.TrimStart();
            // Definite errors
            if (r.StartsWith("E#HC tunnel")   ||
                r.StartsWith("E#Proxy error") ||
                r.StartsWith("E#SAP timeout") ||
                r.StartsWith("E#not-discovered")) return false;
            // SAP business errors — still count as "reached SAP" = infrastructure OK
            // We log them but mark as SapOk=true (SAP responded correctly)
            return true;
        }

        private long TotalCalls()
        {
            long total = 0;
            foreach (var s in _opcodeStats.Values) total += s.Count;
            return total;
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

        private class OpcodeStats
        {
            private readonly object _lock = new object();
            private readonly System.Collections.Generic.List<long> _samples
                = new System.Collections.Generic.List<long>(200);

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
                Count = 1;
                Errors = ok ? 0 : 1;
                LastError = err;
                LastSeen  = DateTime.UtcNow;
                _samples.Add(ms);
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

                    // Keep last 200 samples for percentile calc
                    if (_samples.Count >= 200) _samples.RemoveAt(0);
                    _samples.Add(ms);

                    // Recalculate P95
                    var sorted = new System.Collections.Generic.List<long>(_samples);
                    sorted.Sort();
                    int p95idx = (int)Math.Ceiling(sorted.Count * 0.95) - 1;
                    P95Ms = sorted[Math.Max(0, p95idx)];
                }
            }
        }
    }
}
