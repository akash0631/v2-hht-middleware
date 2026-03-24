using SAP.Middleware.Connector;
using System.Text;

namespace V2HHTMiddleware.Controllers.HHT.Handlers.Store
{
    // Every handler follows the same pattern:
    // - Constructor takes bool qa (false = prod SAP, true = QA SAP)
    // - Execute() calls Prod() or QA() accordingly
    // - Input comes from Parts[] via P(index)
    // - Output is S#... or E#...

    public class GetStoreStockHandler : HHTBaseHandler {
        readonly bool _qa; public GetStoreStockHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GET_STOCK");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_EAN11",P(2));f.SetValue("IM_LGORT",P(3).StartsWith("00")?P(3):"0001");f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));var m=f.GetStructure("EX_MARD");return"S##"+m.GetString("MATNR")+"#"+m.GetString("LABST")+"#"+m.GetString("PSTAT")+"#"+m.GetString("PRCTL")+"!"+EanData(f.GetTable("ET_EAN_DATA"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class GetStoreStockTakeHandler : HHTBaseHandler {
        readonly bool _qa; public GetStoreStockTakeHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GET_STOCK");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_EAN11",P(2));f.SetValue("IM_STOCK_TAKE","X");f.SetValue("IM_LGORT","0001");f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));var m=f.GetStructure("EX_MARD");return"S##"+m.GetString("MATNR")+"#"+m.GetString("LABST")+"#"+m.GetString("PSTAT")+"#"+m.GetString("PRCTL")+"!"+EanData(f.GetTable("ET_EAN_DATA"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class GetGrtStockHandler : HHTBaseHandler {
        readonly bool _qa; public GetGrtStockHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GET_GRTSTOCK");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_EAN11",P(2));f.SetValue("IM_LGORT",P(3).StartsWith("00")?P(3):"0001");f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));var m=f.GetStructure("EX_MARD");return"S##"+m.GetString("MATNR")+"#"+m.GetString("LABST")+"!"+EanData(f.GetTable("ET_EAN_DATA"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StoreGetBinHandler : HHTBaseHandler {
        readonly bool _qa; public StoreGetBinHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GET_BIN");f.SetValue("IM_WERKS",P(1));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"))+"!";return"S#"+Tbl(f.GetTable("ET_LAGP"),"LGPLA")+"!";}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StoreGetBinV2Handler : HHTBaseHandler {
        readonly bool _qa; public StoreGetBinV2Handler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GET_BIN_V2");f.SetValue("IM_WERKS",P(1));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"))+"!";return"S#"+Tbl(f.GetTable("ET_LAGP"),"LGPLA")+"!";}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StoreGetBinStockHandler : HHTBaseHandler {
        readonly bool _qa; public StoreGetBinStockHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GET_BIN_STOCK");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_LGPLA",P(2));f.SetValue("IM_LGNUM","SDC");f.SetValue("IM_LGORT",P(3).StartsWith("00")?P(3):"0002");f.Invoke(d);var r=f.GetStructure("EX_RETURN");var e2=EanData(f.GetTable("ET_EAN_DATA"));if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"))+"!"+e2;return"S#"+Tbl(f.GetTable("ET_STOCK"),"MATERIAL","AVL_STOCK","OPEN_STOCK","SCAN_QTY","BIN")+"!"+e2;}catch(System.Exception e){return Err(e.Message);}}
    }
    public class GetMatBinStockHandler : HHTBaseHandler {
        readonly bool _qa; public GetMatBinStockHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GET_MAT_BIN_STOCK");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_LGORT","0002");f.SetValue("IM_LGPLA",P(2));f.SetValue("IM_LGNUM","SDC");f.SetValue("IM_EAN11",P(4));f.Invoke(d);var r=f.GetStructure("EX_RETURN");var e2=EanData(f.GetTable("ET_EAN_DATA"));if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"))+"!"+e2;return"S#"+Tbl(f.GetTable("ET_STOCK"),"MATERIAL","AVL_STOCK","OPEN_STOCK","SCAN_QTY","BIN")+"!"+e2;}catch(System.Exception e){return Err(e.Message);}}
    }
    public class GetMatBinStockBtoBHandler : HHTBaseHandler {
        readonly bool _qa; public GetMatBinStockBtoBHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GET_MAT_BIN_STOCK");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_LGORT","0002");f.SetValue("IM_LGPLA",P(2));f.SetValue("IM_LGNUM","SDC");f.SetValue("IM_EAN11",P(4));f.SetValue("IM_BIN_TO_BIN","X");f.Invoke(d);var r=f.GetStructure("EX_RETURN");var e2=EanData(f.GetTable("ET_EAN_DATA"));if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"))+"!"+e2;return"S#"+Tbl(f.GetTable("ET_STOCK"),"MATERIAL","AVL_STOCK","OPEN_STOCK","SCAN_QTY","BIN")+"!"+e2;}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ValidateBinHandler : HHTBaseHandler {
        readonly bool _qa; public ValidateBinHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_GET_BIN_DETAILS");f.SetValue("IM_LGNUM",P(1));f.SetValue("IM_LGPLA",P(2));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StoreBinListValidationHandler : HHTBaseHandler {
        readonly bool _qa; public StoreBinListValidationHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_BIN_LIST_VALIDATION");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_PICNR",P(2));f.SetValue("IM_LGORT",P(3));f.SetValue("IM_LGNUM",P(4));f.Invoke(d);var r=f.GetStructure("EX_RETURN");var e2=EanData(f.GetTable("ET_EAN_DATA"));if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"))+"!"+e2;return"S#"+Tbl(f.GetTable("ET_PICKLIST"),"MATERIAL","AVL_STOCK","SCAN_QTY","BIN")+"!"+e2;}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StoreBinConHuGetDetailsHandler : HHTBaseHandler {
        readonly bool _qa; public StoreBinConHuGetDetailsHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_BINCONHU_GET_DETAILS");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_LGNUM",P(2));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+Tbl(f.GetTable("ET_DATA"),"EXIDV","LGPLA","MATNR","VEMNG");}catch(System.Exception e){return Err(e.Message);}}
    }
    public class SaveEmptyBinHandler : HHTBaseHandler {
        readonly bool _qa; public SaveEmptyBinHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_SAVE_EMPTY_BIN");f.SetValue("IM_LGNUM",P(1));f.SetValue("IM_USER",P(2));f.SetValue("IM_WERKS",P(3));var p=P(4).Split(',');var t=f.GetTable("IT_DATA");for(int i=0;i+5<p.Length;i+=6){t.Append();t.SetValue("WAREHOUSE",p[i]);t.SetValue("CRATE",p[i+1]);t.SetValue("BIN_TYPE",p[i+2]);t.SetValue("BIN",p[i+3]);t.SetValue("SCAN_QTY",p[i+4]);t.SetValue("KEY",p[i+5]);}f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ValidateEmptyBinHandler : HHTBaseHandler {
        readonly bool _qa; public ValidateEmptyBinHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_VALIDATE_EMPTY_BIN");f.SetValue("IM_LGNUM",P(1));f.SetValue("IM_LGPLA",P(2));f.SetValue("IM_WERKS",P(3));f.SetValue("IM_CRATE",P(4));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ValiCrateEmptyBinHandler : HHTBaseHandler {
        readonly bool _qa; public ValiCrateEmptyBinHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_VALI_CRATE_EMPTYBIN");f.SetValue("IM_LGNUM",P(1));f.SetValue("IM_WERKS",P(2));f.SetValue("IM_CRATE",P(3));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ValidateSlocHandler : HHTBaseHandler {
        readonly bool _qa; public ValidateSlocHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_VALIDATE_SLOC");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_LGORT",P(2));f.SetValue("IM_LGNUM","SDC");f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class GetSlocHandler : HHTBaseHandler {
        readonly bool _qa; public GetSlocHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GET_SLOC");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_WM_MANAGED","");f.SetValue("IM_LGNUM","SDC");f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+Tbl(f.GetTable("ET_SLOC_DST"),"LGORT","LGOBE")+"!"+Tbl(f.GetTable("ET_SLOC_SRC"),"LGORT","LGOBE");}catch(System.Exception e){return Err(e.Message);}}
    }
    public class GetStorePicklistHandler : HHTBaseHandler {
        readonly bool _qa; public GetStorePicklistHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GET_PICKLIST");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_PICNR",P(2));f.SetValue("IM_LGORT","0002");f.SetValue("IM_LGNUM","SDC");f.Invoke(d);var r=f.GetStructure("EX_RETURN");var e2=EanData(f.GetTable("ET_EAN_DATA"));if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"))+"!"+e2;return"S#"+Tbl(f.GetTable("ET_PICKLIST"),"MATERIAL","AVL_STOCK","SCAN_QTY","BIN")+"!"+e2;}catch(System.Exception e){return Err(e.Message);}}
    }
    public class GetStorePicklistV2Handler : GetStorePicklistHandler { public GetStorePicklistV2Handler(bool qa):base(qa){} }
    public class SaveDirectPickingHandler : HHTBaseHandler {
        readonly bool _qa; public SaveDirectPickingHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_DIRECT_PICKING");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_LGNUM","SDC");f.SetValue("IM_USER",p[1]);var t=f.GetTable("IT_DATA");for(int i=2;i+2<p.Length;i+=3){t.Append();t.SetValue("MATERIAL",p[i]);t.SetValue("SCAN_QTY",p[i+1]);t.SetValue("BIN",p[i+2]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class SaveDirectPickingV2Handler : HHTBaseHandler {
        readonly bool _qa; public SaveDirectPickingV2Handler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_DIRECT_PICKING");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_LGNUM","SDC");f.SetValue("IM_USER",p[1]);var t=f.GetTable("IT_DATA");for(int i=2;i+3<p.Length;i+=4){t.Append();t.SetValue("MATERIAL",p[i]);t.SetValue("SCAN_QTY",p[i+1]);t.SetValue("BIN",p[i+2]);t.SetValue("PICNR",p[i+3]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class PicklistNosDispHandler : HHTBaseHandler {
        readonly bool _qa; public PicklistNosDispHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_PICKLIST_NOS_DISP");f.SetValue("LV_WERKS",P(1));f.SetValue("LV_DATE",P(2));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+Tbl(f.GetTable("LT_PICNR"),"PICNR","PICNR_DATE");}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ZhhtusrDelPickingHandler : HHTBaseHandler {
        readonly bool _qa; public ZhhtusrDelPickingHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZHHTUSR_DEL_PICKING_RFC");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_DEL_DATE",P(2));f.SetValue("IM_DEL_DATE2",P(3));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+Tbl(f.GetTable("ET_DATA"),"PICNR","WERKS","LGNUM");}catch(System.Exception e){return Err(e.Message);}}
    }
    public class SaveGrcPutawayHandler : HHTBaseHandler {
        readonly bool _qa; public SaveGrcPutawayHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GRC_PUTWAY");var p=P(1).Split(',');f.SetValue("IM_EXIDV",p[0]);f.SetValue("IM_WERKS",p[1]);f.SetValue("IM_LGNUM","SDC");f.SetValue("IM_PARTIAL","");f.SetValue("IM_USER",p[2]);var t=f.GetTable("IT_DATA");for(int i=3;i+2<p.Length;i+=3){t.Append();t.SetValue("MATNR",p[i]);t.SetValue("VEMNG",p[i+1]);t.SetValue("LGPLA",p[i+2]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class SaveFloorPutawayHandler : HHTBaseHandler {
        readonly bool _qa; public SaveFloorPutawayHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_FLOOR_PUTWAY");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_LGNUM","SDC");f.SetValue("IM_USER",p[1]);var t=f.GetTable("IT_DATA");for(int i=2;i+2<p.Length;i+=3){t.Append();t.SetValue("MATNR",p[i]);t.SetValue("VEMNG",p[i+1]);t.SetValue("LGPLA",p[i+2]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class SaveFloorPutawayTakeHandler : HHTBaseHandler {
        readonly bool _qa; public SaveFloorPutawayTakeHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_FLOOR_PUTWAY");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_LGNUM","SDC");f.SetValue("IM_STOCK_TAKE","X");f.SetValue("IM_USER",p[1]);var t=f.GetTable("IT_DATA");for(int i=2;i+2<p.Length;i+=3){t.Append();t.SetValue("MATNR",p[i]);t.SetValue("VEMNG",p[i+1]);t.SetValue("LGPLA",p[i+2]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class FloorPutawayNewHandler : HHTBaseHandler {
        readonly bool _qa; public FloorPutawayNewHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_FLOOR_PUAWAY_NEW");f.SetValue("P_EXIDV",P(1));f.SetValue("P_LGPLA",P(2));f.SetValue("P_WERKS",P(3));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StoreFloorPutawayHuHandler : HHTBaseHandler {
        readonly bool _qa; public StoreFloorPutawayHuHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_FLOOR_PUTWAY_HU");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_LGNUM","SDC");f.SetValue("IM_USER",P(2));f.SetValue("IM_HU",P(3));f.SetValue("IM_LGPLA",P(4));f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StoreHuPutawayBinConHandler : HHTBaseHandler {
        readonly bool _qa; public StoreHuPutawayBinConHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_HU_PUTWAY_BIN_CON");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_LGNUM",p[1]);var t=f.GetTable("IT_DATA");for(int i=2;i+1<p.Length;i+=2){t.Append();t.SetValue("BIN",p[i]);t.SetValue("HU_NO",p[i+1]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class SaveGrtFromMsaHandler : HHTBaseHandler {
        readonly bool _qa; public SaveGrtFromMsaHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GRT_FROM_MSA");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_LGNUM","SDC");f.SetValue("IM_USER",p[1]);f.SetValue("IM_PACK_MAT",p[2]);var t=f.GetTable("IT_DATA");for(int i=3;i+2<p.Length;i+=3){t.Append();t.SetValue("MATERIAL",p[i]);t.SetValue("SCAN_QTY",p[i+1]);t.SetValue("BIN",p[i+2]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class SaveGrtFromDisplayHandler : HHTBaseHandler {
        readonly bool _qa; public SaveGrtFromDisplayHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GRT_FROM_DISP_AREA");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_LGORT_SRC",p[1]);f.SetValue("IM_LGORT_DEST",p[2]);f.SetValue("IM_LGNUM","0002");f.SetValue("IM_USER",p[3]);f.SetValue("IM_PACK_MAT",p[4]);var t=f.GetTable("IT_DATA");for(int i=5;i+1<p.Length;i+=2){t.Append();t.SetValue("MATERIAL",p[i]);t.SetValue("SCAN_QTY",p[i+1]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class GrtSaveHandler : HHTBaseHandler {
        readonly bool _qa; public GrtSaveHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_GRT_SAVE");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_LGORT",p[1]);f.SetValue("IM_USER",p[2]);f.SetValue("IM_CRATE",p[3]);var t=f.GetTable("IT_DATA");for(int i=4;i+15<p.Length;i+=16){t.Append();t.SetValue("WM_NO",p[i]);t.SetValue("MATERIAL",p[i+1]);t.SetValue("PLANT",p[i+2]);t.SetValue("STOR_LOC",p[i+3]);t.SetValue("BATCH",p[i+4]);t.SetValue("CRATE",p[i+5]);t.SetValue("BIN",p[i+6]);t.SetValue("STORAGE_TYPE",p[i+7]);t.SetValue("MEINS",p[i+8]);t.SetValue("AVL_STOCK",p[i+9]);t.SetValue("OPEN_STOCK",p[i+10]);t.SetValue("SCAN_QTY",p[i+11]);t.SetValue("PICNR",p[i+12]);t.SetValue("PICK_QTY",p[i+13]);t.SetValue("HU_NO",p[i+14]);t.SetValue("BARCODE",p[i+15]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class GrtPutwayCrateValHandler : HHTBaseHandler {
        readonly bool _qa; public GrtPutwayCrateValHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_GRT_PUTWAY_CRATE_VAL");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_CRATE",P(2));f.SetValue("IM_LGPLA",P(3));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class GrtPutawayPostHandler : HHTBaseHandler {
        readonly bool _qa; public GrtPutawayPostHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_GRT_PUTWAY_POST");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_CRATE",P(2));f.SetValue("IM_LGPLA",P(3));f.SetValue("IM_USER",P(4));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class HuGetDetailsHandler : HHTBaseHandler {
        readonly bool _qa; public HuGetDetailsHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_HU_GET_DETAILS");f.SetValue("IM_EXIDV",P(1));f.SetValue("IM_WERKS",P(2));f.SetValue("IM_LGNUM","SDC");f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+Tbl(f.GetTable("ET_HU_ITEM"),"MATNR","VEMNG","MEINS")+"!"+Tbl(f.GetTable("ET_LAGP"),"LGPLA")+"!"+EanData(f.GetTable("ET_EAN_DATA"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class GetHusHandler : HHTBaseHandler {
        readonly bool _qa; public GetHusHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GET_HUS");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_VBELN",P(2));f.SetValue("IM_EDOCNO",P(3));f.SetValue("IM_LGNUM","SDC");f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+Tbl(f.GetTable("ET_HUS"),"EXIDV","VEMNG","MATNR");}catch(System.Exception e){return Err(e.Message);}}
    }
    public class SaveHusHandler : HHTBaseHandler {
        readonly bool _qa; public SaveHusHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_HU_GRC");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_LGNUM","SDC");f.SetValue("IM_USER",p[1]);var t=f.GetTable("IT_DATA");for(int i=2;i<p.Length;i++){t.Append();t.SetValue("HU_NO",p[i]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class SaveHuAssignHandler : HHTBaseHandler {
        readonly bool _qa; public SaveHuAssignHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_CREATE_HU_AND_ASSIGN");var p=P(1).Split(',');f.SetValue("IM_VBELN",p[0]);f.SetValue("IM_USER",p[1]);f.SetValue("IM_EXIDV",p[2]);var t=f.GetTable("IT_DATA");for(int i=3;i+5<p.Length;i+=6){t.Append();t.SetValue("MATNR",p[i]);t.SetValue("CHARG",p[i+1]);t.SetValue("WERKS",p[i+2]);t.SetValue("LGORT",p[i+3]);t.SetValue("TMENG",p[i+4]);t.SetValue("VRKME",p[i+5]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StoreHuValidateHandler : HHTBaseHandler {
        readonly bool _qa; public StoreHuValidateHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_HU_VALIDATE");f.SetValue("IM_PICNR",P(1));f.SetValue("IM_EXIDV",P(2));f.SetValue("IM_WERKS",P(3));f.SetValue("IM_LGNUM",P(4));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StoreBinConPickingHuHandler : HHTBaseHandler {
        readonly bool _qa; public StoreBinConPickingHuHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_BIN_CON_PICKING_HU");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_LGNUM",p[1]);f.SetValue("IM_USER",p[2]);f.SetValue("IM_EXIDV",p[3]);f.SetValue("IM_PICNR",p[4]);var t=f.GetTable("IT_DATA");for(int i=5;i+2<p.Length;i+=3){t.Append();t.SetValue("MATERIAL",p[i]);t.SetValue("BIN",p[i+1]);t.SetValue("SCAN_QTY",p[i+2]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class HuQuanHandler : HHTBaseHandler {
        readonly bool _qa; public HuQuanHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_HU_QUAN");f.SetValue("P_EXIDV",P(1));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StoreGetMajorCatHandler : HHTBaseHandler {
        readonly bool _qa; public StoreGetMajorCatHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GET_MAJOR_CAT");f.SetValue("IM_WERKS",P(1));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+Tbl(f.GetTable("ET_DATA"),"SEG","DIV","SDIV","MCAT","MC");}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StoreGetMajorCatDataHandler : HHTBaseHandler {
        readonly bool _qa; public StoreGetMajorCatDataHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GET_MAJOR_CAT_DATA");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_SEG",P(2));f.SetValue("IM_DIVISION",P(3));f.SetValue("IM_SUB_DIV",P(4));f.SetValue("IM_MAJ_CAT",P(5));f.SetValue("IM_MC",P(6));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+EanData(f.GetTable("ET_EAN_DATA"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class CreateToHandler : HHTBaseHandler {
        readonly bool _qa; public CreateToHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_TO_CREATE_FROM_GR_DATA");var p=P(1).Split(',');f.SetValue("IM_MBLNR",p[0]);f.SetValue("IM_MJAHR",p[1]);f.SetValue("IM_USER",p[2]);var t=f.GetTable("IT_DATA");for(int i=3;i+2<p.Length;i+=3){t.Append();t.SetValue("MATNR",p[i]);t.SetValue("MENGE",p[i+1]);t.SetValue("LGPLA",p[i+2]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ToGetDetailsHandler : HHTBaseHandler {
        readonly bool _qa; public ToGetDetailsHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_TO_GET_DETAILS");f.SetValue("IM_LGNUM",P(1));f.SetValue("IM_TANUM",P(2));f.SetValue("IM_USER",P(3));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+Tbl(f.GetTable("ET_LTAP"),"TANUM","TAPOS","MATNR","VEMNG","LGPLA")+"!"+EanData(f.GetTable("ET_EAN_DATA"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ToScanDataSaveHandler : HHTBaseHandler {
        readonly bool _qa; public ToScanDataSaveHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_TO_SCAN_DATA_SAVE");var p=P(1).Split(',');f.SetValue("IM_LGNUM",p[0]);f.SetValue("IM_TANUM",p[1]);f.SetValue("IM_USER",p[2]);f.SetValue("IM_EXIDV",p[3]);f.SetValue("IM_LGPLA",p[4]);var t=f.GetTable("IT_DATA");for(int i=5;i+5<p.Length;i+=6){t.Append();t.SetValue("EXIDV",p[i]);t.SetValue("VBELN",p[i+1]);t.SetValue("TMENG",p[i+2]);t.SetValue("MATNR",p[i+3]);t.SetValue("WERKS",p[i+4]);t.SetValue("LGORT",p[i+5]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class SaveGrcToDataHandler : HHTBaseHandler {
        readonly bool _qa; public SaveGrcToDataHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_SAVE_GRC_TO_DATA");var p=P(1).Split(',');f.SetValue("IM_USER",p[0]);var t=f.GetTable("IT_DATA");for(int i=1;i+9<p.Length;i+=10){t.Append();t.SetValue("WAREHOUSE",p[i]);t.SetValue("SITE",p[i+1]);t.SetValue("SLOC",p[i+2]);t.SetValue("CRATE",p[i+3]);t.SetValue("BIN_TYPE",p[i+4]);t.SetValue("BIN",p[i+5]);t.SetValue("MATERIAL",p[i+6]);t.SetValue("SCAN_QTY",p[i+7]);t.SetValue("KEY",p[i+8]);t.SetValue("SOURCE_BIN",p[i+9]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class Store0001StockTakeHandler : HHTBaseHandler {
        readonly bool _qa; public Store0001StockTakeHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_0001_STOCK_TAKE");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_LGNUM",p[1]);f.SetValue("IM_USER",p[2]);f.SetValue("IM_STOCK_TAKE","X");var t=f.GetTable("IT_DATA");for(int i=3;i+2<p.Length;i+=3){t.Append();t.SetValue("MATNR",p[i]);t.SetValue("VEMNG",p[i+1]);t.SetValue("LGPLA",p[i+2]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class Store0001ReverseStockHandler : HHTBaseHandler {
        readonly bool _qa; public Store0001ReverseStockHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_0001_REVERSE_STOCK");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_LGNUM",p[1]);f.SetValue("IM_USER",p[2]);f.SetValue("IM_STOCK_TAKE","X");var t=f.GetTable("IT_DATA");for(int i=3;i+2<p.Length;i+=3){t.Append();t.SetValue("MATNR",p[i]);t.SetValue("VEMNG",p[i+1]);t.SetValue("LGPLA",p[i+2]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StoreTrf0001To0010Handler : HHTBaseHandler {
        readonly bool _qa; public StoreTrf0001To0010Handler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_TRF_0001_TO_0010");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_LGNUM",p[1]);f.SetValue("IM_USER",p[2]);var t=f.GetTable("IT_DATA");for(int i=3;i+11<p.Length;i+=12){t.Append();t.SetValue("WM_NO",p[i]);t.SetValue("MATERIAL",p[i+1]);t.SetValue("PLANT",p[i+2]);t.SetValue("STOR_LOC",p[i+3]);t.SetValue("BIN",p[i+4]);t.SetValue("STORAGE_TYPE",p[i+5]);t.SetValue("MEINS",p[i+6]);t.SetValue("AVL_STOCK",p[i+7]);t.SetValue("OPEN_STOCK",p[i+8]);t.SetValue("SCAN_QTY",p[i+9]);t.SetValue("PICK_QTY",p[i+10]);t.SetValue("BARCODE",p[i+11]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StoreTransferBinToBinHandler : HHTBaseHandler {
        readonly bool _qa; public StoreTransferBinToBinHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_TRANSFER_BIN_TO_BIN");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_NLPLA",p[1]);f.SetValue("IM_LGNUM","SDC");f.SetValue("IM_LGORT","0002");f.SetValue("IM_USER",p[2]);var t=f.GetTable("IT_DATA");for(int i=3;i+2<p.Length;i+=3){t.Append();t.SetValue("MATERIAL",p[i]);t.SetValue("SCAN_QTY",p[i+1]);t.SetValue("BIN",p[i+2]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StoreSlocToSlocHandler : HHTBaseHandler {
        readonly bool _qa; public StoreSlocToSlocHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_TRANSFER_SLOC_TO_SLO");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_LGORT_SRC",p[1]);f.SetValue("IM_LGORT_DEST",p[2]);f.SetValue("IM_LGNUM","0002");f.SetValue("IM_USER",p[3]);var t=f.GetTable("IT_DATA");for(int i=4;i+1<p.Length;i+=2){t.Append();t.SetValue("MATERIAL",p[i]);t.SetValue("SCAN_QTY",p[i+1]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class GetV01001sStockHandler : HHTBaseHandler {
        readonly bool _qa; public GetV01001sStockHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GET_V01_001S_STOCK");f.SetValue("IM_WERKS",P(1));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+Tbl(f.GetTable("ET_DATA"),"WM_NO","MATERIAL","PLANT","STOR_LOC","BIN","STORAGE_TYPE","MEINS","AVL_STOCK","OPEN_STOCK","SCAN_QTY","PICK_QTY")+"!"+EanData(f.GetTable("ET_EAN_DATA"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class GetV01001sPostHandler : HHTBaseHandler {
        readonly bool _qa; public GetV01001sPostHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GET_V01_001S_POST");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_LGNUM",p[1]);f.SetValue("IM_USER",p[2]);var t=f.GetTable("IT_DATA");for(int i=3;i+10<p.Length;i+=11){t.Append();t.SetValue("WM_NO",p[i]);t.SetValue("MATERIAL",p[i+1]);t.SetValue("PLANT",p[i+2]);t.SetValue("STOR_LOC",p[i+3]);t.SetValue("BIN",p[i+4]);t.SetValue("STORAGE_TYPE",p[i+5]);t.SetValue("MEINS",p[i+6]);t.SetValue("AVL_STOCK",p[i+7]);t.SetValue("OPEN_STOCK",p[i+8]);t.SetValue("SCAN_QTY",p[i+9]);t.SetValue("PICK_QTY",p[i+10]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StoreGetMatFromEanHandler : HHTBaseHandler {
        readonly bool _qa; public StoreGetMatFromEanHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GET_MAT_FROM_EAN");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_EAN",P(2));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+EanData(f.GetTable("ET_EAN_DATA"))+"!"+Tbl(f.GetTable("ET_LQUA"),"LGPLA","MATNR","VEMNG");}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ValidateStoreEanHandler : HHTBaseHandler {
        readonly bool _qa; public ValidateStoreEanHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_STORE_EAN_DATA");f.SetValue("IM_EAN11",P(1));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+EanData(f.GetTable("ET_EAN_DATA"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ValidateStoreEanV2Handler : HHTBaseHandler {
        readonly bool _qa; public ValidateStoreEanV2Handler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_GET_MAT_FROM_EAN_V2");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_EAN",P(2));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+EanData(f.GetTable("ET_EAN_DATA"))+"!"+Tbl(f.GetTable("ET_LQUA"),"LGPLA","MATNR","VEMNG");}catch(System.Exception e){return Err(e.Message);}}
    }
    public class AppArticleDetailsHandler : HHTBaseHandler {
        readonly bool _qa; public AppArticleDetailsHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_APP_ARTICLE_DETAILS");f.SetValue("IM_EAN",P(1));f.SetValue("IM_WERKS",P(2));f.SetValue("IM_LGNUM",P(3));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class GetPackingMaterialHandler : HHTBaseHandler {
        readonly bool _qa; public GetPackingMaterialHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_GET_PACKING_MATERIAL");f.SetValue("IM_LGNUM","V2R");f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+Tbl(f.GetTable("ET_PACK_MAT"),"MATNR","MAKTX");}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StoreStidPostHandler : HHTBaseHandler {
        readonly bool _qa; public StoreStidPostHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_STORE_STID_POST");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_STID",p[1]);f.SetValue("IM_LGPLA",p[2]);f.SetValue("IM_USER",p[3]);var t=f.GetTable("IT_DATA");for(int i=4;i+3<p.Length;i+=4){t.Append();t.SetValue("STOCK_TAKE",p[i]);t.SetValue("BIN",p[i+1]);t.SetValue("MATERIAL",p[i+2]);t.SetValue("SCAN_QTY",p[i+3]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class StoreStidSaveMcHandler : HHTBaseHandler {
        readonly bool _qa; public StoreStidSaveMcHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_STORE_STID_SAVE_MC");var p=P(1).Split(',');f.SetValue("IM_WERKS",p[0]);f.SetValue("IM_STID",p[1]);f.SetValue("IM_USER",p[2]);var t=f.GetTable("IT_DATA");for(int i=3;i+5<p.Length;i+=6){t.Append();t.SetValue("STOCK_TAKE",p[i]);t.SetValue("BIN",p[i+1]);t.SetValue("MATERIAL",p[i+2]);t.SetValue("SCAN_QTY",p[i+3]);t.SetValue("LOCATION",p[i+4]);t.SetValue("SITE",p[i+5]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ValidateStockTakeIdHandler : HHTBaseHandler {
        readonly bool _qa; public ValidateStockTakeIdHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_STORE_VALDIATE_STID");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_STID",P(2));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+Tbl(f.GetTable("IT_BIN"),"BIN","LGPLA");}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ValidateGandolaMcHandler : HHTBaseHandler {
        readonly bool _qa; public ValidateGandolaMcHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_STORE_VALDIATE_GANDOLA");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_GANDOLA",P(2));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class GetEanStidMcHandler : HHTBaseHandler {
        readonly bool _qa; public GetEanStidMcHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_GET_EAN_STID_MC");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_STID",P(2));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+EanData(f.GetTable("ET_EAN_DATA"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class DiscountGetEanDataHandler : HHTBaseHandler {
        readonly bool _qa; public DiscountGetEanDataHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZSTORE_DISCOUNT_GET_EAN_DATA");f.SetValue("IM_EAN",P(1));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+Tbl(f.GetTable("ET_DISCOUNT_DATA"),"MATNR","DISCOUNT","VALID_FROM","VALID_TO")+"!"+EanData(f.GetTable("ET_EAN_DATA"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class DiscountSaveEanDataHandler : HHTBaseHandler {
        readonly bool _qa; public DiscountSaveEanDataHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZSTORE_DISCOUNT_SAVE_EAN_DATA");var p=P(1).Split(',');f.SetValue("IM_USER",p[0]);f.SetValue("WERKS",p[1]);f.SetValue("EAN11",p[2]);f.SetValue("SQNTY",p[3]);f.SetValue("MATNR",p[4]);var t=f.GetTable("IT_DATA");for(int i=5;i+3<p.Length;i+=4){t.Append();t.SetValue("WERKS",p[i]);t.SetValue("EAN11",p[i+1]);t.SetValue("SQNTY",p[i+2]);t.SetValue("MATNR",p[i+3]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class DiscountStoreValiHandler : HHTBaseHandler {
        readonly bool _qa; public DiscountStoreValiHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZSTORE_DISCOUNT_STORE_VALI");f.SetValue("IM_WERKS",P(1));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class PushDataToSap1StockHandler : HHTBaseHandler {
        readonly bool _qa; public PushDataToSap1StockHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_PUSHDATATOSAP_1STOCK");var p=P(1).Split(',');f.SetValue("IM_USER",p[0]);f.SetValue("EMP_CODE",p[1]);f.SetValue("SITE",p[2]);f.SetValue("GANDOLA",p[3]);f.SetValue("ARTICLE",p[4]);f.SetValue("QUANTITY",p[5]);f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class PushDataToSap1DisHandler : HHTBaseHandler {
        readonly bool _qa; public PushDataToSap1DisHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_PUSHDATATOSAP_1DIS");var p=P(1).Split(',');f.SetValue("IM_USER",p[0]);f.SetValue("IM_NATURE",p[1]);f.SetValue("EMP_CODE",p[2]);f.SetValue("SITE",p[3]);f.SetValue("GANDOLA",p[4]);f.SetValue("ARTICLE",p[5]);f.SetValue("QUANTITY",p[6]);f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class PushDataToSap1TotalHandler : HHTBaseHandler {
        readonly bool _qa; public PushDataToSap1TotalHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_STORE_PUSHDATATOSAP_1TOTAL");var p=P(1).Split(',');f.SetValue("IM_USER",p[0]);f.SetValue("EMP_CODE",p[1]);f.SetValue("SITE",p[2]);f.SetValue("GANDOLA",p[3]);f.SetValue("QUANTITY",p[4]);f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class SdcPut31Handler : HHTBaseHandler {
        readonly bool _qa; public SdcPut31Handler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZRFC_SDC_PUT31");f.SetValue("IM_SITE",P(1));f.SetValue("IM_HU",P(2));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+EanData(f.GetTable("ET_EAN_DATA"))+"!"+Tbl(f.GetTable("ET_FINAL"),"LGPLA","MATNR","VEMNG")+"!"+Tbl(f.GetTable("ET_LQUA"),"LGPLA","MATNR","VEMNG");}catch(System.Exception e){return Err(e.Message);}}
    }
    public class SdcPut31BinValHandler : HHTBaseHandler {
        readonly bool _qa; public SdcPut31BinValHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZRFC_SDC_PUT31_BIN_VALIDATION");f.SetValue("IM_SITE",P(1));f.SetValue("IM_LGPLA",P(2));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class HuPut31SaveHandler : HHTBaseHandler {
        readonly bool _qa; public HuPut31SaveHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_HUPUT31_SAVE");var p=P(1).Split(',');f.SetValue("IM_COMPLETE_FLAG","X");f.SetValue("IM_PICNR",p[0]);var t=f.GetTable("IT_HUSAVE");for(int i=1;i+9<p.Length;i+=10){t.Append();t.SetValue("HU",p[i]);t.SetValue("ITEM_NO",p[i+1]);t.SetValue("ARTICLE",p[i+2]);t.SetValue("PLANT",p[i+3]);t.SetValue("STGE_LOC",p[i+4]);t.SetValue("SCAN_QTY",p[i+5]);t.SetValue("REM_QTY",p[i+6]);t.SetValue("BIN",p[i+7]);t.SetValue("LGNUM",p[i+8]);}f.Invoke(d);return OkOrErr(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class GetStoDataHandler : HHTBaseHandler {
        readonly bool _qa; public GetStoDataHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_GET_STO_DATA");f.SetValue("IM_EXIDV",P(1));f.SetValue("IM_STO",P(2));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+Tbl(f.GetTable("ET_DATA"),"MATNR","VEMNG")+"!"+EanData(f.GetTable("ET_EAN_DATA"))+"!"+Tbl(f.GetTable("ET_LAGP_DATA"),"LGPLA");}catch(System.Exception e){return Err(e.Message);}}
    }
    public class GetGrcBinsHandler : HHTBaseHandler {
        readonly bool _qa; public GetGrcBinsHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_GET_GRC_BINS");f.SetValue("IM_MBLNR",P(1));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+Tbl(f.GetTable("ET_BINS"),"LGPLA")+"!"+Tbl(f.GetTable("ET_CRATE"),"EXIDV");}catch(System.Exception e){return Err(e.Message);}}
    }
    public class ValidateDcSlocHandler : HHTBaseHandler {
        readonly bool _qa; public ValidateDcSlocHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_VALIDATE_DC_SLOC");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_LGORT",P(2));f.SetValue("IM_V11",P(3));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class RfcStoreEanDataStkHandler : HHTBaseHandler {
        readonly bool _qa; public RfcStoreEanDataStkHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_STORE_EAN_DATA_STK");f.SetValue("IM_EAN11",P(1));f.SetValue("IM_WERKS",P(2));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+EanData(f.GetTable("ET_EAN_DATA"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class RfcValidateCrateHandler : HHTBaseHandler {
        readonly bool _qa; public RfcValidateCrateHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_RFC_VALIDATE_CRATE");f.SetValue("IM_WERKS",P(1));f.SetValue("IM_CRATE",P(2));f.Invoke(d);return TypeMsg(f.GetStructure("EX_RETURN"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class GetGrDetailsHandler : HHTBaseHandler {
        readonly bool _qa; public GetGrDetailsHandler(bool qa){_qa=qa;}
        public override string Execute(){try{var d=_qa?QA():Prod();var f=d.Repository.CreateFunction("ZWM_GR_GET_DETAILS");f.SetValue("IM_MBLNR",P(1));f.SetValue("IM_MJAHR",P(2));f.SetValue("IM_USER",P(3));f.Invoke(d);var r=f.GetStructure("EX_RETURN");if(r.GetString("TYPE")=="E")return Err(r.GetString("MESSAGE"));return"S#"+Tbl(f.GetTable("ET_MSEG_DATA"),"MATNR","MENGE","LGPLA")+"!"+EanData(f.GetTable("ET_EAN_DATA"));}catch(System.Exception e){return Err(e.Message);}}
    }
    public class CreateToFromGrDataHandler : CreateToHandler { public CreateToFromGrDataHandler(bool qa):base(qa){} }
}
