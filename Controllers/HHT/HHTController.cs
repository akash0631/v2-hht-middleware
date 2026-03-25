using System;
using System.Configuration;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;

namespace V2HHTMiddleware.Controllers.HHT
{
    [RoutePrefix("api/hht")]
    public class HHTController : ApiController
    {
        // ── APK metadata served directly by Azure — no dependency on Server 200 ──
        private const string APK_VERSION       = "12.098";
        private const string APK_DOWNLOAD_URL  = "https://assets.eatnubo.com/hht/V2_HHT_Azure_Release.apk";
        private const string MIDDLEWARE_VERSION = "v2-hht-azure-proxy|3.0";

        // ── Singleton HttpClient ──────────────────────────────────────────────
        private static readonly HttpClient _http;
        private static volatile string _javaBase = null;
        private static readonly object _discoveryLock = new object();

        static HHTController()
        {
            var handler = new HttpClientHandler
            {
                MaxConnectionsPerServer = 300,
                UseProxy = false,
                AllowAutoRedirect = false
            };
            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(55)
            };
        }

        // ── HC proxy discovery ────────────────────────────────────────────────
        private static string GetJavaBase()
        {
            if (_javaBase != null) return _javaBase;
            lock (_discoveryLock)
            {
                if (_javaBase != null) return _javaBase;

                var found = new System.Collections.Concurrent.ConcurrentBag<int>();
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

        // ── Main opcode endpoint — all legacy URL patterns ────────────────────
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

        // ── App version — served directly by Azure, no Java dependency ────────
        // Android calls this on startup to check for APK updates.
        // Returns current v12.098 with CDN download link.
        [HttpGet, Route("appversion")]
        public HttpResponseMessage AppVersion()
            => Json($"{{\"upgrade\":\"force\",\"version\":\"{APK_VERSION}\",\"downloadLink\":\"{APK_DOWNLOAD_URL}\"}}");

        [HttpGet, Route("ValueXMW/appversion")]
        public HttpResponseMessage AppVersionLegacy()
            => Json($"{{\"upgrade\":\"force\",\"version\":\"{APK_VERSION}\",\"downloadLink\":\"{APK_DOWNLOAD_URL}\"}}");

        // ── Health — full pipeline status ─────────────────────────────────────
        [HttpGet, Route("health")]
        public async Task<HttpResponseMessage> Health()
        {
            string javaBase   = GetJavaBase();
            string javaStatus = "unreachable";

            if (javaBase != null)
            {
                try
                {
                    var r = await _http.GetAsync(javaBase.Replace("/xmwgw", "") + "/index.jsp")
                                       .ConfigureAwait(false);
                    javaStatus = "ok:" + (int)r.StatusCode;
                }
                catch (Exception ex)
                {
                    javaStatus = "err:" + ex.Message.Substring(0, Math.Min(60, ex.Message.Length)).Replace("\n", " ");
                }
            }

            string body = $"OK|{MIDDLEWARE_VERSION}" +
                          $"|apk={APK_VERSION}" +
                          $"|java={javaBase ?? "not-discovered"}" +
                          $"|java={javaStatus}" +
                          $"|{DateTime.UtcNow:yyyy-MM-dd HH:mm}UTC";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain")
            };
        }

        // ── Proxy core ────────────────────────────────────────────────────────
        private async Task<HttpResponseMessage> Proxy()
        {
            string javaBase = GetJavaBase();
            if (javaBase == null)
                return Txt("E#Azure middleware cannot reach Server 200 — HC tunnel down");

            try
            {
                string body   = await Request.Content.ReadAsStringAsync().ConfigureAwait(false);
                string target = javaBase + "/ValueXMW";

                var req = new HttpRequestMessage(HttpMethod.Post, target)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain")
                };

                // Forward device headers
                foreach (var h in Request.Headers)
                    if (h.Key.StartsWith("X-HHT-", StringComparison.OrdinalIgnoreCase))
                        req.Headers.TryAddWithoutValidation(h.Key, h.Value);

                var resp     = await _http.SendAsync(req).ConfigureAwait(false);
                string rBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(rBody, System.Text.Encoding.UTF8, "text/plain")
                };
            }
            catch (TaskCanceledException)
            {
                return Txt("E#Timeout — SAP did not respond in time");
            }
            catch (Exception ex)
            {
                return Txt("E#Proxy error: " + ex.Message.Replace("\n", " "));
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static HttpResponseMessage Txt(string s) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(s, System.Text.Encoding.UTF8, "text/plain")
            };

        private static HttpResponseMessage Json(string s) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(s, System.Text.Encoding.UTF8, "application/json")
            };
    }
}
