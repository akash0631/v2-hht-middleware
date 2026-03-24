using System.Web;
using System.Web.Http;
using V2HHTMiddleware.App_Start;

namespace V2HHTMiddleware
{
    public class Global : HttpApplication
    {
        protected void Application_Start()
        {
            GlobalConfiguration.Configure(WebApiConfig.Register);

            // Pre-warm SAP connection pool at startup
            HHTBaseHandler.InitializeSapPool();
        }
    }
}
