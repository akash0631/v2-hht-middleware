using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Newtonsoft.Json;


namespace V2HHTMiddleware.Controllers.HHT
{
    [RoutePrefix("api/hht")]
    public class HHTController : ApiController
    {
        // ── Constants ──────────────────────────────────────────────────────────
        private const string APK_VERSION = "12.106";
        private const string APK_URL     = "https://apk.v2retail.net/download";
        private const string MW_VERSION  = "v2-hht-azure|5.0";

        // Persistent stats file — survives App Service restarts (D:\home is mounted storage)
        private static readonly string STATS_FILE =
            Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? @"D:\home",
                         "data", "hht_opcode_stats.json");

        // ── HTTP client ────────────────────────────────────────────────────────
        private static readonly HttpClient _http;
        private static volatile string _javaBase = null;
        private static readonly object _discoveryLock = new object();

        // ── In-memory ring buffer (last 1000 calls) ────────────────────────────
        private static readonly ConcurrentQueue<CallLog> _ring = new ConcurrentQueue<CallLog>();
        private const int RING_MAX = 1000;

        // ── Plant / Store name lookup ───────────────────────────────────────────
        // Pre-loaded from Supabase store_plant_master_aka (refreshed on startup).
        // Keys = SAP plant code (WERKS), Values = short store name.
        // Regenerate: POST /api/hht/refresh-plants  or redeploy.
        private static readonly Dictionary<string, string> _plantNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
            { "DB01", "PTN-RDC" },
            { "DB03", "PATNA-HUB" },
            { "DB05", "MUZAFFARPUR-HUB" },
            { "DD03", "KAPASHERA PLANT" },
            { "DH21", "MNSR-DC" },
            { "DH24", "FRKH-NGR-RDC" },
            { "DH25", "FRKH-NGR-DC-1" },
            { "DH26", "JMLPR-RDC" },
            { "DH27", "FRK-HUB" },
            { "DH30", "FRKH-NGR-PRD" },
            { "DJ01", "JAMSHEDPUR-HUB" },
            { "DJ02", "DHANBAD-HUB" },
            { "DK01", "BNGLR-DATA-CNTR" },
            { "DK02", "HUBLI-HUB" },
            { "DK10", "BNGLR" },
            { "DM01", "BHOPAL-HUB" },
            { "DM02", "JABALPUR-HUB" },
            { "DN01", "GWHT-RDC" },
            { "DO01", "BBSR-RDC" },
            { "DP01", "JALANDHAR-HUB" },
            { "DR01", "AJMER-HUB" },
            { "DU05", "VARANASI-HUB" },
            { "DU06", "LUCKNOW-HUB" },
            { "DW01", "KOLKATA-RDC" },
            { "DW02", "KOLKATA-HUB" },
            { "DX01", "JAMMU-CITY-HUB" },
            { "HA10", "ITNGR" },
            { "HA11", "PASIGHAT" },
            { "HA12", "NAHARLAGUN" },
            { "HB05", "PTN-1(MAHARAJ A COMPLEX)" },
            { "HB06", "GAYA" },
            { "HB07", "BGPR-1(KACHARI CHOWK)" },
            { "HB08", "DRBG" },
            { "HB09", "BETIA" },
            { "HB10", "ARA" },
            { "HB11", "PURNIA" },
            { "HB12", "CHPR" },
            { "HB13", "SWN" },
            { "HB14", "BGSR" },
            { "HB15", "B-SRF" },
            { "HB16", "STMD" },
            { "HB17", "MTHR" },
            { "HB18", "KSNGNJ" },
            { "HB19", "SMSTPR-1" },
            { "HB20", "BEGUSARAI-3" },
            { "HB21", "SHRSA" },
            { "HB22", "BARH" },
            { "HB23", "LKSR" },
            { "HB24", "BANKA" },
            { "HB25", "BUXAR" },
            { "HB26", "AURNGBD" },
            { "HB27", "MZFPR" },
            { "HB28", "BGSR-2" },
            { "HB29", "PTN-2(SAGUNA MORE)" },
            { "HB30", "GOPALGANJ" },
            { "HB31", "MADHUBANI" },
            { "HB32", "SASARAM" },
            { "HB34", "PTN-3(ANISHA BAD)" },
            { "HB35", "CHPRA" },
            { "HB36", "HJPR" },
            { "HB37", "PTN-4(KURJI)" },
            { "HB38", "BGPR-2(TATARPUR CHOWK)" },
            { "HB39", "DEHRI ON SONE" },
            { "HB40", "BUXAR (CIVIL LINES)" },
            { "HB41", "PTN-5(PHULWARI SHARIF)" },
            { "HB42", "JAMUI" },
            { "HB43", "PTN-6(MATCHUATOLI)" },
            { "HB44", "MUNGER" },
            { "HB45", "PTN-7(PATNA CITY)" },
            { "HB46", "SAMASTIPUR(NR OLD V2)" },
            { "HB47", "JAMALPUR" },
            { "HB48", "JEHANABAD" },
            { "HB49", "PATNA CITY-(SADIKPUR)" },
            { "HB50", "BHABUA" },
            { "HB51", "PTN-8(GOLA ROAD)" },
            { "HB52", "SAMASTIPUR (GC)" },
            { "HB53", "NAWADA" },
            { "HB54", "PATNA(SAHIB)" },
            { "HB55", "KHAGARIA" },
            { "HB56", "MOTIHARI" },
            { "HB57", "SIWAN-2" },
            { "HB58", "JAINAGAR" },
            { "HB59", "MUZAFFARPUR" },
            { "HB60", "MADHEPURA" },
            { "HB61", "PATNA(BHOOTHNATH)" },
            { "HB62", "GAYA-2" },
            { "HB63", "PATNA-4" },
            { "HB64", "DARBHANGA-2" },
            { "HB65", "PATNA (RAJEEV NAGAR)" },
            { "HB66", "KATIHAR" },
            { "HB67", "GOPALGANJ-2" },
            { "HC01", "RAIPUR(COLORS MALL)-(V2)" },
            { "HC02", "AMBIKAPUR" },
            { "HC03", "BILASPUR" },
            { "HC04", "RAIGARH" },
            { "HC05", "JAGDALPUR(BESIDE VMM)" },
            { "HC06", "DHAMTARI" },
            { "HD16", "MHPL" },
            { "HD17", "KRB" },
            { "HD18", "RJR-GDN" },
            { "HD19", "K-NGR" },
            { "HD21", "NJF" },
            { "HD22", "KPSHR" },
            { "HD23", "KPSHR-2" },
            { "HD24", "BHAJANPURA" },
            { "HD25", "RANIBAGH" },
            { "HD26", "MHPL-2(VASANT KUNJ ROAD)" },
            { "HD27", "NARELA" },
            { "HD28", "MAHAVIR ENCLAVE" },
            { "HD29", "NAJAFGARH" },
            { "HD30", "KIRARI(APNA BAZAR)" },
            { "HD31", "BURARI" },
            { "HD32", "BUDH VIHAR" },
            { "HD33", "KHICHRIPUR" },
            { "HD34", "UTTAM-NAGAR" },
            { "HE10", "HYD" },
            { "HF01", "NAMCHI" },
            { "HG10", "PNJM" },
            { "HG11", "BCHLM" },
            { "HG12", "MARGAON" },
            { "HG13", "SOUTH GOA(DABOLIM)" },
            { "HH12", "FBD" },
            { "HH13", "FBD-2(NIT MARKET)" },
            { "HH14", "ROHTAK-(TN)" },
            { "HH15", "FATEHABAD" },
            { "HH16", "AMBALA CANTT" },
            { "HH17", "KURUKSHETRA" },
            { "HH18", "SONIPAT" },
            { "HH19", "REWARI" },
            { "HH20", "GURUGRAM SEC-14" },
            { "HH21", "BADSHAHPUR GURGAON" },
            { "HI05", "SLN" },
            { "HI06", "KANGRA" },
            { "HI07", "UNA" },
            { "HJ08", "JMD-1(SAKCHI)" },
            { "HJ09", "RNCH-1(MAIN ROAD)" },
            { "HJ10", "HZRBG" },
            { "HJ11", "DGHR" },
            { "HJ12", "R-DHWR" },
            { "HJ14", "DUMKA" },
            { "HJ15", "CHAS" },
            { "HJ16", "DLTNGNJ" },
            { "HJ17", "JMD-2(ADITYAPUR)" },
            { "HJ19", "RNCH-2(BHUTALA MALL)" },
            { "HJ20", "JMD-3(MANGO CHOWK)" },
            { "HJ21", "DHANBAD" },
            { "HJ22", "JMD-BISTUPUR" },
            { "HJ23", "CHAKRADHARPUR" },
            { "HJ24", "LOHARDAGA" },
            { "HJ25", "GAMAHRIA" },
            { "HJ26", "DHANBAD(SUSNILEWA)" },
            { "HJ27", "RAMGARH" },
            { "HJ28", "RANCHI(PISCA)" },
            { "HJ29", "GIRIDIH (JAMUA RD)" },
            { "HJ30", "GODDA(V MART BESIDE)" },
            { "HJ31", "DHANBAD (GOVINDPUR)" },
            { "HK04", "HBL-1(AKSHAY PARK)" },
            { "HK05", "BELAGAVI-1(KOLAPUR CIRCLE)" },
            { "HK06", "UDUPI" },
            { "HK07", "VJPR" },
            { "HK09", "DVNGR" },
            { "HK10", "GLBRG" },
            { "HK11", "DHRWD" },
            { "HK12", "BELAGAVI-2(RPD ROAD)" },
            { "HK13", "MYSURU" },
            { "HK14", "JAMKHANDI" },
            { "HK15", "RAICHUR" },
            { "HK16", "HBL-2(PADMA TALKIES)" },
            { "HK17", "LEGACY MALL" },
            { "HK18", "HAVERI-(V2)" },
            { "HK19", "BAGALKOT" },
            { "HK20", "KOPPAL" },
            { "HK21", "TC PALYA" },
            { "HK22", "MYSORE-2" },
            { "HK23", "BALLARY-INFANTRY-RD" },
            { "HK24", "CHIKKAMAGALURU" },
            { "HK25", "UTTARAHALLI" },
            { "HK26", "BANGALORE (CHANDAPURA)" },
            { "HK27", "BELGAUM-3" },
            { "HK28", "BIDAR" },
            { "HK29", "BEGUR- KOPPA ROAD" },
            { "HK30", "MANDYA" },
            { "HL01", "ANAND (GUJARAT)" },
            { "HL02", "AATMAN-SURAT" },
            { "HL03", "ANANAD(RAJPATH MARG)" },
            { "HL04", "NADIAD" },
            { "HL05", "PATAN" },
            { "HL06", "KSB TRIDENT SURAT" },
            { "HL07", "PALANPUR" },
            { "HM20", "SGR" },
            { "HM21", "JBLPR" },
            { "HM22", "REWA" },
            { "HM23", "HARDA" },
            { "HM24", "BHOPAL (KOLAR ROAD)" },
            { "HM25", "BHOPAL-2(KAROND ROAD)" },
            { "HM26", "BURHANPUR" },
            { "HM27", "UJJAIN" },
            { "HM28", "CHHINDWARA" },
            { "HM29", "HOSHANGABAD" },
            { "HM30", "KATNI (GOLE BAZAR)" },
            { "HM31", "GWALIOR" },
            { "HM32", "NARSINGHPUR-(BR)-(V2)" },
            { "HM33", "BETUL" },
            { "HM34", "BHOPAL (MANDIDEEP)" },
            { "HM35", "BHOPAL (SERVICE ROAD)" },
            { "HM36", "KHARGONE" },
            { "HM37", "DEWAS" },
            { "HM38", "SEHORE(New Bus Stand)" },
            { "HM39", "SAGAR(CIVIL LINES)" },
            { "HM40", "BHOPAL" },
            { "HM41", "JABALPUR-2" },
            { "HM42", "AMOUDHA-SATNA" },
            { "HM43", "KHANDWA" },
            { "HM44", "UJJAIN-2" },
            { "HM45", "RATLAM" },
            { "HM46", "SINGRAULI (WAIDHAN)" },
            { "HM47", "BHOPAL(RAISEN ROAD)" },
            { "HM48", "GWALIOR 2" },
            { "HM49", "INDORE (MANGAL CITY)" },
            { "HM50", "GUNA" },
            { "HM51", "ITARSI" },
            { "HM52", "RAJGARH" },
            { "HN10", "GWHT -1(PALTAN BAZAR)" },
            { "HN11", "SLCHR-1(TULA-PATTY)" },
            { "HN12", "GW-2-GNSPR" },
            { "HN13", "BRPT-1(BARPETA ROAD)" },
            { "HN14", "JRHT" },
            { "HN15", "GWHT-3(ADABARI)" },
            { "HN21", "AGTLA" },
            { "HN22", "SLCHR-2(UKIL-PATTY)" },
            { "HN23", "SHILLONG" },
            { "HN25", "BRPT-2(BARPETA TOWN)" },
            { "HN26", "TEZPUR" },
            { "HN27", "TINSUKIA" },
            { "HN28", "NALBARI" },
            { "HN29", "DIPHU" },
            { "HN30", "BONGAIGAON" },
            { "HN31", "KARIMGANJ" },
            { "HN32", "KOKRAJHAR" },
            { "HN33", "DIBRUGARH" },
            { "HN34", "DHUBRI(BORO BAZAR)" },
            { "HN35", "HOJAI" },
            { "HN36", "NAGAON" },
            { "HN37", "GUWAHATI (KALAPAHAR)" },
            { "HN38", "KOKRAJHAR" },
            { "HN39", "BISWANATH CHARALI" },
            { "HN40", "GOALPARA" },
            { "HN41", "VARANASI-2" },
            { "HN42", "NORTH LAKHIMPUR" },
            { "HN43", "IMPHAL-PCTC-MALL" },
            { "HN44", "GOLAGHAT TOWN" },
            { "HN45", "SIVASAGAR" },
            { "HN46", "MANGALDAI" },
            { "HN47", "MORIGOAN" },
            { "HN48", "UDALGURI" },
            { "HN49", "HAILAKANDI" },
            { "HN50", "SONARI" },
            { "HN60", "DIMAPUR" },
            { "HN61", "KOHIMA" },
            { "HN62", "CHUMOUKEDIMA" },
            { "HN80", "KANCHIPUR IMPHAL(MANIPUR)" },
            { "HO08", "O-BHRM-1(TELEPHONE BHAWAN)" },
            { "HO09", "CTK-1" },
            { "HO10", "BBSR-1(KRISHNA PLAZA)" },
            { "HO11", "JEYPORE" },
            { "HO12", "BBSR-2(PATRA PADA)" },
            { "HO13", "BBSR-3(PATIA)" },
            { "HO15", "JJPR" },
            { "HO16", "ANGUL" },
            { "HO18", "BLSRE" },
            { "HO19", "BHADRAK" },
            { "HO20", "NMPDA" },
            { "HO21", "SNDRGH" },
            { "HO22", "KHARIAR" },
            { "HO23", "KHORDA" },
            { "HO24", "RRKL" },
            { "HO25", "BRPD-1(DURGA BARI)" },
            { "HO26", "BBSR-4(RASULGARH)" },
            { "HO27", "BHRM-2(BANK ROAD)" },
            { "HO28", "BBSR-5(KALPANA)" },
            { "HO29", "KEONJHAR" },
            { "HO30", "JHARSUGUDA" },
            { "HO31", "SAMBHALPUR" },
            { "HO32", "PARADEEP" },
            { "HO33", "PARALAKHEMUNDI" },
            { "HO34", "SEMLIGUDA" },
            { "HO35", "HAVERI" },
            { "HO36", "NUAPADA" },
            { "HO37", "BOUDH" },
            { "HO38", "BRPD-2(BALESHWAR BUILDING)" },
            { "HO39", "BARGARH" },
            { "HO40", "JHARSUGUDA-2" },
            { "HO41", "NAYAGARH(NEAR SNV)" },
            { "HO42", "CTK-2" },
            { "HO43", "RAYAGADA" },
            { "HO44", "BARGARH-2" },
            { "HO45", "TALCHER" },
            { "HO46", "BALASORE-2" },
            { "HO47", "BHAWANI PATNA (ODISHA)" },
            { "HO48", "SONEPUR (ODISHA)" },
            { "HO49", "BALANGIR (ODISHA)" },
            { "HP01", "RUPNAGAR" },
            { "HP02", "GURDASPUR" },
            { "HP03", "PATHANKOT (OPTION 2)" },
            { "HP04", "LUDHIANA" },
            { "HP05", "ZIRAKPUR" },
            { "HP06", "BATALA" },
            { "HP07", "MOHALI" },
            { "HP08", "DHURI" },
            { "HP09", "KAPURTHALA" },
            { "HP10", "NAWANSHAHR" },
            { "HP11", "HOSHIARPUR" },
            { "HP12", "JALANDHAR" },
            { "HP13", "TARN TARAN" },
            { "HP14", "ABOHAR-FAZILKA" },
            { "HP15", "PATIALA-LEELA BHAWAN" },
            { "HP16", "FATEHGARH" },
            { "HP17", "BATHINDA (AMRIK SINGH ROAD)" },
            { "HP18", "PANCHKULA" },
            { "HP19", "MALERKOTLA" },
            { "HP20", "FIROZPUR" },
            { "HP21", "ALANDHAR-2" },
            { "HP22", "PATIALA" },
            { "HR15", "KOTA" },
            { "HR16", "UDAIPUR" },
            { "HR17", "BHILWARA-AJMER ROAD" },
            { "HR18", "KISHANGARH" },
            { "HR19", "CHITTORGARH" },
            { "HR20", "SRI GANGANAGAR" },
            { "HR21", "KOTA" },
            { "HR22", "JAIPUR (MANSAROVAR)" },
            { "HR23", "SIKAR" },
            { "HR24", "JHUNJHUNU" },
            { "HR25", "BIKANER GOGA GATE" },
            { "HR26", "NAGAUR" },
            { "HR27", "RAJSAMAND" },
            { "HR28", "AJMER" },
            { "HR29", "UDAIPUR-CENTRAL" },
            { "HS01", "GAJUWAKA" },
            { "HS02", "VIZIANAGARAM" },
            { "HS03", "ANAKAPALLI" },
            { "HS04", "VISHAKHAPATNAM" },
            { "HS05", "GUNTUR(ANDHRA PRADESH)" },
            { "HS06", "MADANAPALLI" },
            { "HS07", "ONGOLE" },
            { "HT01", "HANAMKONDA" },
            { "HT12", "HLDN" },
            { "HT13", "HRDWR-1" },
            { "HT14", "RRKE" },
            { "HT15", "HRDWR-2" },
            { "HT16", "KHTM" },
            { "HT17", "HLDN-2(JAIL ROAD CHAURAHA)" },
            { "HT18", "DEHRADUN" },
            { "HT19", "DEHRADUN (SEEMA-DWAR)" },
            { "HT20", "KOTDWAR" },
            { "HT21", "DEHRADUN -JOGGIWALA" },
            { "HT22", "RUDRAPUR" },
            { "HT23", "ALMORA" },
            { "HT24", "CHAMPAWAT" },
            { "HU32", "GK-1-BNKRD-LGF" },
            { "HU33", "VNS" },
            { "HU34", "GKP-2(MEDICAL ROAD)" },
            { "HU35", "AZAM" },
            { "HU36", "JNPR" },
            { "HU37", "BHRCH" },
            { "HU39", "LKNW" },
            { "HU40", "STPR" },
            { "HU41", "VNS-2(LANKA)" },
            { "HU42", "BNGL" },
            { "HU43", "MAU" },
            { "HU44", "GZPR" },
            { "HU45", "PGRH" },
            { "HU46", "LKNW-2" },
            { "HU47", "FRKHBD" },
            { "HU48", "AKBRPR" },
            { "HU49", "BHRCH-2(HOSPITAL ROAD)" },
            { "HU51", "LKNW-3" },
            { "HU53", "FRZBD" },
            { "HU54", "PDRNA" },
            { "HU55", "VNS-3(CHITAIPUR)" },
            { "HU56", "PAHARIYA" },
            { "HU57", "LKNW-4(JNK PRM)" },
            { "HU58", "AZAM-2(MURLI TALKIES)" },
            { "HU59", "BASTI" },
            { "HU60", "BALLIA" },
            { "HU61", "RBRLY" },
            { "HU62", "MIRZAPUR" },
            { "HU63", "GK-3-BNKRD" },
            { "HU64", "BLRMPR" },
            { "HU65", "PRAYAGRAJ-1(KATRA)" },
            { "HU66", "JNPR-2" },
            { "HU67", "LKHMPR" },
            { "HU68", "JHANSI" },
            { "HU69", "LKNW-5(MATIYARI)" },
            { "HU70", "KNPR-1(LAL BANGLA)" },
            { "HU71", "GONDA" },
            { "HU72", "BRLY" },
            { "HU73", "BRLY-2" },
            { "HU74", "BHANGEL" },
            { "HU75", "MRZPR-2" },
            { "HU76", "VNS-4(PAHARIYA)" },
            { "HU77", "VNS-5(MALDAHIYA)" },
            { "HU78", "RENUKOOT" },
            { "HU79", "ALLAHABAD (PRAYAGRAJ)" },
            { "HU80", "LKNW-6(TELI BAG)" },
            { "HU81", "LKNW-7(SITAPUR ROAD)" },
            { "HU82", "KHALILABAD" },
            { "HU83", "LKNW-8(ALAMBAGH)" },
            { "HU84", "GORAKHPUR-4" },
            { "HU85", "ORAI" },
            { "HU86", "SITAPUR" },
            { "HU87", "FARUKHABAD" },
            { "HU88", "HARDOI" },
            { "HU89", "BAREILLY (CIVIL LINES)" },
            { "HU90", "RAMPUR" },
            { "HU91", "BUDAUN" },
            { "HU92", "BAREILLY (GURUDWARA)" },
            { "HU93", "GHAZIABAD (TIGRI)" },
            { "HU94", "DEORIA" },
            { "HU95", "LUCKNOW(KURSI RAOD)" },
            { "HU96", "LUCKNOW (ASHIYANA)" },
            { "HU97", "BIJNOR" },
            { "HU98", "HATHRAS" },
            { "HU99", "AGRA- SIKANDRA (LOHA MANDI)" },
            { "HV01", "DINDOLI" },
            { "HW10", "GRHT" },
            { "HW11", "HTBGN" },
            { "HW12", "VIP ROAD" },
            { "HW13", "MALDAH" },
            { "HW14", "AQTCA" },
            { "HW15", "SILIGURI" },
            { "HW16", "CHINSURAH" },
            { "HW17", "RAMPURHAT" },
            { "HW18", "DURGAPUR" },
            { "HW19", "KHARAGPUR" },
            { "HW20", "ASANSOL-1(MARKET ROAD)" },
            { "HW21", "MIDNAPORE" },
            { "HW22", "BASIRHAT" },
            { "HW23", "NSOL-2" },
            { "HW24", "ASANSOL-2(CENTRUM MALL)" },
            { "HW25", "B-BERHAMPORE" },
            { "HW26", "HABRA" },
            { "HW27", "BASIRHAT" },
            { "HW28", "DANKUNI (NEW)" },
            { "HW29", "BARRACKPORE" },
            { "HW30", "JALPAIGURI" },
            { "HW31", "KANCHRAPARA" },
            { "HW32", "JAIGAON (WEST BENGAL)" },
            { "HW33", "DOMKAL" },
            { "HW34", "NAIHATI" },
            { "HW35", "PURALIA" },
            { "HW36", "CHAKDAHA" },
            { "HW37", "DHUPGURI" },
            { "HX10", "KTH" },
            { "HX11", "JAMMU" },
            { "HX12", "UDHAMPUR" },
            { "HX13", "JANIPUR" },
            { "HX14", "KUNJWANI" },
            { "HX15", "DODA" },
            { "HX16", "ANANTNAG" },
            { "HX17", "RAJOURI" },
            { "HX18", "BUDGAM" },
            { "HX19", "KISHTWAR" },
            { "HX50", "ICHALKARANJI" },
            { "HX51", "OSMANABAD" },
            { "HX52", "BARAMATI" },
            { "HY01", "HANAMKONDA(MULUG ROAD CROSING)" },
            { "HY02", "KARIMNAGAR (AZMATHPURA)" },
            { "RD04", "HO-OLD" },
            { "RH01", "HO-NEW" },
            { "SEDC", "SEDC" },
            { "SEST", "SEST" },
            { "U100", "AGRA- SANJAY PLACE" },
            { "U101", "LONI(INDRAPURI)" },
            { "U102", "BELA PRATAPGARH" },
            { "U103", "NOIDA (OMAXE MALL)" },
            { "U104", "SHAHJAHANPUR" },
            { "U105", "ALIGARH" },
            { "U106", "KASGANJ" },
            { "U107", "ALLAHBAD(NAINI)" },
            { "U108", "PHAPHAMAU" },
            { "U109", "SAHARANPUR" },
            { "U110", "KANPUR AMBEDKARPURAM" },
            { "U111", "MEERUT" },
            { "U112", "MUZAFFARNAGAR" },
            { "U113", "MAINPURI" },
            { "U114", "GORAKHPUR (MEDICAL ROAD)" },
            { "U115", "KHODA" },
            { "U116", "PILLIBHIT" },
            { "U117", "AYODHYA" },
            { "U118", "SIKOHABAD" },
            { "U119", "JHANSI-1" },
            { "U120", "SULTANPUR" },
            { "U121", "JHANSI -2" }
            };

        // ── Response cache — read-only opcodes with 60s TTL ────────────────────
        private sealed class CacheEntry { public string Body; public DateTime Expires; }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry>
            _cache = new System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan CACHE_TTL = TimeSpan.FromSeconds(60);
        // Only cache opcodes that return near-static master/reference data
        private static readonly HashSet<string> CACHEABLE = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "storegetbin", "storegetbin_v2", "zwm_store_get_bin", "zwm_store_get_bin_v2",
            "packgingmaterial", "packingmaterial",
            "zwm_store_get_major_cat", "zwm_store_get_major_cat_data",
            "zwm_get_msa_section_list", "zwm_get_packing_material",
            "getsloc", "validatesloc", "zwm_rfc_validate_dc_sloc",
            "zfms_screen", "zwm_get_grc_bins"
        };
        private static string CacheKey(string opcode, string store) => opcode + "|" + (store ?? "?");
        private static bool TryGetCache(string opcode, string store, out string body)
        {
            body = null;
            if (!CACHEABLE.Contains(opcode)) return false;
            if (_cache.TryGetValue(CacheKey(opcode, store), out var e) && e.Expires > DateTime.UtcNow)
            { body = e.Body; return true; }
            return false;
        }
        private static void SetCache(string opcode, string store, string body)
        {
            if (!CACHEABLE.Contains(opcode) || string.IsNullOrEmpty(body)) return;
            // Don't cache error responses
            if (body.Contains("E#") || body.Length < 10) return;
            _cache[CacheKey(opcode, store)] = new CacheEntry { Body = body, Expires = DateTime.UtcNow.Add(CACHE_TTL) };
            // Evict expired entries periodically (every ~100 cache writes)
            if (_cache.Count > 200)
            {
                var expired = _cache.Where(kv => kv.Value.Expires <= DateTime.UtcNow).Select(kv => kv.Key).ToList();
                foreach (var k in expired) _cache.TryRemove(k, out _);
            }
        }

        // Active device sessions: key=userId, value=last seen info
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DeviceSession> _sessions
            = new System.Collections.Concurrent.ConcurrentDictionary<string, DeviceSession>(StringComparer.OrdinalIgnoreCase);

        // ── Per-opcode stats — loaded from disk on startup, flushed every 60s ──
        private static readonly ConcurrentDictionary<string, OpcodeStats> _opcodeStats
            = new ConcurrentDictionary<string, OpcodeStats>(StringComparer.OrdinalIgnoreCase);

        // All 117 registered opcodes (for "registered vs active" display)
        private static readonly HashSet<string> ALL_OPCODES = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "scnrec","scnsel","storegetbin","zwm_store_get_bin","storegetbin_v2",
            "zwm_store_get_bin_v2","storegetbinstock","getstorestock","getstorestocktake",
            "getmatbinstock","getmatbinstockbtob","validatebin","validatesloc","getsloc",
            "store_get_mat_from_ean","zwm_store_get_mat_from_ean","validatestablestocktakeid",
            "validatestablestocktakeid_mc","validatestoreean","validatestoreean_v2",
            "articledetails","packgingmaterial","zwm_store_get_major_cat",
            "zwm_store_get_major_cat_data","zwm_store_bin_list_validation",
            "zwm_store_binconhu_get_details","zwm_save_empty_bin","zwm_validate_empty_bin",
            "zwm_vali_crate_emptybin","getstorepicklist","getstorepicklist_v2",
            "zwm_picklist_nos_disp","savedirectpicking","savedirectpicking_v2",
            "zhhtusr_del_picking_rfc","zwm_store_bin_con_picking_hu","get_v01_001s_post",
            "get_v01_001s_stock","hugetdetails","hudetails","gethus","savehus",
            "savehuassign","savehudetails","zwm_store_hu_validate","zwm_hu_quan",
            "zwm_validate_external_hu","savegrcputway","savefloorputway","savefloorputwaytake",
            "zwm_floor_puaway_new","zwm_store_floor_putway_hu","zwm_store_hu_putway_bin_con",
            "savegrtmsa","savegrtfromdisplay","zwm_grt_save","zwm_grt_putway_crate_validation",
            "zwm_grt_putway_post","zwm_store_get_grtstock","zwm_rfc_validate_crate",
            "zwm_get_grc_bins","zwm_save_grc_to_data","stocktakegetdetails","stocktakesavedata",
            "stockvalidatebarcode","zwm_rfc_stock_take_bin_vali","zwm_rfc_stock_take_arti_vali",
            "zwm_rfc_stock_take_crate_vali","zwm_rfc_stock_take_save_v11",
            "zwm_store_0001_stock_take","store_0001_stock_take","zwm_store_0001_reverse_stock",
            "zwm_rfc_store_ean_data_stk","zwm_rfc_stock_movement_v21","zwm_rfc_stock_validate_v21",
            "zwm_store_pushdatatosap_1total","zwm_store_pushdatatosap_1dis","pushdatatosap01stock",
            "zhwm_store_pushdatasap_1stock","savebtob","savesloctoslocwwm",
            "zwm_store_transfer_bin_to_bin","zwm_store_trf_0001_to_0010","store_trf_0001_to_0010",
            "storestidpost","storestidpost_mc","validategandola_mc","savecrate","validatecrateto",
            "zstore_discount_store_vali","zstore_discount_get_ean_data","zstore_discount_save_ean_data",
            "nitrec","nitupd","nitdel","disrec","scndelivery","zwm_get_sto_data",
            "zwm_validate_dc_sloc","zwm_dc_hu_grt_val","zwm_dc_hugrt_binhu_val",
            "zwm_dc_hugrt_hu_val","zwm_dc_hugrt_save","getgrdetails","createto",
            "zwm_to_get_details","zwm_to_scan_data_save","zwm_to_create_from_gr_data",
            "zwm_cla_palette_validate","zwm_cla_hu_validate","zwm_cla_bin_validate",
            "zwm_cla_hu_palette_save","zwm_cla_palette_bin_tag_save","zwm_huput31_save",
            "zrfc_sdc_put31","zrfc_sdc_put31_bin_validation","zwm_rfc_get_ean_stid_mc",
            "zwm_rfc_stock_movement_v21","zwm_store_get_grtstock",
            "pushdatatosap01stock","zhwm_store_pushdatasap_1stock"
        };

        private static readonly Timer _flushTimer;
        private static readonly object _fileLock = new object();
        private static bool _statsLoaded = false;

        static HHTController()
        {
            var handler = new HttpClientHandler
            {
                MaxConnectionsPerServer = 300,
                UseProxy = false,
                AllowAutoRedirect = false
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(55) };

            // Load persisted stats from disk immediately
            LoadStatsFromDisk();

            // Flush stats to disk every 60 seconds
            _flushTimer = new Timer(_ => FlushStatsToDisk(), null,
                TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ROUTES
        // ═══════════════════════════════════════════════════════════════════════

        [HttpPost, Route("")]
        public Task<HttpResponseMessage> Handle() => Proxy();

        [HttpPost, Route("ValueXMW")]
        public Task<HttpResponseMessage> ValueXMW() => Proxy();

        [HttpPost, Route("ValueXMW/{app}")]
        public Task<HttpResponseMessage> ValueXMWApp(string app) => Proxy();

        [HttpPost, Route("ValueXMW/{app}/{platform}/{version}")]
        public Task<HttpResponseMessage> ValueXMWFull(string app, string platform, string version) => Proxy();

        [HttpPost, Route("~/ValueXMW/{app}/{platform}/{version}")]
        public Task<HttpResponseMessage> ValueXMWRoot(string app, string platform, string version) => Proxy();


        // ── v12+ app ─────────────────────────────────────────────────────────
        [HttpPost, Route("noacljsonrfcadaptor")]
        public Task<HttpResponseMessage> NoAclJson()    => ProxyNoAcl();
        [HttpGet,  Route("noacljsonrfcadaptor")]
        public Task<HttpResponseMessage> NoAclJsonGet() => ProxyNoAcl();

        // ── index.jsp / ping — v12 IPActivity connectivity check ──────────
        [HttpGet, Route("index.jsp")]
        public HttpResponseMessage IndexJspGet()
        {
            return Json("ok");
        }

        [HttpGet, Route("ping")]
        public HttpResponseMessage Ping()
        {
            return Json("ok");
        }


        // ── MIN version — bump this to force all devices below it to upgrade ──
        private const string MIN_APK_VERSION = "1.0"; // auto-update disabled — re-enable once all devices on new cert
        private static int CmpVer(string a, string b) {
            try {
                var pa=a.Split('.'); var pb=b.Split('.');
                for(int i=0;i<Math.Max(pa.Length,pb.Length);i++){
                    int va=i<pa.Length?int.Parse(pa[i]):0,vb=i<pb.Length?int.Parse(pb[i]):0;
                    if(va!=vb)return va-vb;
                } return 0;
            }catch{return 0;}
        }

        
        // ── GET /api/hht/plants ────────────────────────────────────────────────
        // Returns plant code → short name lookup for HU Swap label printing.
        // Cached in memory on deployment — call once per app session.
        [HttpGet, Route("plants")]
        public HttpResponseMessage GetPlants()
        {
            return Request.CreateResponse(HttpStatusCode.OK, _plantNames);
        }

        // ── POST /api/hht/refresh-plants ───────────────────────────────────────
        // Re-fetches from Supabase and reloads the in-memory cache.
        // Call this after store master changes instead of redeploying.
        [HttpPost, Route("refresh-plants")]
        public async Task<HttpResponseMessage> RefreshPlants()
        {
            try
            {
                const string SUPABASE_URL = "https://pymdqnnwwxrgeolvgvgv.supabase.co";
                const string ANON_KEY     = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InB5bWRxbm53d3hyZ2VvbHZndmd2Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTMzMzU0NzYsImV4cCI6MjA2ODkxMTQ3Nn0.jUrb0jIg6qjj2Rlh9DxYesSnbstoD4uoDCswqOqAkUM";
                const string ENDPOINT     = SUPABASE_URL +
                    "/rest/v1/store_plant_master_aka?select=STORE-CODE,STORE-NAME&limit=1000";

                using (var req = new HttpRequestMessage(HttpMethod.Get, ENDPOINT))
                {
                    req.Headers.Add("apikey",        ANON_KEY);
                    req.Headers.Add("Authorization", "Bearer " + ANON_KEY);
                    var resp = await _http.SendAsync(req);
                    var body = await resp.Content.ReadAsStringAsync();
                    var rows = Newtonsoft.Json.JsonConvert.DeserializeObject<
                        List<Dictionary<string, string>>>(body);

                    int count = 0;
                    _plantNames.Clear();
                    foreach (var row in rows)
                    {
                        var code = row.ContainsKey("STORE-CODE") ? (row["STORE-CODE"] ?? "").Trim().ToUpper() : "";
                        var name = row.ContainsKey("STORE-NAME") ? (row["STORE-NAME"] ?? "").Trim()         : "";
                        if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(name))
                        {
                            _plantNames[code] = name;
                            count++;
                        }
                    }
                    return Request.CreateResponse(HttpStatusCode.OK,
                        new { refreshed = count, message = "Plant names reloaded from Supabase" });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message });
            }
        }

        [HttpGet, Route("appversion")]
        public HttpResponseMessage AppVersion()
        {
            var qs=System.Web.HttpUtility.ParseQueryString(Request.RequestUri.Query);
            var maj=qs["majorVersion"]??""; var min=qs["minorVersion"]??"";
            var dv=(maj.Length>0&&min.Length>0)?maj+"."+min:APK_VERSION;
            var upg=CmpVer(dv,MIN_APK_VERSION)<0?"available":"none";
            return Json($"{{\"upgrade\":\"{upg}\",\"version\":\"{APK_VERSION}\",\"downloadLink\":\"{APK_URL}\"}}");
        }

        [HttpGet, Route("ValueXMW/appversion")]
        public HttpResponseMessage AppVersionLegacy()
        {
            var qs=System.Web.HttpUtility.ParseQueryString(Request.RequestUri.Query);
            var maj=qs["majorVersion"]??""; var min=qs["minorVersion"]??"";
            var dv=(maj.Length>0&&min.Length>0)?maj+"."+min:APK_VERSION;
            var upg=CmpVer(dv,MIN_APK_VERSION)<0?"available":"none";
            return Json($"{{\"upgrade\":\"{upg}\",\"version\":\"{APK_VERSION}\",\"downloadLink\":\"{APK_URL}\"}}");
        }


        // ── Health ─────────────────────────────────────────────────────────────
        [HttpGet, Route("health")]
        public async Task<HttpResponseMessage> Health()
        {
            string javaBase   = GetJavaBase();
            string javaStatus = "unreachable";
            if (javaBase != null)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    var r  = await _http.GetAsync(javaBase.Replace("/xmwgw", "") + "/index.jsp").ConfigureAwait(false);
                    sw.Stop();
                    javaStatus = $"ok:{(int)r.StatusCode}:{sw.ElapsedMilliseconds}ms";
                }
                catch (Exception ex)
                {
                    javaStatus = "err:" + ex.Message.Substring(0, Math.Min(60, ex.Message.Length)).Replace("\n", " ");
                }
            }

            int activeOpcodes     = _opcodeStats.Count;
            int registeredOpcodes = ALL_OPCODES.Count;
            long totalCalls       = _opcodeStats.Values.Sum(s => s.Count);

            return Txt(
                $"OK|{MW_VERSION}" +
                $"|apk={APK_VERSION}" +
                $"|java={javaBase ?? "not-discovered"}" +
                $"|java={javaStatus}" +
                $"|calls_total={totalCalls}" +
                $"|active_opcodes={activeOpcodes}" +
                $"|registered_opcodes={registeredOpcodes}" +
                $"|stats_persisted={_statsLoaded}" +
                $"|{DateTime.UtcNow:yyyy-MM-dd HH:mm}UTC"
            );
        }


        [HttpGet, Route("stats")]
        public HttpResponseMessage Stats()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== V2 HHT Azure Middleware — Live Stats ===");
            sb.AppendLine($"Time           : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"MW Version     : {MW_VERSION}");
            sb.AppendLine($"Java Base      : {_javaBase ?? "not-discovered"}");
            sb.AppendLine($"Stats persisted: {_statsLoaded} (file: {STATS_FILE})");
            sb.AppendLine();

            int    active     = _opcodeStats.Count;
            int    registered = ALL_OPCODES.Count;
            long   total      = _opcodeStats.Values.Sum(s => s.Count);
            long   errors     = _opcodeStats.Values.Sum(s => s.Errors);

            sb.AppendLine($"Registered opcodes : {registered}");
            sb.AppendLine($"Active opcodes     : {active} (called at least once)");
            sb.AppendLine($"Never-called       : {registered - active} (not yet used in current period)");
            sb.AppendLine($"Total RFC calls    : {total}");
            sb.AppendLine($"Infra errors       : {errors}");
            sb.AppendLine();

            // Active opcodes — full stats
            sb.AppendLine("ACTIVE OPCODE PERFORMANCE:");
            sb.AppendLine($"{"Opcode",-42} {"Calls",6} {"Errors",6} {"Avg ms",8} {"Min ms",8} {"Max ms",8} {"P95 ms",8} {"LastSeen",19}");
            sb.AppendLine(new string('-', 115));
            foreach (var kv in _opcodeStats.OrderByDescending(x => x.Value.Count))
            {
                var s = kv.Value;
                sb.AppendLine($"{kv.Key,-42} {s.Count,6} {s.Errors,6} {s.AvgMs,8:F0} {s.MinMs,8:F0} {s.MaxMs,8:F0} {s.P95Ms,8:F0} {s.LastSeen:yyyy-MM-dd HH:mm:ss}");
            }

            // Never-called opcodes
            sb.AppendLine();
            sb.AppendLine("NEVER-CALLED OPCODES (registered but 0 calls this period):");
            var neverCalled = ALL_OPCODES.Where(o => !_opcodeStats.ContainsKey(o)).OrderBy(o => o).ToList();
            sb.AppendLine(string.Join(", ", neverCalled));

            // Recent calls
            sb.AppendLine();
            sb.AppendLine("LAST 500 CALLS:");
            sb.AppendLine($"{"Timestamp",-20} {"User",-8} {"Opcode",-35} {"Store",-6} {"Ms",6} {"OK",4} {"Resp",40}");
            sb.AppendLine(new string('-', 125));
            var recent = _ring.ToArray();
            int ringCount = Math.Min(recent.Length, 500);
            for (int i = recent.Length - 1; i >= recent.Length - ringCount; i--)
            {
                var c = recent[i];
                string uid = string.IsNullOrEmpty(c.UserId) ? "-" : c.UserId;
                sb.AppendLine($"{c.Timestamp.ToString("HH:mm:ss.fff"),-20} {uid,-8} {c.Opcode,-35} {c.Store,-6} {c.ElapsedMs,6} {(c.SapOk?"✅":"❌"),4}  {c.ResponseSnippet,-40}");
            }

            return Txt(sb.ToString());
        }

        // ── Cache stats ───────────────────────────────────────────────────────
        [HttpGet, Route("cache/stats")]
        public HttpResponseMessage CacheStats()
        {
            var now = DateTime.UtcNow;
            var live  = _cache.Where(kv => kv.Value.Expires > now).ToList();
            var data  = live.Select(kv => new {
                key     = kv.Key,
                expires = kv.Value.Expires.ToString("HH:mm:ss"),
                ttl_sec = (int)(kv.Value.Expires - now).TotalSeconds
            }).OrderBy(x => x.key).ToList();
            return Json(Newtonsoft.Json.JsonConvert.SerializeObject(new {
                live_entries   = live.Count,
                total_entries  = _cache.Count,
                cacheable_ops  = CACHEABLE.Count,
                ttl_seconds    = (int)CACHE_TTL.TotalSeconds,
                entries        = data
            }));
        }

        [HttpPost, Route("cache/clear")]
        public HttpResponseMessage CacheClear()
        {
            _cache.Clear();
            return Json(@"{""cleared"":true}");
        }

        // ── Active device sessions ────────────────────────────────────────────
        [HttpGet, Route("sessions")]
        public HttpResponseMessage Sessions()
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-60);
            var active = _sessions.Values
                .Where(s => s.LastSeen >= cutoff)
                .OrderByDescending(s => s.LastSeen)
                .Select(s => new {
                    user_id        = s.UserId,
                    store          = s.Store,
                    last_opcode    = s.LastOpcode,
                    last_seen      = s.LastSeen.ToString("HH:mm:ss"),
                    last_seen_mins = (int)(DateTime.UtcNow - s.LastSeen).TotalMinutes,
                    call_count     = s.CallCount,
                    active         = (DateTime.UtcNow - s.LastSeen).TotalMinutes < 5
                }).ToList();
            return Json(Newtonsoft.Json.JsonConvert.SerializeObject(new { sessions = active, total = active.Count }));
        }

        // ── Per-opcode drill-down ──────────────────────────────────────────────
        [HttpGet, Route("stats/{opcode}")]
        public HttpResponseMessage StatsOpcode(string opcode)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Opcode: {opcode} ===");
            sb.AppendLine($"Registered: {(ALL_OPCODES.Contains(opcode) ? "YES" : "NO — not in router")}");
            if (_opcodeStats.TryGetValue(opcode, out var s))
            {
                sb.AppendLine($"Status     : ACTIVE");
                sb.AppendLine($"Total calls: {s.Count}");
                sb.AppendLine($"Errors     : {s.Errors}");
                sb.AppendLine($"Avg latency: {s.AvgMs:F0}ms");
                sb.AppendLine($"Min latency: {s.MinMs:F0}ms");
                sb.AppendLine($"Max latency: {s.MaxMs:F0}ms");
                sb.AppendLine($"P95 latency: {s.P95Ms:F0}ms");
                sb.AppendLine($"Last error : {s.LastError ?? "none"}");
                sb.AppendLine($"Last seen  : {s.LastSeen:yyyy-MM-dd HH:mm:ss} UTC");
            }
            else
            {
                sb.AppendLine($"Status     : NEVER CALLED (0 calls in current period)");
            }
            sb.AppendLine();
            sb.AppendLine("Recent calls:");
            var calls = _ring.ToArray();
            int shown = 0;
            for (int i = calls.Length - 1; i >= 0 && shown < 30; i--)
            {
                var c = calls[i];
                if (!c.Opcode.Equals(opcode, StringComparison.OrdinalIgnoreCase)) continue;
                sb.AppendLine($"  {c.Timestamp:HH:mm:ss}  Store={c.Store}  {c.ElapsedMs}ms  {(c.SapOk?"✅":"❌")}  {c.ResponseSnippet}");
                shown++;
            }
            if (shown == 0) sb.AppendLine("  No recent calls in ring buffer.");
            return Txt(sb.ToString());
        }

        // ── Manual flush ───────────────────────────────────────────────────────
        [HttpPost, Route("stats/flush")]
        public HttpResponseMessage FlushStats()
        {
            FlushStatsToDisk();
            return Txt($"Flushed {_opcodeStats.Count} opcodes to {STATS_FILE}");
        }

        // ── Reset stats ────────────────────────────────────────────────────────
        [HttpPost, Route("stats/reset")]
        public HttpResponseMessage ResetStats()
        {
            _opcodeStats.Clear();
            while (_ring.TryDequeue(out _)) { }
            FlushStatsToDisk();
            return Txt("Stats reset and file cleared.");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // PROXY
        // ═══════════════════════════════════════════════════════════════════════

        private async Task<HttpResponseMessage> Proxy()
        {
            string javaBase = GetJavaBase();
            if (javaBase == null)
                return LogAndReturn(null, 0, "E#HC tunnel down — cannot reach Server 200", false, "?");

            string body   = await Request.Content.ReadAsStringAsync().ConfigureAwait(false);

            // v12 app posts JSON to /ValueXMW — route to ProxyNoAcl which returns SAP JSON
            if (body.TrimStart().StartsWith("{"))
                return await ProxyNoAcl(body).ConfigureAwait(false);

            string opcode  = ExtractOpcode(body);
            string store   = ExtractStore(body);
            string userId  = ExtractUserId(body);

            // Cache check — serve cached response for read-only opcodes
            if (TryGetCache(opcode, store, out string cachedBody))
            {
                LogAndReturn(opcode, 0, cachedBody, true, store, userId);
                var cachedResp = Request.CreateResponse(System.Net.HttpStatusCode.OK);
                cachedResp.Content = new StringContent(cachedBody, Encoding.UTF8, "application/json");
                cachedResp.Headers.Add("X-Cache", "HIT");
                return cachedResp;
            }

            var    sw      = Stopwatch.StartNew();
            string respBody;
            bool   sapOk;

            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, javaBase + "/ValueXMW")
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/plain")
                };
                foreach (var h in Request.Headers)
                    if (h.Key.StartsWith("X-HHT-", StringComparison.OrdinalIgnoreCase))
                        req.Headers.TryAddWithoutValidation(h.Key, h.Value);

                var resp  = await _http.SendAsync(req).ConfigureAwait(false);
                sw.Stop();
                respBody  = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                sapOk     = IsInfraOk(respBody);
            }
            catch (TaskCanceledException)
            {
                sw.Stop();
                respBody = "E#SAP timeout — RFC did not respond in 55s";
                sapOk    = false;
            }
            catch (Exception ex)
            {
                sw.Stop();
                respBody = "E#Proxy error: " + ex.Message.Replace("\n", " ");
                sapOk    = false;
            }

            // Cache successful responses for cacheable opcodes
            if (sapOk) SetCache(opcode, store, respBody);

            return LogAndReturn(opcode, sw.ElapsedMilliseconds, respBody, sapOk, store, userId);
        }

        // ═══════════════════════════════════════════════════════════════════════
        // LOGGING
        // ═══════════════════════════════════════════════════════════════════════

        private HttpResponseMessage LogAndReturn(string opcode, long ms, string resp, bool ok, string store, string userId = "")
        {
            if (opcode != null)
            {
                // Ring buffer
                var entry = new CallLog
                {
                    Timestamp       = DateTime.UtcNow,
                    Opcode          = opcode,
                    Store           = store ?? "?",
                    UserId          = userId ?? "",
                    ElapsedMs       = ms,
                    SapOk           = ok,
                    ResponseSnippet = (resp ?? "").Length > 60
                        ? resp.Substring(0, 60) : resp ?? ""
                };
                _ring.Enqueue(entry);
                while (_ring.Count > RING_MAX) _ring.TryDequeue(out _);

                // Update active sessions — track by userId (from scnrec/login) 
                // OR by store code when store is a real 4-char site code (not "?")
                // store is a real plant code if it's short (2-6 chars), not "?", 
                // and contains no underscores (opcodes have underscores or are lowercase)
                bool isRealStore = store != null && store != "?" 
                                   && store.Length >= 2 && store.Length <= 6
                                   && !store.Contains("_")
                                   && store != opcode
                                   && !store.Equals("scnrec", StringComparison.OrdinalIgnoreCase);
                var sessionKey = !string.IsNullOrEmpty(userId) ? userId
                                : isRealStore ? "S:" + store
                                : null;
                if (sessionKey != null)
                {
                    var displayId = !string.IsNullOrEmpty(userId) ? userId : store;
                    _sessions.AddOrUpdate(sessionKey,
                        _ => new DeviceSession { UserId = displayId, Store=store??"?", LastOpcode=opcode, LastSeen=DateTime.UtcNow, CallCount=1 },
                        (_, s) => { s.Store=store??"?"; s.LastOpcode=opcode; s.LastSeen=DateTime.UtcNow; s.CallCount++; return s; });
                }

                // Opcode stats (persisted)
                _opcodeStats.AddOrUpdate(opcode,
                    _ => new OpcodeStats(ms, ok,
                        ok ? null : (resp ?? "").Substring(0, Math.Min(80, (resp ?? "").Length))),
                    (_, existing) =>
                    {
                        existing.Record(ms, ok,
                            ok ? null : (resp ?? "").Substring(0, Math.Min(80, (resp ?? "").Length)));
                        return existing;
                    });

                // Structured log → stdout → App Insights
                var snip = (resp ?? "").Replace("\n"," ").Replace("|",":");
                if (snip.Length > 80) snip = snip.Substring(0, 80);
                Console.WriteLine($"[HHT] {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}|{opcode}|{store}|{ms}|{(ok?"OK":"ERR")}|{snip}");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(resp ?? "", Encoding.UTF8, "text/plain")
            };
        }

        // ═══════════════════════════════════════════════════════════════════════
        // PERSISTENCE — JSON file in D:\home\data (survives restarts)
        // ═══════════════════════════════════════════════════════════════════════

        private static void LoadStatsFromDisk()
        {
            try
            {
                if (!File.Exists(STATS_FILE)) { _statsLoaded = false; return; }
                var json = File.ReadAllText(STATS_FILE);
                var dict = JsonConvert.DeserializeObject<Dictionary<string, PersistedStats>>(json);
                if (dict == null) { _statsLoaded = false; return; }
                foreach (var kv in dict)
                {
                    var p = kv.Value;
                    var s = new OpcodeStats((long)p.MinMs, true, null);
                    s.RestoreFrom(p);
                    _opcodeStats[kv.Key] = s;
                }
                _statsLoaded = true;
                Console.WriteLine($"[HHT-PERSIST] Loaded {dict.Count} opcodes from {STATS_FILE}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HHT-PERSIST] Load error: {ex.Message}");
                _statsLoaded = false;
            }
        }

        private static void FlushStatsToDisk()
        {
            try
            {
                var dir = Path.GetDirectoryName(STATS_FILE);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var dict = new Dictionary<string, PersistedStats>();
                foreach (var kv in _opcodeStats)
                {
                    var s = kv.Value;
                    dict[kv.Key] = new PersistedStats
                    {
                        Count     = s.Count,
                        Errors    = s.Errors,
                        MinMs     = s.MinMs,
                        MaxMs     = s.MaxMs,
                        AvgMs     = s.AvgMs,
                        P95Ms     = s.P95Ms,
                        LastError = s.LastError,
                        LastSeen  = s.LastSeen.ToString("o")
                    };
                }

                var json = JsonConvert.SerializeObject(dict);
                lock (_fileLock)
                {
                    File.WriteAllText(STATS_FILE + ".tmp", json);
                    if (File.Exists(STATS_FILE)) File.Delete(STATS_FILE);
                    File.Move(STATS_FILE + ".tmp", STATS_FILE);
                }
                Console.WriteLine($"[HHT-PERSIST] Flushed {dict.Count} opcodes → {STATS_FILE}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HHT-PERSIST] Flush error: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // HC DISCOVERY
        // ═══════════════════════════════════════════════════════════════════════

        private static string GetJavaBase()
        {
            if (_javaBase != null) return _javaBase;
            lock (_discoveryLock)
            {
                if (_javaBase != null) return _javaBase;
                var found = new ConcurrentBag<int>();
                var tasks = new List<Task>();
                for (int i = 1; i <= 254; i++)
                {
                    int idx = i;
                    tasks.Add(Task.Run(() =>
                    {
                        try
                        {
                            using (var sock = new System.Net.Sockets.Socket(
                                System.Net.Sockets.AddressFamily.InterNetwork,
                                System.Net.Sockets.SocketType.Stream,
                                System.Net.Sockets.ProtocolType.Tcp))
                            {
                                sock.Blocking = false;
                                try { sock.Connect($"127.0.0.{idx}", 9080); } catch { }
                                var w = new List<System.Net.Sockets.Socket> { sock };
                                var e = new List<System.Net.Sockets.Socket> { sock };
                                System.Net.Sockets.Socket.Select(null, w, e, 200000);
                                if (w.Count > 0 && e.Count == 0) found.Add(idx);
                            }
                        }
                        catch { }
                    }));
                }
                Task.WaitAll(tasks.ToArray(), 4000);
                int best = int.MaxValue;
                foreach (var x in found) if (x < best) best = x;
                _javaBase = best < int.MaxValue ? $"http://127.0.0.{best}:9080/xmwgw" : null;
                return _javaBase;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════════════════

        private static string ExtractOpcode(string body)
        {
            if (string.IsNullOrEmpty(body)) return "unknown";
            int idx = body.IndexOf('#');
            return idx > 0 ? body.Substring(0, idx).Trim().ToLowerInvariant() : body.Trim().ToLowerInvariant();
        }

        private static string ExtractStore(string body)
        {
            if (string.IsNullOrEmpty(body)) return "?";
            // JSON body (v12 app): {"bapiname":"RFC","IM_WERKS":"DH24",...}
            if (body.TrimStart().StartsWith("{"))
            {
                try {
                    var j = Newtonsoft.Json.Linq.JObject.Parse(body);
                    var werks = j["IM_WERKS"]?.ToString() ?? j["im_werks"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(werks)) return werks;
                    // Fallback: any field that looks like a plant code
                    foreach (var kv in j)
                        if (kv.Value?.ToString().Length >= 3 && kv.Value.ToString().Length <= 6
                            && (kv.Value.ToString().StartsWith("H") || kv.Value.ToString().StartsWith("DH")))
                            return kv.Value.ToString();
                } catch { }
                return "?";
            }
            // Legacy body: opcode#user#password#store#...
            var parts = body.Split('#');
            if (parts.Length >= 4 && parts[0].Equals("scnrec", StringComparison.OrdinalIgnoreCase)) return parts[3];
            if (parts.Length >= 2 && parts[1].Length >= 3 && parts[1].Length <= 6
                && (parts[1].StartsWith("H") || parts[1].StartsWith("DH") || parts[1].StartsWith("h")))
                return parts[1];
            return "?";
        }

        private static string ExtractUserId(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            // JSON body: {"bapiname":"RFC","im_userid":"250","IM_USERID":"250",...}
            if (body.TrimStart().StartsWith("{"))
            {
                try {
                    var j = Newtonsoft.Json.Linq.JObject.Parse(body);
                    return j["im_userid"]?.ToString() ?? j["IM_USERID"]?.ToString()
                        ?? j["im_password"]?.ToString() ?? j["IM_PASSWORD"]?.ToString() ?? "";
                } catch { }
                return "";
            }
            // Legacy: opcode#user#password#store → parts[1]=user
            var parts = body.Split('#');
            return parts.Length >= 2 ? parts[1] : "";
        }

        // Infrastructure errors only (tunnel/proxy failures) — SAP business errors are OK
        private static bool IsInfraOk(string resp)
        {
            if (string.IsNullOrEmpty(resp)) return true;
            var r = resp.TrimStart();
            return !r.StartsWith("E#HC tunnel") && !r.StartsWith("E#Proxy error") &&
                   !r.StartsWith("E#SAP timeout") && !r.StartsWith("E#not-discovered");
        }

        private static HttpResponseMessage Txt(string s) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(s, Encoding.UTF8, "text/plain") };

        private static HttpResponseMessage Json(string s) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(s, Encoding.UTF8, "application/json") };

        // ═══════════════════════════════════════════════════════════════════════
        // DATA MODELS
        // ═══════════════════════════════════════════════════════════════════════

        private class CallLog
        {
            public DateTime Timestamp       { get; set; }
            public string   Opcode          { get; set; }
            public string   Store           { get; set; }
            public string   UserId          { get; set; }
            public long     ElapsedMs       { get; set; }
            public bool     SapOk           { get; set; }
            public string   ResponseSnippet { get; set; }
        }

        class DeviceSession
        {
            public string   UserId      { get; set; }
            public string   Store       { get; set; }
            public string   LastOpcode  { get; set; }
            public DateTime LastSeen    { get; set; }
            public int      CallCount   { get; set; }
        }

        public class PersistedStats
        {
            public long   Count     { get; set; }
            public long   Errors    { get; set; }
            public double MinMs     { get; set; }
            public double MaxMs     { get; set; }
            public double AvgMs     { get; set; }
            public double P95Ms     { get; set; }
            public string LastError { get; set; }
            public string LastSeen  { get; set; }
        }


        private async Task<HttpResponseMessage> ProxyNoAcl(string preReadBody = null)
        {
            // v12 app sends: POST /noacljsonrfcadaptor?bapiname=RFC_NAME
            // Body: {"bapiname":"RFC","IM_PARAM1":"val",...}
            //
            // Two-path strategy:
            //   Path A: Try Java /noacljsonrfcadaptor with strict application/json
            //           -> returns native SAP JSON for ALL new RFCs (ZGRT_*, ZFM_*, etc.)
            //   Path B: Fall back to Java /ValueXMW if content-type rejected
            //           -> works for older RFCs that exist in ValueXMW handler

            string javaBase = GetJavaBase();
            if (javaBase == null)
                return LogAndReturn("noacl", 0, "E#HC tunnel down", false, "?");

            string rawBody = preReadBody ?? await Request.Content.ReadAsStringAsync().ConfigureAwait(false);

            // Parse bapi name and IM_ params
            string bapi  = "";
            var imVals   = new System.Collections.Generic.List<string>();
            try
            {
                var jobj = Newtonsoft.Json.Linq.JObject.Parse(rawBody);
                bapi = jobj["bapiname"]?.ToString() ?? "";
                foreach (var kv in jobj)
                    if (kv.Key.StartsWith("IM_", System.StringComparison.OrdinalIgnoreCase))
                        imVals.Add(kv.Value?.ToString() ?? "");
            }
            catch { }

            var qs = System.Web.HttpUtility.ParseQueryString(Request.RequestUri?.Query ?? "");
            if (string.IsNullOrEmpty(bapi)) bapi = qs["bapiname"] ?? "noacl";

            string opcode  = bapi.Equals("ZWM_USER_AUTHORITY_CHECK", System.StringComparison.OrdinalIgnoreCase)
                             ? "scnrec" : bapi.ToLower();
            string store   = ExtractStore(rawBody);
            string userId  = ExtractUserId(rawBody);

            var sw = Stopwatch.StartNew();

            // ── PATH A: Java /noacljsonrfcadaptor (native SAP JSON response) ────
            try
            {
                string noaclUrl = javaBase.Replace("/xmwgw", "/xmwgw/noacljsonrfcadaptor")
                                  + "?" + (qs.Count > 0 ? qs.ToString() : "bapiname=" + bapi + "&aclclientid=android");

                // CRITICAL: set Content-Type as MediaTypeHeaderValue (no charset suffix)
                // Java's noacljsonrfcadaptor checks for exact "application/json"
                var noaclContent = new StringContent(rawBody, Encoding.UTF8);
                noaclContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                var noaclReq  = new HttpRequestMessage(HttpMethod.Post, noaclUrl) { Content = noaclContent };
                var noaclResp = await _http.SendAsync(noaclReq).ConfigureAwait(false);
                string noaclRaw = await noaclResp.Content.ReadAsStringAsync().ConfigureAwait(false);

                // Java accepted the request if response is valid JSON (not the content-type error string)
                if (!string.IsNullOrEmpty(noaclRaw) &&
                    !noaclRaw.Contains("Only Applicaton/Json") &&
                    !noaclRaw.Contains("Content Type Not supported") &&
                    noaclRaw.TrimStart().StartsWith("{"))
                {
                    sw.Stop();
                    bool ok = IsInfraOk(noaclRaw);
                    LogAndReturn(opcode, (long)sw.ElapsedMilliseconds, noaclRaw, ok, store);
                    if (ok) SetCache(opcode, "?", noaclRaw);
                    var nativeResp = Request.CreateResponse(System.Net.HttpStatusCode.OK);
                    nativeResp.Content = new StringContent(noaclRaw, Encoding.UTF8, "application/json");
                    nativeResp.Headers.Add("X-Cache", "MISS");
                    return nativeResp;
                }
                // Java rejected content type — fall through to Path B
            }
            catch { /* fall through to ValueXMW */ }

            // ── PATH B: Java /ValueXMW with old opcode format ────────────────────
            // Translate: bapiname + IM_ values → "opcode#val1#val2#...#<eol>"
            var legacySb = new System.Text.StringBuilder(opcode);
            foreach (var v in imVals) legacySb.Append("#").Append(v);
            legacySb.Append("#<eol>");

            string respBody; bool sapOk;
            try
            {
                var legReq = new HttpRequestMessage(HttpMethod.Post, javaBase + "/ValueXMW")
                {
                    Content = new StringContent(legacySb.ToString(), Encoding.UTF8, "application/json")
                };
                var legResp = await _http.SendAsync(legReq).ConfigureAwait(false);
                sw.Stop();
                respBody = await legResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                sapOk    = IsInfraOk(respBody);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return LogAndReturn(opcode, (long)sw.ElapsedMilliseconds, "E#" + ex.Message, false, store);
            }

            // Translate old response format → SAP JSON for v12 app
            string jsonOut = BuildSapJson(bapi, respBody ?? "");
            LogAndReturn(opcode, (long)sw.ElapsedMilliseconds, respBody, sapOk, store);
            var httpOut = Request.CreateResponse(System.Net.HttpStatusCode.OK);
            httpOut.Content = new StringContent(jsonOut, Encoding.UTF8, "application/json");
            return httpOut;
        }

        // Translate Java ValueXMW "Response:X#p1#p2#..." → SAP JSON for v12 app
        //
        // Java response formats:
        //   Response:1#data   = success with data
        //   Response:0        = failure (auth or general)
        //   Response:E#msg    = SAP explicit error with message
        //   Response:S#data   = SAP success with structured data
        //   Response:null     = opcode unknown to Java
        private string BuildSapJson(string bapi, string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw.Trim().Equals("Response:null", StringComparison.OrdinalIgnoreCase))
            {
                var e0 = new Newtonsoft.Json.Linq.JObject();
                e0["EX_RETURN"] = new Newtonsoft.Json.Linq.JObject(
                    new Newtonsoft.Json.Linq.JProperty("TYPE",    "E"),
                    new Newtonsoft.Json.Linq.JProperty("MESSAGE", "Operation not supported. Please update the app.")
                );
                return e0.ToString(Newtonsoft.Json.Formatting.None);
            }

            // Strip "Response:" prefix
            string payload = raw.StartsWith("Response:") ? raw.Substring(9) : raw;

            // Trim trailing #<eol> or <eol>
            if (payload.EndsWith("<eol>")) payload = payload.Substring(0, payload.Length - 5);
            payload = payload.TrimEnd('#').Trim();

            string[] parts  = payload.Split('#');
            string   status = parts.Length > 0 ? parts[0].Trim() : "";

            var obj = new Newtonsoft.Json.Linq.JObject();

            // ── Explicit SAP error (Response:E#message) ──────────────────────
            if (status.Equals("E", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("0"))
            {
                string msg = parts.Length > 1
                    ? string.Join(" ", parts, 1, parts.Length - 1).Trim()
                    : (status == "0" ? "Authentication failed. Check your SAP credentials." : "SAP returned an error.");
                obj["EX_RETURN"] = new Newtonsoft.Json.Linq.JObject(
                    new Newtonsoft.Json.Linq.JProperty("TYPE",    "E"),
                    new Newtonsoft.Json.Linq.JProperty("MESSAGE", msg)
                );
                return obj.ToString(Newtonsoft.Json.Formatting.None);
            }

            // ── Success (Response:1#... or Response:S#...) ───────────────────
            obj["EX_RETURN"] = new Newtonsoft.Json.Linq.JObject(
                new Newtonsoft.Json.Linq.JProperty("TYPE",    "S"),
                new Newtonsoft.Json.Linq.JProperty("MESSAGE", "")
            );

            if (bapi.Equals("ZWM_USER_AUTHORITY_CHECK", StringComparison.OrdinalIgnoreCase))
            {
                // Login: Response:1#WERKS
                // Derive EX_GROUP from WERKS (same logic as v11.83 app):
                //   DH* plants = DC/Warehouse
                //   DH25       = Ecomm
                //   everything else = Store
                string werks = parts.Length > 1 ? parts[1].Trim() : "";
                string group = werks.StartsWith("DH", StringComparison.OrdinalIgnoreCase) ? "DC" : "";
                if (werks.Equals("DH25", StringComparison.OrdinalIgnoreCase)) group = "";
                obj["EX_WERKS"] = werks;
                obj["EX_GROUP"] = group;
            }
            else
            {
                // All other RFCs: pass the raw response through as a data field
                // The app fragments check EX_RETURN.TYPE first, then read their
                // own specific EX_ fields — for now pass raw in EX_RETURN.MESSAGE
                // so at minimum it doesn't crash, and we can map fields later
                obj["EX_RETURN"]["MESSAGE"] = raw;

                // Also put full raw response in ET_DATA for fragments that read it
                var arr = new Newtonsoft.Json.Linq.JArray();
                var row = new Newtonsoft.Json.Linq.JObject();
                row["RESPONSE"] = raw;
                arr.Add(row);
                obj["EX_RETURN"]["ET_DATA"] = arr;
            }

            return obj.ToString(Newtonsoft.Json.Formatting.None);
        }



        private class OpcodeStats
        {
            private readonly object _lock = new object();
            private readonly List<long> _samples = new List<long>(200);

            public long     Count     { get; private set; }
            public long     Errors    { get; private set; }
            public double   MinMs     { get; private set; }
            public double   MaxMs     { get; private set; }
            public double   AvgMs     { get; private set; }
            public double   P95Ms     { get; private set; }
            public string   LastError { get; private set; }
            public DateTime LastSeen  { get; private set; }

            public OpcodeStats(long ms, bool ok, string err)
            {
                MinMs = MaxMs = AvgMs = P95Ms = ms;
                Count = 1; Errors = ok ? 0 : 1; LastError = err;
                LastSeen = DateTime.UtcNow; _samples.Add(ms);
            }

            public void Record(long ms, bool ok, string err)
            {
                lock (_lock)
                {
                    Count++;
                    if (!ok) { Errors++; if (err != null) LastError = err; }
                    if (ms < MinMs) MinMs = ms;
                    if (ms > MaxMs) MaxMs = ms;
                    AvgMs = (AvgMs * (Count - 1) + ms) / Count;
                    LastSeen = DateTime.UtcNow;
                    if (_samples.Count >= 200) _samples.RemoveAt(0);
                    _samples.Add(ms);
                    var sorted = new List<long>(_samples); sorted.Sort();
                    P95Ms = sorted[Math.Max(0, (int)Math.Ceiling(sorted.Count * 0.95) - 1)];
                }
            }

            // Restore from persisted data (on startup)
            public void RestoreFrom(PersistedStats p)
            {
                lock (_lock)
                {
                    Count     = p.Count;
                    Errors    = p.Errors;
                    MinMs     = p.MinMs;
                    MaxMs     = p.MaxMs;
                    AvgMs     = p.AvgMs;
                    P95Ms     = p.P95Ms;
                    LastError = p.LastError;
                    DateTime.TryParse(p.LastSeen, out var dt);
                    LastSeen  = dt;
                    // Seed samples with AvgMs for percentile continuity
                    _samples.Clear();
                    for (int i = 0; i < Math.Min(10, (int)p.Count); i++)
                        _samples.Add((long)p.AvgMs);
                }
            }
        }
    }
}
