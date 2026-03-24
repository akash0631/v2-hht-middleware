using System.Web;
using System.Web.Http;
using V2HHTMiddleware.App_Start;
using V2HHTMiddleware.Controllers.HHT;

namespace V2HHTMiddleware
{
    public class Global : HttpApplication
    {
        protected void Application_Start()
        {
            GlobalConfiguration.Configure(WebApiConfig.Register);
            HHTBaseHandler.InitializeSapPool();
        }
    }
}
