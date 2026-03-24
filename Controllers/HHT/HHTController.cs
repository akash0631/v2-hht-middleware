using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;

namespace V2HHTMiddleware.Controllers.HHT
{
    /// <summary>
    /// Single entry point for ALL Android HHT device requests.
    ///
    /// NEW URL:    POST https://v2-hht-api.azurewebsites.net/api/hht
    /// LEGACY URL: POST https://v2-hht-api.azurewebsites.net/ValueXMW/{app}/{platform}/{version}
    ///
    /// Android app migration = change base URL only. Zero protocol changes.
    ///
    /// Request body:  opcode#param1#param2#...   (UTF-8 plain text)
    /// Optional header:  X-HHT-Env: QA   → route to QA SAP instead of Prod (testing only)
    ///
    /// Response:  S#...  (success)  or  E#...  (error)  — same as old xmwgw
    /// </summary>
    [RoutePrefix("api/hht")]
    public class HHTController : ApiController
    {
        // ── Main endpoint ──────────────────────────────────────────────────────
        [HttpPost, Route("")]
        public async Task<HttpResponseMessage> Handle()
        {
            string raw = "";
            try
            {
                raw = (await Request.Content.ReadAsStringAsync() ?? "").Trim();
                if (string.IsNullOrEmpty(raw))
                    return Txt("E#Empty request");

                int sep   = raw.IndexOf('#');
                string op = (sep > 0 ? raw.Substring(0, sep) : raw).ToLower().Trim();

                if (string.IsNullOrEmpty(op))
                    return Txt("E#Missing opcode");

                // Optional: route to QA SAP for testing
                bool useQa = Request.Headers.Contains("X-HHT-Env") &&
                             string.Join("", Request.Headers.GetValues("X-HHT-Env")).Equals("QA", StringComparison.OrdinalIgnoreCase);

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

        // ── Health check ───────────────────────────────────────────────────────
        [HttpGet, Route("health")]
        public HttpResponseMessage Health()
        {
            int count = HHTRouter.AllOpcodes().Count();
            return Txt($"OK|v2-hht-middleware|opcodes={count}|{DateTime.UtcNow:yyyy-MM-dd HH:mm}UTC");
        }

        // ── Legacy URL — old Android apps keep working during cutover ──────────
        [HttpPost, Route("~/ValueXMW/{app}/{platform}/{version}")]
        public Task<HttpResponseMessage> Legacy(string app, string platform, string version)
            => Handle();

        // ─────────────────────────────────────────────────────────────────────
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
