using SAP.Middleware.Connector;

namespace V2HHTMiddleware.Controllers.HHT.Handlers.DC
{
    public class NitRecHandler : HHTBaseHandler {
        readonly bool _qa; public NitRecHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_PO_SCAN_DATA_SAVE");var p=P(1).Split(',');f.SetValue("IM_LGNUM","V2R");f.SetValue("IM_EBELN",p[0]);f.SetValue("IM_XBLNR",p[1]);f.SetValue("IM_BILL",p[2]);f.SetValue("IM_GATE_ENTRY",p[3]);f.SetValue("IM_FRBNR",p[4]);f.SetValue("IM_USER",p[5]);var t=f.GetTable("IT_DATA");for(int i=6;i+3<p.Length;i+=4){t.Append();t.SetValue("MATERIAL",p[i]);t.SetValue("SCAN_QTY",p[i+1]);t.SetValue("CRATE",p[i+2]);t.SetValue("LGPLA",p[i+3]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class NitDelHandler : HHTBaseHandler {
        readonly bool _qa; public NitDelHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_PO_GET_DETAILS");f.SetValue("IM_EBELN",P(1));f.SetValue("IM_XBLNR",P(2));f.SetValue("IM_GATE_ENTRY",P(3));f.SetValue("IM_BILL",P(4));f.SetValue("IM_USER",P(5));f.SetValue("IM_FRBNR",P(6));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+Tbl(f.GetTable("ET_PO_DATA"),"MATNR","MENGE","LGPLA")+"!"+EanData(f.GetTable("ET_EAN_DATA"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class NitUpdHandler : HHTBaseHandler {
        readonly bool _qa; public NitUpdHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_VALIDATE_CRATE");f.SetValue("IM_EBELN",P(1));f.SetValue("IM_XBLNR",P(2));f.SetValue("IM_CRATE",P(3));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ScnDeliveryHandler : HHTBaseHandler {
        readonly bool _qa; public ScnDeliveryHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_DELIVERY_GET_DETAILS");f.SetValue("IM_VBELN",P(1));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));var likp=f.GetStructure("EX_LIKP");var hdr=likp.GetString("KUNNR")+"#"+likp.GetString("VBELN");return"S#"+hdr+"#"+Tbl(f.GetTable("ET_LIPS"),"MATNR","WERKS","LGORT","CHARG","LFIMG","VRKME","ORMNG")+"!"+EanData(f.GetTable("ET_EAN_DATA"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ScnSelHandler : HHTBaseHandler
    {
        readonly bool _qa;
        public ScnSelHandler(bool qa) { _qa = qa; }

        // ── ET_BIN_MC in-memory cache ─────────────────────────────────────────
        // ET_BIN_MC = full bin-material map for the DC warehouse.
        // It's the same for every delivery from a given site and barely changes
        // during a shift — cache it per WERKS for 10 minutes to avoid the
        // full warehouse bin scan on every scnsel call.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedBinMc>
            _binMcCache = new System.Collections.Concurrent.ConcurrentDictionary<string, CachedBinMc>();

        private class CachedBinMc
        {
            public string Data;
            public System.DateTime Expiry;
        }

        public override string Execute()
        {
            try
            {
                var dest = _qa ? QA() : Prod();
                var fn   = dest.Repository.CreateFunction("ZWM_DELIVERY_GET_DETAILS_PLP2");
                fn.SetValue("IM_VBELN", P(1));
                fn.Invoke(dest);

                var ret = fn.GetStructure("EX_RETURN");
                if (ret.GetString("TYPE") == "E")
                    return Err(ret.GetString("MESSAGE"));

                // ── ET_LIPS — correct fields from original Java implementation ─
                // MATNR, WERKS, LGORT, CHARG, LFIMG, VRKME, ORMNG,
                // MANDT, MATKL, WGBEZ, VLPLA, VISTM, VEMNG, CRATE, REMAIN_QTY
                var lips = Tbl(fn.GetTable("ET_LIPS"),
                    "MATNR", "WERKS", "LGORT", "CHARG", "LFIMG", "VRKME", "ORMNG",
                    "MANDT", "MATKL", "WGBEZ", "VLPLA", "VISTM", "VEMNG", "CRATE", "REMAIN_QTY");

                // ── EX_LIKP — delivery header (customer + delivery no) ─────────
                var likp   = fn.GetStructure("EX_LIKP");
                var header = likp.GetString("KUNNR") + "#" + likp.GetString("VBELN");

                // ── ET_EAN_DATA ───────────────────────────────────────────────
                var ean = EanData(fn.GetTable("ET_EAN_DATA"));

                // ── ET_BIN_MC — cache by WERKS (DC site) for 10 minutes ───────
                // Extract WERKS from first LIPS row for cache key
                var lipsTable = fn.GetTable("ET_LIPS");
                string werks  = lipsTable.RowCount > 0 ? lipsTable[0].GetString("WERKS") : "DC";
                string cacheKey = (_qa ? "QA_" : "P_") + werks;

                string binMc;
                if (_binMcCache.TryGetValue(cacheKey, out var cached) &&
                    cached.Expiry > System.DateTime.UtcNow)
                {
                    binMc = cached.Data;  // ✅ cache hit — skip the slow SAP bin scan
                }
                else
                {
                    binMc = Tbl(fn.GetTable("ET_BIN_MC"), "LGPLA", "MATNR", "VEMNG");
                    _binMcCache[cacheKey] = new CachedBinMc
                    {
                        Data   = binMc,
                        Expiry = System.DateTime.UtcNow.AddMinutes(10)
                    };
                }

                // Response: S # header # lips ! ean ! binmc
                return "S#" + header + "#" + lips + "!" + ean + "!" + binMc;
            }
            catch (System.Exception ex) { return Err(ex.Message); }
        }
    }
    public class DisRecHandler : HHTBaseHandler {
        readonly bool _qa; public DisRecHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_FM_BARCODE_GET_TO_DATA");f.SetValue("I_TONUMBER",P(1));f.SetValue("ZWERKS","1006");f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+Tbl(f.GetTable("E_TOITEMDATA"),"TANUM","TAPOS","MATNR","VEMNG","LGPLA");}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StockTakeGetDetailsHandler : HHTBaseHandler {
        readonly bool _qa; public StockTakeGetDetailsHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_STOCK_TAKE_GET_DETAILS");f.SetValue("IM_STOCK_TAKE",P(1));f.SetValue("IM_RFC","X");f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+Tbl(f.GetTable("ET_ITEM"),"MATNR","MENGE","LGPLA")+"!"+Tbl(f.GetTable("ET_BIN"),"LGPLA")+"!"+Tbl(f.GetTable("ET_CRATE"),"EXIDV");}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StockTakeSaveDataHandler : HHTBaseHandler {
        readonly bool _qa; public StockTakeSaveDataHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_STOCK_TAKE_SAVE_DATA");var p=P(1).Split(',');f.SetValue("IM_STOCK_TAKE",p[0]);f.SetValue("IM_USER",p[1]);f.SetValue("IM_RFC","X");var t=f.GetTable("IT_ITEM");for(int i=2;i+15<p.Length;i+=16){t.Append();t.SetValue("MANDT",p[i]);t.SetValue("STOCK_TAKE",p[i+1]);t.SetValue("POSNR",p[i+2]);t.SetValue("WERKS",p[i+3]);t.SetValue("LGNUM",p[i+4]);t.SetValue("LGTYP",p[i+5]);t.SetValue("LGPLA",p[i+6]);t.SetValue("MATNR",p[i+7]);t.SetValue("MENGE",p[i+8]);t.SetValue("MEINS",p[i+9]);t.SetValue("CRATE",p[i+10]);t.SetValue("TANUM",p[i+11]);t.SetValue("TAPOS",p[i+12]);t.SetValue("ERNAM",p[i+13]);t.SetValue("ERDAT",p[i+14]);t.SetValue("UZEIT",p[i+15]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StockValidateBarcodeHandler : HHTBaseHandler {
        readonly bool _qa; public StockValidateBarcodeHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_STOCK_VALIDATE_BARCODE");f.SetValue("IM_BARCODE",P(1));f.SetValue("IM_LGNUM",P(2));f.SetValue("IM_LGTYP",P(3));f.SetValue("IM_RFC","X");f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+EanData(f.GetTable("ET_EAN_DATA"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StockTakeArtiValiHandler : HHTBaseHandler {
        readonly bool _qa; public StockTakeArtiValiHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_STOCK_TAKE_ARTI_VALI");f.SetValue("IM_BARCODE",P(1));f.SetValue("IM_SITE",P(2));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));var m=f.GetStructure("EX_MARM");return"S#"+m.GetString("MATNR")+"#"+m.GetString("UMREZ")+"#"+m.GetString("EAN11");}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StockTakeBinValiHandler : HHTBaseHandler {
        readonly bool _qa; public StockTakeBinValiHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_STOCK_TAKE_BIN_VALI");f.SetValue("IM_BIN",P(1));f.SetValue("IM_SITE",P(2));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StockTakeCrateValiHandler : HHTBaseHandler {
        readonly bool _qa; public StockTakeCrateValiHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_STOCK_TAKE_CRATE_VALI");f.SetValue("IM_CRATE",P(1));f.SetValue("IM_SITE",P(2));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StockTakeSaveV11Handler : HHTBaseHandler {
        readonly bool _qa; public StockTakeSaveV11Handler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_STOCK_TAKE_SAVE_V11");var p=P(1).Split(',');f.SetValue("IM_USER",p[0]);var t=f.GetTable("IT_DATA");for(int i=1;i+8<p.Length;i+=9){t.Append();t.SetValue("WAREHOUSE",p[i]);t.SetValue("SITE",p[i+1]);t.SetValue("SLOC",p[i+2]);t.SetValue("CRATE",p[i+3]);t.SetValue("BIN_TYPE",p[i+4]);t.SetValue("BIN",p[i+5]);t.SetValue("MATERIAL",p[i+6]);t.SetValue("SCAN_QTY",p[i+7]);t.SetValue("KEY",p[i+8]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StockValidateV21Handler : HHTBaseHandler {
        readonly bool _qa; public StockValidateV21Handler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_STOCK_VALIDATE_V21");f.SetValue("IM_USER",P(1));f.SetValue("TYPE",P(2));f.SetValue("BIN",P(3));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StockMovementV21Handler : HHTBaseHandler {
        readonly bool _qa; public StockMovementV21Handler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_STOCK_MOVEMENT_V21");f.SetValue("IM_USER",P(1));f.SetValue("PICK_PUTAWAY",P(2));f.SetValue("TYPE",P(3));f.SetValue("PLANT",P(4));f.SetValue("WAREHOUSE","V2R");f.SetValue("LOCATION","0001");f.SetValue("STORAGE_TYPE","E01");f.SetValue("BIN",P(5));f.SetValue("DESTINATION_BIN",P(6));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class DcHuGrtValHandler : HHTBaseHandler {
        readonly bool _qa; public DcHuGrtValHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_DC_HU_GRT_VAL");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_SLGORT",P(2));f.SetValue("IM_DLGORT",P(3));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class DcHuGrtBinHuValHandler : HHTBaseHandler {
        readonly bool _qa; public DcHuGrtBinHuValHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_DC_HUGRT_BINHU_VAL");f.SetValue("IM_LGPLA",P(1));f.SetValue("IM_SITE",P(2));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class DcHuGrtHuValHandler : HHTBaseHandler {
        readonly bool _qa; public DcHuGrtHuValHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_DC_HUGRT_HU_VAL");f.SetValue("IM_EXIDV",P(1));f.SetValue("IM_WERKS",P(2));f.SetValue("IM_SLOC",P(3));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class DcHuGrtSaveHandler : HHTBaseHandler {
        readonly bool _qa; public DcHuGrtSaveHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_DC_HUGRT_SAVE");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_USER",p[1]);f.SetValue("IM_SLGORT",p[2]);f.SetValue("IM_DLGORT",p[3]);var t=f.GetTable("IT_DATA");for(int i=4;i+1<p.Length;i+=2){t.Append();t.SetValue("LGPLA",p[i]);t.SetValue("EX_HU",p[i+1]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ClaBinValidateHandler : HHTBaseHandler {
        readonly bool _qa; public ClaBinValidateHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_CLA_BIN_VALIDATE");f.SetValue("PALETTE",P(1));f.SetValue("CLABIN",P(2));f.SetValue("INDICATOR",P(3));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ClaHuValidateHandler : HHTBaseHandler {
        readonly bool _qa; public ClaHuValidateHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_CLA_HU_VALIDATE");f.SetValue("EXIDV",P(1));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ClaPaletteValidateHandler : HHTBaseHandler {
        readonly bool _qa; public ClaPaletteValidateHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_CLA_PALETTE_VALIDATE");f.SetValue("PALETTE",P(1));f.SetValue("INDICATOR",P(2));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ClaHuPaletteSaveHandler : HHTBaseHandler {
        readonly bool _qa; public ClaHuPaletteSaveHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_CLA_HU_PALETTE_SAVE");var p=P(1).Split(',');f.SetValue("EXIDV",p[0]);f.SetValue("WERKS",p[1]);f.SetValue("PALETTE",p[2]);var t=f.GetTable("IM_DATA");for(int i=3;i+1<p.Length;i+=2){t.Append();t.SetValue("EXIDV",p[i]);t.SetValue("PALETTE",p[i+1]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ClaPaletteBinTagSaveHandler : HHTBaseHandler {
        readonly bool _qa; public ClaPaletteBinTagSaveHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_CLA_PALETTE_BIN_TAG_SAVE");f.SetValue("PALETTE",P(1));f.SetValue("CLABIN",P(2));f.SetValue("INDICATOR",P(3));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ValidateCrateToHandler : HHTBaseHandler {
        readonly bool _qa; public ValidateCrateToHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_VALIDATE_CRATE");f.SetValue("IM_EBELN",P(1));f.SetValue("IM_XBLNR",P(2));f.SetValue("IM_CRATE",P(3));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class SaveCrateHandler : HHTBaseHandler {
        readonly bool _qa; public SaveCrateHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_TO_CREATE_FROM_SCAN_DATA");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_MBLNR",p[1]);var t=f.GetTable("IT_DATA");for(int i=2;i+1<p.Length;i+=2){t.Append();t.SetValue("CRATE",p[i]);t.SetValue("LGPLA",p[i+1]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ValidateExternalHuHandler : HHTBaseHandler {
        readonly bool _qa; public ValidateExternalHuHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_VALIDATE_EXTERNAL_HU");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_EXIDV",P(2));f.SetValue("IM_DWERKS",P(3));f.SetValue("IM_VBELN",P(4));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
}
