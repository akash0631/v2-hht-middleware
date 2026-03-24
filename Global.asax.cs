using System.Web;
using System.Web.Http;

namespace V2HHTMiddleware
{
    public class Global : HttpApplication
    {
        protected void Application_Start()
        {
            GlobalConfiguration.Configure(WebApiConfig.Register);
        }
    }
}
