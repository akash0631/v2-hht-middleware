using System.Web.Http;

namespace V2HHTMiddleware.App_Start
{
    public static class WebApiConfig
    {
        public static void Register(HttpConfiguration config)
        {
            // Attribute routing (used by HHTController [Route] attributes)
            config.MapHttpAttributeRoutes();

            // Conventional fallback
            config.Routes.MapHttpRoute(
                name: "DefaultApi",
                routeTemplate: "api/{controller}/{id}",
                defaults: new { id = RouteParameter.Optional }
            );

            // Return JSON by default (not XML)
            var formatters = config.Formatters;
            formatters.Remove(formatters.XmlFormatter);
        }
    }
}
