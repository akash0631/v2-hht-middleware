using System;
using System.Linq;
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
        [HttpPost, Route("")]
        public async Task<HttpResponseMessage> Handle()
            => await ProcessRequest();

        [HttpPost, Route("ValueXMW")]
        public async Task<HttpResponseMessage> LegacyValueXMW()
            => await ProcessRequest();

        [HttpPost, Route("ValueXMW/{app}")]
        public async Task<HttpResponseMessage> LegacyValueXMWApp(string app)
            => await ProcessRequest();

        [HttpPost, Route("ValueXMW/{app}/{platform}/{version}")]
        public async Task<HttpResponseMessage> LegacyFull(string app, string platform, string version)
            => await ProcessRequest();

        [HttpPost, Route("~/ValueXMW/{app}/{platform}/{version}")]
        public Task<HttpResponseMessage> LegacyRoot(string app, string platform, string version)
            => ProcessRequest();

        [HttpGet, Route("appversion")]
        public HttpResponseMessage AppVersion()
            => Txt("1|11|83|" + DateTime.UtcNow.ToString("yyyy-MM-dd"));

        [HttpGet, Route("ValueXMW/appversion")]
        public HttpResponseMessage AppVersionLegacy()
            => Txt("1|11|83|" + DateTime.UtcNow.ToString("yyyy-MM-dd"));

        [HttpGet, Route("health")]
        public HttpResponseMessage Health()
        {
            string proxyIp = JavaMWProxy.DiscoverIP() ?? "not-found";
            return Txt($"OK|v2-hht-proxy|java-mw={proxyIp}:9080|{DateTime.UtcNow:yyyy-MM-dd HH:mm}UTC");
        }

        // ── Proxy all HHT requests to Java middleware on Server 200 via HC tunnel ──
        private static async Task<HttpResponseMessage> ProcessRequest()
        {
            try
            {
                string proxyIp = JavaMWProxy.DiscoverIP();
                if (proxyIp == null)
                    return Txt("E#Java middleware not reachable via HC tunnel");

                string raw = "";
                // Get request body from current HttpContext since ApiController.Request
                // may not be available in static context
                var ctx = System.Web.HttpContext.Current;
                if (ctx?.Request != null)
                {
                    using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                        raw = await Task.Run(() => sr.ReadToEnd());
                }

                string targetUrl = $"http://{proxyIp}:9080/xmwgw/ValueXMW";

                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                {
                    var content = new StringContent(raw, Encoding.UTF8, "text/plain");
                    var resp = await client.PostAsync(targetUrl, content);
                    string body = await resp.Content.ReadAsStringAsync();
                    return Txt(body);
                }
            }
            catch (TaskCanceledException)
            {
                return Txt("E#Request timed out. SAP may be busy. Please retry.");
            }
            catch (Exception ex)
            {
                return Txt("E#" + ex.Message);
            }
        }

        private static HttpResponseMessage Txt(string body)
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK);
            r.Content = new StringContent(body, Encoding.UTF8, "text/plain");
            return r;
        }
    }

    internal static class TaskExt
    {
        public static async Task<T> TimeoutAfter<T>(this Task<T> t, TimeSpan timeout)
        {
            if (await Task.WhenAny(t, Task.Delay(timeout)) != t)
                throw new TimeoutException();
            return await t;
        }
    }
}
