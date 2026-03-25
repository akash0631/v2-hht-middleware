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
        // ── Singleton HttpClient — shared across all requests & instances ──────
        // HttpClient is thread-safe and designed to be reused.
        // Single instance = connection pooling, no socket exhaustion.
        private static readonly HttpClient _http;
        private static volatile string _javaBase = null;
        private static readonly object _discoveryLock = new object();

        static HHTController()
        {
            // Allow up to 300 concurrent connections (1000 devices, ~3 connections each)
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

        // ── Discover HC proxy IP for port 9080 ──────────────────────────────
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

                // Pick lowest index for determinism
                int best = int.MaxValue;
                foreach (var x in found) if (x < best) best = x;

                _javaBase = best < int.MaxValue
                    ? $"http://127.0.0.{best}:9080/xmwgw"
                    : null;

                return _javaBase;
            }
        }

        // ── Legacy URL formats the Android app uses ─────────────────────────
        [HttpPost, Route("ValueXMW")]
        public Task<HttpResponseMessage> LegacyValueXMW() => Proxy();

        [HttpPost, Route("ValueXMW/{app}")]
        public Task<HttpResponseMessage> LegacyValueXMWApp(string app) => Proxy();

        [HttpPost, Route("ValueXMW/{app}/{platform}/{version}")]
        public Task<HttpResponseMessage> LegacyFull(string app, string platform, string version) => Proxy();

        [HttpPost, Route("~/ValueXMW/{app}/{platform}/{version}")]
        public Task<HttpResponseMessage> LegacyRoot(string app, string platform, string version) => Proxy();

        [HttpPost, Route("")]
        public Task<HttpResponseMessage> Handle() => Proxy();

        // ── Version check — Android app calls this on startup ───────────────
        [HttpGet, Route("appversion")]
        public Task<HttpResponseMessage> AppVersion() => ProxyGet("appversion?appName=V2RetailOps&platform=Android&majorVersion=11&minorVersion=83");

        [HttpGet, Route("ValueXMW/appversion")]
        public Task<HttpResponseMessage> AppVersionLegacy() => ProxyGet("appversion?appName=V2RetailOps&platform=Android&majorVersion=11&minorVersion=83");

        // ── Health — shows Azure + Java status ──────────────────────────────
        [HttpGet, Route("health")]
        public async Task<HttpResponseMessage> Health()
        {
            string javaBase = GetJavaBase();
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
                    javaStatus = "err:" + ex.Message.Replace("\n", " ").Substring(0, Math.Min(50, ex.Message.Length));
                }
            }

            string body = $"OK|v2-hht-azure-proxy|java={javaBase ?? "not-discovered"}|java-status={javaStatus}|{DateTime.UtcNow:yyyy-MM-dd HH:mm}UTC";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain")
            };
        }

        // ── Core proxy logic ─────────────────────────────────────────────────
        private async Task<HttpResponseMessage> Proxy()
        {
            string javaBase = GetJavaBase();
            if (javaBase == null)
            {
                return Txt("E#Java middleware not reachable — HC tunnel down");
            }

            try
            {
                // Read raw POST body
                string body = await Request.Content.ReadAsStringAsync().ConfigureAwait(false);

                // Build target URL: preserve /ValueXMW path that Java expects
                string target = javaBase + "/ValueXMW";

                var req = new HttpRequestMessage(HttpMethod.Post, target)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain")
                };

                // Forward device-identifying headers if present
                foreach (var h in Request.Headers)
                {
                    if (h.Key.StartsWith("X-HHT-", StringComparison.OrdinalIgnoreCase))
                        req.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }

                var resp = await _http.SendAsync(req).ConfigureAwait(false);
                string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(respBody, System.Text.Encoding.UTF8, "text/plain")
                };
            }
            catch (TaskCanceledException)
            {
                return Txt("E#Request timeout — SAP took too long");
            }
            catch (Exception ex)
            {
                return Txt("E#Proxy error: " + ex.Message.Replace("\n", " "));
            }
        }

        private async Task<HttpResponseMessage> ProxyGet(string path)
        {
            string javaBase = GetJavaBase();
            if (javaBase == null) return Txt("E#not-discovered");
            try
            {
                var resp = await _http.GetAsync($"{javaBase}/{path}").ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain")
                };
            }
            catch { return Txt("E#proxy-error"); }
        }

        private static HttpResponseMessage Txt(string s) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(s, System.Text.Encoding.UTF8, "text/plain")
            };
    }
}
