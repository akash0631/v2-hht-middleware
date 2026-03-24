using System;
using System.Collections.Generic;
using V2HHTMiddleware.Controllers.HHT.Handlers.Auth;
using V2HHTMiddleware.Controllers.HHT.Handlers.Store;
using V2HHTMiddleware.Controllers.HHT.Handlers.DC;

namespace V2HHTMiddleware.Controllers.HHT
{
    /// <summary>
    /// Maps every HHT opcode string to its handler.
    /// To add a new opcode: create handler class, add one line here.
    /// </summary>
    public static class HHTRouter
    {
        // Factory functions — new instance per request (handlers are not thread-safe)
        private static readonly Dictionary<string, Func<bool, HHTBaseHandler>> _map =
            new Dictionary<string, Func<bool, HHTBaseHandler>>(StringComparer.OrdinalIgnoreCase)
        {
            // ── AUTH ────────────────────────────────────────────────────────────
            { "scnrec",                      qa => new ScnrecHandler(qa) },

            // ── STORE: STOCK ────────────────────────────────────────────────────
            { "getstorestock",               qa => new GetStoreStockHandler(qa) },
            { "getstorestocktake",           qa => new GetStoreStockTakeHandler(qa) },
            { "zwm_store_get_grtstock",      qa => new GetGrtStockHandler(qa) },

            // ── STORE: BINS ─────────────────────────────────────────────────────
            { "storegetbin",                 qa => new StoreGetBinHandler(qa) },
            { "zwm_store_get_bin",           qa => new StoreGetBinHandler(qa) },
            { "storegetbin_v2",              qa => new StoreGetBinV2Handler(qa) },
            { "zwm_store_get_bin_v2",        qa => new StoreGetBinV2Handler(qa) },
            { "storegetbinstock",            qa => new StoreGetBinStockHandler(qa) },
            { "getmatbinstock",              qa => new GetMatBinStockHandler(qa) },
            { "getmatbinstockbtob",          qa => new GetMatBinStockBtoBHandler(qa) },
            { "validatebin",                 qa => new ValidateBinHandler(qa) },
            { "zwm_store_bin_list_validation",   qa => new StoreBinListValidationHandler(qa) },
            { "zwm_store_binconhu_get_details",  qa => new StoreBinConHuGetDetailsHandler(qa) },
            { "zwm_save_empty_bin",          qa => new SaveEmptyBinHandler(qa) },
            { "zwm_validate_empty_bin",      qa => new ValidateEmptyBinHandler(qa) },
            { "zwm_vali_crate_emptybin",     qa => new ValiCrateEmptyBinHandler(qa) },

            // ── STORE: SLOC ─────────────────────────────────────────────────────
            { "validatesloc",                qa => new ValidateSlocHandler(qa) },
            { "getsloc",                     qa => new GetSlocHandler(qa) },

            // ── STORE: PICKLIST / PICKING ───────────────────────────────────────
            { "getstorepicklist",            qa => new GetStorePicklistHandler(qa) },
            { "getstorepicklist_v2",         qa => new GetStorePicklistV2Handler(qa) },
            { "savedirectpicking",           qa => new SaveDirectPickingHandler(qa) },
            { "savedirectpicking_v2",        qa => new SaveDirectPickingV2Handler(qa) },
            { "zwm_picklist_nos_disp",       qa => new PicklistNosDispHandler(qa) },
            { "zhhtusr_del_picking_rfc",     qa => new ZhhtusrDelPickingHandler(qa) },

            // ── STORE: PUTAWAY ──────────────────────────────────────────────────
            { "savegrcputway",               qa => new SaveGrcPutawayHandler(qa) },
            { "savefloorputway",             qa => new SaveFloorPutawayHandler(qa) },
            { "savefloorputwaytake",         qa => new SaveFloorPutawayTakeHandler(qa) },
            { "zwm_floor_puaway_new",        qa => new FloorPutawayNewHandler(qa) },
            { "zwm_store_floor_putway_hu",   qa => new StoreFloorPutawayHuHandler(qa) },
            { "zwm_store_hu_putway_bin_con", qa => new StoreHuPutawayBinConHandler(qa) },

            // ── STORE: GRT ──────────────────────────────────────────────────────
            { "savegrtmsa",                  qa => new SaveGrtFromMsaHandler(qa) },
            { "savegrtfromdisplay",          qa => new SaveGrtFromDisplayHandler(qa) },
            { "zwm_grt_save",               qa => new GrtSaveHandler(qa) },
            { "zwm_grt_putway_crate_validation", qa => new GrtPutwayCrateValHandler(qa) },
            { "zwm_grt_putway_post",         qa => new GrtPutawayPostHandler(qa) },

            // ── STORE: HU ───────────────────────────────────────────────────────
            { "hugetdetails",                qa => new HuGetDetailsHandler(qa) },
            { "hudetails",                   qa => new HuGetDetailsHandler(qa) },
            { "gethus",                      qa => new GetHusHandler(qa) },
            { "savehus",                     qa => new SaveHusHandler(qa) },
            { "savehuassign",                qa => new SaveHuAssignHandler(qa) },
            { "savehudetails",               qa => new SaveHusHandler(qa) },
            { "zwm_store_hu_validate",       qa => new StoreHuValidateHandler(qa) },
            { "zwm_store_bin_con_picking_hu",qa => new StoreBinConPickingHuHandler(qa) },
            { "zwm_hu_quan",                 qa => new HuQuanHandler(qa) },
            { "zwm_store_get_major_cat",     qa => new StoreGetMajorCatHandler(qa) },
            { "zwm_store_get_major_cat_data",qa => new StoreGetMajorCatDataHandler(qa) },

            // ── STORE: TO ───────────────────────────────────────────────────────
            { "createto",                    qa => new CreateToHandler(qa) },
            { "zwm_to_get_details",          qa => new ToGetDetailsHandler(qa) },
            { "zwm_to_scan_data_save",       qa => new ToScanDataSaveHandler(qa) },
            { "zwm_save_grc_to_data",        qa => new SaveGrcToDataHandler(qa) },
            { "zwm_store_0001_stock_take",   qa => new Store0001StockTakeHandler(qa) },
            { "store_0001_stock_take",       qa => new Store0001StockTakeHandler(qa) },
            { "zwm_store_0001_reverse_stock",qa => new Store0001ReverseStockHandler(qa) },
            { "zwm_store_trf_0001_to_0010",  qa => new StoreTrf0001To0010Handler(qa) },
            { "store_trf_0001_to_0010",      qa => new StoreTrf0001To0010Handler(qa) },
            { "zwm_store_transfer_bin_to_bin",qa => new StoreTransferBinToBinHandler(qa) },
            { "savebtob",                    qa => new StoreTransferBinToBinHandler(qa) },
            { "savesloctoslocwwm",           qa => new StoreSlocToSlocHandler(qa) },
            { "get_v01_001s_stock",          qa => new GetV01001sStockHandler(qa) },
            { "get_v01_001s_post",           qa => new GetV01001sPostHandler(qa) },

            // ── STORE: EAN / ARTICLE ────────────────────────────────────────────
            { "store_get_mat_from_ean",      qa => new StoreGetMatFromEanHandler(qa) },
            { "zwm_store_get_mat_from_ean",  qa => new StoreGetMatFromEanHandler(qa) },
            { "validatestoreean",            qa => new ValidateStoreEanHandler(qa) },
            { "validatestoreean_v2",         qa => new ValidateStoreEanV2Handler(qa) },
            { "articledetails",              qa => new AppArticleDetailsHandler(qa) },
            { "packgingmaterial",            qa => new GetPackingMaterialHandler(qa) },

            // ── STORE: STID ─────────────────────────────────────────────────────
            { "storestidpost",               qa => new StoreStidPostHandler(qa) },
            { "storestidpost_mc",            qa => new StoreStidSaveMcHandler(qa) },
            { "validatestablestocktakeid",   qa => new ValidateStockTakeIdHandler(qa) },
            { "validatestablestocktakeid_mc",qa => new ValidateStockTakeIdHandler(qa) },
            { "validategandola_mc",          qa => new ValidateGandolaMcHandler(qa) },
            { "zwm_rfc_get_ean_stid_mc",     qa => new GetEanStidMcHandler(qa) },

            // ── STORE: DISCOUNT ─────────────────────────────────────────────────
            { "zstore_discount_get_ean_data", qa => new DiscountGetEanDataHandler(qa) },
            { "zstore_discount_save_ean_data",qa => new DiscountSaveEanDataHandler(qa) },
            { "zstore_discount_store_vali",   qa => new DiscountStoreValiHandler(qa) },

            // ── STORE: PUSH DATA ────────────────────────────────────────────────
            { "pushdatatosap01stock",         qa => new PushDataToSap1StockHandler(qa) },
            { "zhwm_store_pushdatasap_1stock",qa => new PushDataToSap1StockHandler(qa) },
            { "zwm_store_pushdatatosap_1dis", qa => new PushDataToSap1DisHandler(qa) },
            { "zwm_store_pushdatatosap_1total",qa => new PushDataToSap1TotalHandler(qa) },

            // ── STORE: SDC PUT31 ────────────────────────────────────────────────
            { "zrfc_sdc_put31",              qa => new SdcPut31Handler(qa) },
            { "zrfc_sdc_put31_bin_validation",qa => new SdcPut31BinValHandler(qa) },
            { "zwm_huput31_save",            qa => new HuPut31SaveHandler(qa) },

            // ── STORE: MISC ─────────────────────────────────────────────────────
            { "zwm_get_sto_data",            qa => new GetStoDataHandler(qa) },
            { "zwm_get_grc_bins",            qa => new GetGrcBinsHandler(qa) },
            { "zwm_validate_dc_sloc",        qa => new ValidateDcSlocHandler(qa) },
            { "zwm_rfc_store_ean_data_stk",  qa => new RfcStoreEanDataStkHandler(qa) },
            { "zwm_rfc_validate_crate",      qa => new RfcValidateCrateHandler(qa) },
            { "getgrdetails",                qa => new GetGrDetailsHandler(qa) },
            { "zwm_to_create_from_gr_data",  qa => new CreateToFromGrDataHandler(qa) },

            // ── DC: NIT ─────────────────────────────────────────────────────────
            { "nitrec",                      qa => new NitRecHandler(qa) },
            { "nitdel",                      qa => new NitDelHandler(qa) },
            { "nitupd",                      qa => new NitUpdHandler(qa) },

            // ── DC: DELIVERY ────────────────────────────────────────────────────
            { "scndelivery",                 qa => new ScnDeliveryHandler(qa) },
            { "scnsel",                      qa => new ScnSelHandler(qa) },
            { "disrec",                      qa => new DisRecHandler(qa) },

            // ── DC: STOCK TAKE ──────────────────────────────────────────────────
            { "stocktakegetdetails",         qa => new StockTakeGetDetailsHandler(qa) },
            { "stocktakesavedata",           qa => new StockTakeSaveDataHandler(qa) },
            { "stockvalidatebarcode",        qa => new StockValidateBarcodeHandler(qa) },
            { "zwm_rfc_stock_take_arti_vali",qa => new StockTakeArtiValiHandler(qa) },
            { "zwm_rfc_stock_take_bin_vali", qa => new StockTakeBinValiHandler(qa) },
            { "zwm_rfc_stock_take_crate_vali",qa => new StockTakeCrateValiHandler(qa) },
            { "zwm_rfc_stock_take_save_v11", qa => new StockTakeSaveV11Handler(qa) },
            { "zwm_rfc_stock_validate_v21",  qa => new StockValidateV21Handler(qa) },
            { "zwm_rfc_stock_movement_v21",  qa => new StockMovementV21Handler(qa) },

            // ── DC: HU GRT ──────────────────────────────────────────────────────
            { "zwm_dc_hu_grt_val",           qa => new DcHuGrtValHandler(qa) },
            { "zwm_dc_hugrt_binhu_val",      qa => new DcHuGrtBinHuValHandler(qa) },
            { "zwm_dc_hugrt_hu_val",         qa => new DcHuGrtHuValHandler(qa) },
            { "zwm_dc_hugrt_save",           qa => new DcHuGrtSaveHandler(qa) },

            // ── DC: CLA ─────────────────────────────────────────────────────────
            { "zwm_cla_bin_validate",        qa => new ClaBinValidateHandler(qa) },
            { "zwm_cla_hu_validate",         qa => new ClaHuValidateHandler(qa) },
            { "zwm_cla_palette_validate",    qa => new ClaPaletteValidateHandler(qa) },
            { "zwm_cla_hu_palette_save",     qa => new ClaHuPaletteSaveHandler(qa) },
            { "zwm_cla_palette_bin_tag_save",qa => new ClaPaletteBinTagSaveHandler(qa) },

            // ── DC: CRATE ───────────────────────────────────────────────────────
            { "validatecrateto",             qa => new ValidateCrateToHandler(qa) },
            { "savecrate",                   qa => new SaveCrateHandler(qa) },
            { "zwm_validate_external_hu",    qa => new ValidateExternalHuHandler(qa) },
        };

        public static HHTBaseHandler Resolve(string opcode, bool useQa = false)
            => _map.TryGetValue(opcode, out var f) ? f(useQa) : null;

        public static IEnumerable<string> AllOpcodes() => _map.Keys;
    }
}
