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
        // ── Main endpoint ──────────────────────────────────────────────────────
        [HttpPost, Route("")]
        public async Task<HttpResponseMessage> Handle()
        {
            return await ProcessRequest();
        }

        // ── Legacy alias: POST /api/hht/ValueXMW (APK sends here) ─────────────
        [HttpPost, Route("ValueXMW")]
        public async Task<HttpResponseMessage> LegacyValueXMW()
        {
            return await ProcessRequest();
        }

        // ── Legacy alias with segments (old xmwgw URL format) ─────────────────
        [HttpPost, Route("ValueXMW/{app}")]
        public async Task<HttpResponseMessage> LegacyValueXMWApp(string app)
        {
            return await ProcessRequest();
        }

        [HttpPost, Route("ValueXMW/{app}/{platform}/{version}")]
        public async Task<HttpResponseMessage> LegacyFull(string app, string platform, string version)
        {
            return await ProcessRequest();
        }

        // ── Old root-level legacy (kept for safety) ───────────────────────────
        [HttpPost, Route("~/ValueXMW/{app}/{platform}/{version}")]
        public Task<HttpResponseMessage> LegacyRoot(string app, string platform, string version)
            => ProcessRequest();

        // ── App version check — APK calls this on startup ─────────────────────
        [HttpGet, Route("appversion")]
        [HttpGet, Route("ValueXMW/appversion")]
        public HttpResponseMessage AppVersion()
        {
            // Return format the APK expects — tells it no update needed
            return Txt("1|11|83|" + DateTime.UtcNow.ToString("yyyy-MM-dd"));
        }

        // ── Health check ───────────────────────────────────────────────────────
        [HttpGet, Route("health")]
        public HttpResponseMessage Health()
        {
            int count = HHTRouter.AllOpcodes().Count();
            return Txt($"OK|v2-hht-middleware|opcodes={count}|{DateTime.UtcNow:yyyy-MM-dd HH:mm}UTC");
        }

        // ── Core processing logic ──────────────────────────────────────────────
        private async Task<HttpResponseMessage> ProcessRequest()
        {
            try
            {
                string raw = (await Request.Content.ReadAsStringAsync() ?? "").Trim();
                if (string.IsNullOrEmpty(raw))
                    return Txt("E#Empty request");

                int sep   = raw.IndexOf('#');
                string op = (sep > 0 ? raw.Substring(0, sep) : raw).ToLower().Trim();

                if (string.IsNullOrEmpty(op))
                    return Txt("E#Missing opcode");

                bool useQa = Request.Headers.Contains("X-HHT-Env") &&
                             string.Join("", Request.Headers.GetValues("X-HHT-Env"))
                                   .Equals("QA", StringComparison.OrdinalIgnoreCase);

                var handler = HHTRouter.Resolve(op, useQa);
                if (handler == null)
                    return Txt($"E#Unknown opcode: {op}");

                handler.SetRequest(raw);
                string response = await Task.Run(() => handler.Execute())
                                            .TimeoutAfter(TimeSpan.FromSeconds(60));
                return Txt(response);
            }
            catch (TimeoutException)
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
