using SAP.Middleware.Connector;

namespace V2HHTMiddleware.Controllers.HHT.Handlers.Auth
{
    /// <summary>opcode: scnrec | Input: scnrec#USERNAME#PASSWORD | Output: 1#WERKS or 0</summary>
    public class ScnrecHandler : HHTBaseHandler
    {
        private readonly bool _qa;
        public ScnrecHandler(bool qa) { _qa = qa; }

        public override string Execute()
        {
            try
            {
                var dest = _qa ? QA() : Prod();
                var fun  = dest.Repository.CreateFunction("ZWM_USER_AUTHORITY_CHECK");
                fun.SetValue("IM_USERID",   P(1));
                fun.SetValue("IM_PASSWORD", P(2));
                fun.Invoke(dest);
                var ret   = fun.GetStructure("EX_RETURN");
                var werks = fun.GetString("EX_WERKS");
                return ret.GetString("TYPE") != "E" ? "1#" + werks : "0";
            }
            catch (System.Exception ex) { return "0#" + ex.Message; }
        }
    }
}
