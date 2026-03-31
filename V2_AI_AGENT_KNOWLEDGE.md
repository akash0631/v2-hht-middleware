# V2 Retail HHT Pipeline — AI Agent Knowledge Base
*Complete reference for Claude AI agents working on this codebase.*
*Last updated from live systems: March 31, 2026.*

> **Note on credentials:** All credentials in this document are stored as GitHub Actions secrets
> and in the `Master_Access_Vault.docx` on Akash's OneDrive. The placeholders like `${GITHUB_TOKEN_SECRET}`
> refer to secrets configured in GitHub repository settings or passed at runtime.
> When running as a GitHub Actions agent, inject these via `env:` in the workflow YAML.

---

## WHO YOU ARE AND WHAT YOU DO

You are an AI coding agent working on the V2 Retail HHT (Hand-Held Terminal) technology platform. V2 Retail operates 320+ apparel/footwear stores across India. The HHT system runs on ~500 Zebra Android devices across warehouses (DCs) and retail stores. Your job is to fix bugs, build features, deploy APKs, update dashboards, and maintain the middleware — exactly like a senior developer who built the whole stack would.

**You interact by reading/writing files directly to GitHub via the GitHub API, then CI/CD handles the rest. Never tell the user to do something you can do yourself.**

---

## THE FULL STACK AT A GLANCE

```
[500 Zebra TC-series HHTs, Android minSdk=25]
    ↓ HTTPS (Volley, every request has X-HHT-Serial header)
[Azure App Service: v2-hht-api.azurewebsites.net]  ← .NET 4.8, Central India, Basic B2
    ↓ Azure Relay Hybrid Connection (v2hhtrelay / sap-hht-live)
[Server 200: 192.168.144.200 — Java/Tomcat :9080/xmwgw]
    ↓ SAP JCo (NCo)
[SAP S4/HANA: 192.168.144.185 — Client 600 Production, Client 210 Dev]

[Dashboard: hht.v2retail.net] ← Cloudflare Worker (v2-hht-dashboard), polls middleware every 20s
[APK CDN: apk.v2retail.net] ← Cloudflare Worker + R2 bucket (v2retail)
```

---

## GITHUB REPOSITORIES

| Repo | Branch | Purpose | Deploy trigger |
|---|---|---|---|
| `V2-Application/Android_HHT` | `main` | Production APK | push → GitHub Actions → R2 CDN |
| `V2-Application/Android_HHT` | `ui-redesign` | Redesigned UI APK | push → GitHub Actions → R2 CDN |
| `akash0631/v2-hht-middleware` | `master` | Azure .NET middleware | push → GitHub Actions → Azure App Service |
| `akash0631/rfc-api` | `main` | SAP RFC REST API | push to `Controllers/**` → deploy-iis.yml → sap-api.v2retail.net |
| `akash0631/v2-data-platform` | `main` | Fabric Gold Layer schema + SQL | reference only |

**GitHub Token (full repo access):** `${GITHUB_TOKEN_SECRET}`

### How to read/write files via GitHub API

```python
import requests, base64, json

H  = {"Authorization": "Bearer ${GITHUB_TOKEN_SECRET}"}
HA = {**H, "Content-Type": "application/json"}

def read_file(repo, path, branch="main"):
    r = requests.get(f"https://api.github.com/repos/{repo}/contents/{path}?ref={branch}", headers=H)
    d = r.json()
    return base64.b64decode(d["content"]).decode(), d["sha"]

def push_file(repo, path, content, sha, message, branch="main"):
    r = requests.put(f"https://api.github.com/repos/{repo}/contents/{path}", headers=HA,
        data=json.dumps({"message": message, "branch": branch,
                         "content": base64.b64encode(content.encode()).decode(), "sha": sha}))
    ok = r.status_code in (200, 201)
    if not ok: raise Exception(r.json().get("message",""))
    return r.json()["commit"]["sha"][:8]
```

**Always patch, never fully rewrite.** Read the file first, make surgical changes, push back. Rewriting entire files risks losing unrelated code.

---

## APK: ANDROID HHT

### Key facts
- **Package:** `com.v2retail.dotvik`
- **Current version:** `12.106` (versionCode `1178`) on `main`; `12.106-ui` (versionCode `1180`) on `ui-redesign`
- **minSdk:** 25 (Android 7.1 Nougat) — Zebra TC-series devices
- **targetSdk:** 31
- **Build system:** Gradle + GitHub Actions (`build-apk.yml`) on `ubuntu-latest`, JDK 17
- **Signing:** keystore decoded from `KEYSTORE_BASE64` secret, password `V2Retail@456`, alias `v2retail`
- **Distribution:** R2 bucket `v2retail` → Cloudflare Worker `apk.v2retail.net`
  - Main build: `apk.v2retail.net/download` → R2 object `V2_HHT_Azure_Release.apk`
  - UI redesign: `apk.v2retail.net/redesign` → R2 object `V2_HHT_UI_Redesign.apk`
- **Auto-update:** Middleware `/appversion` endpoint controls it. `MIN_APK_VERSION=1.0` means all devices pass (no forced upgrade). **Never bump `MIN_APK_VERSION` accidentally** — it forces a mandatory upgrade on 500 devices.

### APK modules and entry points

| Module | Activity / Fragment | Route condition |
|---|---|---|
| Login | `LoginActivity` | Always first |
| DC Dashboard | `Process_Selection_Activity` | `EX_GROUP == "DC"` |
| Store Home | `Home_Activity` | `EX_GROUP` empty (store) |
| Hub | `HubProcessSelectionActivity` | `EX_GROUP == "HUB"` |
| Ecomm | `Ecomm_Process_Selection` | `WERKS == "DH25"` |

### Login flow — CRITICAL to understand

**Azure path (primary):**
1. `IPActivity` → user picks server → calls `/appversion?...` → if up to date, calls `checkIP(url + "/index.jsp")`
2. If `/index.jsp` returns 200, navigates to `LoginActivity`
3. User enters credentials → `LoginActivity` posts JSON to `URL` (Azure middleware ValueXMW)
4. Middleware calls SAP `ZWM_USER_AUTHORITY_CHECK` → returns `{"EX_GROUP":"DC","EX_WERKS":"DH24"}`
5. App routes based on `EX_GROUP` and `WERKS`
6. `finish()` closes LoginActivity, dashboard activity comes to foreground

**Legacy xmwgw path (fallback for old on-prem):**
1. If URL contains `"xmwgw"`, IPActivity skips `/appversion` check (endpoint doesn't exist on old server) and goes straight to `checkIP(URL + "/index.jsp")`
2. Login sends `"scnrec#user#pass#<eol>"` plain text to old middleware
3. Response: `"1#WERKS"` (success) or `"0"` (failure)
4. After success, makes a second call to `noacljsonrfcadaptor?bapiname=ZWM_USER_AUTHORITY_CHECK` to get `EX_GROUP`
5. Routes via `routeUser(group, werks, data)` → `finish()` (NOT moveTaskToBack)

**Critical bug that was fixed:** `moveTaskToBack(true)` after `startActivity()` pushes the whole task to background, surfacing the Downloads folder. Always use `finish()` instead.

### RFC call architecture

All RFC calls from the app go through the Azure middleware as JSON:
```json
{
  "bapiname": "ZWM_STORE_GET_STOCK",
  "IM_WERKS": "HD22",
  "IM_MATNR": "ART-2931"
}
```
Posted to `https://v2-hht-api.azurewebsites.net/api/hht/ValueXMW` (or the xmwgw URL for legacy).

**Every Volley request** automatically includes the `X-HHT-Serial` header (device's Android ANDROID_ID), injected globally via the custom `HurlStack` in `ApplicationController.java`. This is how the middleware counts individual devices (not just store sessions). Do not remove this.

### Package structure

```
com.v2retail/
  ApplicationController.java     ← Custom HurlStack injects X-HHT-Serial on every request
  dotvik/
    IPActivity.java               ← Server selection screen (first screen)
    LoginActivity.java            ← Login, routing, all paths
    dotvik/dc/                    ← 40+ fragments: GRC, GRT, PTL, HU ops, Stock Take, etc.
    dotvik/store/                 ← 70+ fragments: retail, bin transfers, display, ecomm
    dotvik/hub/                   ← Hub inward/outward
    dotvik/ecomm/                 ← Multi-order picking, QC, putwall, packing
  util/
    AppConstants.java             ← PRIMARY_URL = Azure middleware URL
    SharedPreferencesData.java    ← Read/write: USER, WERKS, USERNAME, PASSWORD, LOC
    PlantNames.java               ← Plant code → display name (DH24→"Delhi-DC", etc.)
    TSPLPrinter.java              ← Bluetooth TVS label printer
```

### HU Swap Print label (FragmentHUSwapPrint.java)
- Prints via Bluetooth TSPLPrinter
- Format: full 70×50mm label, From:/To: on split lines, large QR code (cell=8)
- QR contains: new HU number
- Date format: DD.MM.YYYY, Time: HH:MM:SS
- Plant names shown using `PlantNames.label(werks)` — returns "DH24 Delhi-DC"

### Build trigger
Any push to `main` or `ui-redesign` → `build-apk.yml` runs → uploads artifact → you download via API, verify cert, upload to R2. After upload, `apk.v2retail.net/download` serves the new APK.

**Never use `workflow_dispatch` on Android repo** — only push-triggered builds. (RFC API is different — see below.)

### To bump version
Edit `app/build.gradle` on both branches:
```
versionCode XXXX     ← increment by 1
versionName "12.XXX" ← increment minor version
```

---

## MIDDLEWARE: v2-hht-middleware

### Key facts
- **Language:** C# / ASP.NET 4.8 Web API
- **Hosting:** Azure App Service `v2-hht-api.azurewebsites.net` (Basic B2, Central India)
- **Build:** MSBuild on `windows-latest` via `deploy.yml`
- **Deploy trigger:** Any push to `master` → auto-deploys in ~3 minutes
- **Connectivity to SAP:** Azure Relay Hybrid Connection `v2hhtrelay` / `sap-hht-live`, forwarded via HCM on Server 200 (`192.168.144.200`) → SAP at `192.168.144.185`

### Key endpoints

| Endpoint | Method | Purpose |
|---|---|---|
| `/api/hht/ValueXMW` | POST | Main RFC proxy — all v12 app calls |
| `/api/hht/ValueXMW/{app}` | POST | Same, with app param |
| `/api/hht/noacljsonrfcadaptor` | POST/GET | JSON RFC for v12 noacl path |
| `/api/hht/index.jsp` | GET | Connectivity check (returns 200 OK) |
| `/api/hht/ping` | GET | Alias for index.jsp |
| `/api/hht/appversion` | GET | Version check — returns `{"upgrade":"none"}` when MIN_APK_VERSION=1.0 |
| `/api/hht/health` | GET | Full health string |
| `/api/hht/stats` | GET | Plain text: opcode table + ring buffer (500 calls) |
| `/api/hht/sessions` | GET | JSON: active sessions + device counts |
| `/api/hht/cache/stats` | GET | Response cache entries |
| `/api/hht/cache/clear` | GET | Clear response cache |
| `/api/hht/plants` | GET | 494 store/plant names from Supabase |
| `/api/hht/refresh-plants` | GET | Force refresh plant names cache |

### Health check format
```
OK|v2-hht-azure|5.0|apk=12.106|java=http://127.0.0.68:9080/xmwgw|java=ok:200:34ms|calls_total=795810|active_opcodes=138|registered_opcodes=117|stats_persisted=True|2026-03-31 09:00UTC
```

### Sessions endpoint format
```json
{
  "sessions": [
    {
      "user_id": "DH24",
      "store": "DH24",
      "last_opcode": "zwm_rfc_stock_take_arti_vali",
      "last_seen": "06:19:35",
      "last_seen_mins": 0,
      "call_count": 19089,
      "active": true
    }
  ],
  "total": 99,
  "device_count_live": 0,
  "device_count_30m": 0,
  "device_count_total": 0
}
```
*Note: `device_count_*` will populate as devices update to the new APK that sends `X-HHT-Serial` header. Sessions are keyed by store WERKS code, not individual device — each entry = one store location.*

### Two-path RFC routing
1. **Path A (noacl):** Middleware tries `Java /xmwgw/noacljsonrfcadaptor` first — returns native SAP JSON for all modern RFCs
2. **Path B (ValueXMW fallback):** If Path A fails or returns content-type error, falls back to old `Java /xmwgw/ValueXMW` — legacy format, response translated to SAP JSON by `BuildSapJson()`

### In-memory state (resets on restart)
- `_sessions` — active store sessions, keyed by userId or "S:"+store
- `_deviceSerials` — unique device serial+store combos from X-HHT-Serial header, 60min TTL
- `_opcodeStats` — persisted to disk every 60s at `C:\home\data\hht_opcode_stats.json`
- `_ring` — ring buffer of last 1000 calls (500 shown in stats)
- `_cache` — response cache, 60s TTL, read-only opcodes only

### Deployment gotcha
After deploy, Azure restarts the service. In-memory `_sessions`, `_deviceSerials` reset to zero. `_opcodeStats` restores from disk. Wait ~5 minutes for sessions to repopulate from live traffic before checking session counts.

### SAP environments
- **Dev:** `192.168.144.174`, client `210` — use for all new controller work
- **Quality:** `192.168.144.179`, client `600`
- **Production:** `192.168.144.170`, client `600`
- **Never use:** `192.168.144.46`

---

## DASHBOARD: hht.v2retail.net

- **Type:** Cloudflare Worker (`v2-hht-dashboard`)
- **Updates:** Every 20 seconds, fetches from Azure middleware
- **Data sources:** `/health`, `/stats`, `/sessions`, `/cache/stats` — all in parallel

### What it shows
1. **Pipeline flow** — Azure → HC → Java → SAP status
2. **5 KPI cards** — Total calls, Devices Online, Avg latency, Success rate, Active/Registered opcodes
3. **Store Activity strip** — top 20 stores by call volume, color coded DC/Store/Hub
4. **Calls/5min sparkline** — from ring buffer timestamps
5. **Cache status bar**
6. **Connected Devices panel** — sorted by activity, shows call count per site
7. **Live RFC Feed** — last 500 calls, filterable by opcode or user
8. **Bottlenecks** — top 10 slowest opcodes
9. **Errors panel** — opcodes with errors > 0
10. **Opcode performance table** — all 117 registered RFCs

### How to update dashboard
The dashboard is a single Cloudflare Worker with all HTML/CSS/JS inlined. To update:
```python
import requests, io

H = {"Authorization": "Bearer ${CLOUDFLARE_API_TOKEN_SECRET}"}
ACCOUNT = "bab06c93e17ae71cae3c11b4cc40240b"

# Read current worker source
r = requests.get(f"https://api.cloudflare.com/client/v4/accounts/{ACCOUNT}/workers/scripts/v2-hht-dashboard", headers=H)
worker_js = r.text  # returned directly as text

# Make changes to worker_js string...

# Deploy - MUST use this exact format:
boundary = "deploy123xyz456"
buf = io.BytesIO()
buf.write(f"--{boundary}\r\n".encode())
buf.write(b'Content-Disposition: form-data; name="metadata"\r\nContent-Type: application/json\r\n\r\n{"body_part":"worker.js"}')
buf.write(f"\r\n--{boundary}\r\n".encode())
buf.write(b'Content-Disposition: form-data; name="worker.js"\r\n\r\n')
buf.write(worker_js.encode())
buf.write(f"\r\n--{boundary}--".encode())

r2 = requests.put(f"https://api.cloudflare.com/client/v4/accounts/{ACCOUNT}/workers/scripts/v2-hht-dashboard",
    headers={**H, "Content-Type": f"multipart/form-data; boundary={boundary}"}, data=buf.getvalue())
```

**The dashboard uses service-worker syntax** (`addEventListener("fetch", ...)`) **not ES module syntax** (`export default {...}`). Do not change this — CF rejects ES module uploads without a full metadata configuration.

---

## CLOUDFLARE INFRASTRUCTURE

- **Account ID:** `bab06c93e17ae71cae3c11b4cc40240b`
- **CF Token:** `${CLOUDFLARE_API_TOKEN_SECRET}`

### Workers and their purpose

| Worker | URL | Purpose |
|---|---|---|
| `v2-hht-dashboard` | `hht.v2retail.net` | Live ops dashboard |
| `v2-apk-page` | `apk.v2retail.net` | APK download page with QR codes |
| `hht-apk-publisher` | — | APK publishing helper |
| `hht-fleet-dashboard` | — | Alternative fleet view |
| `hht-health-monitor` | — | Sends alerts on health failures |
| `hht-error-alerter` | — | Error threshold alerting |
| `nubo-ads-bot` | `nubo-ads-bot.akash-bab.workers.dev` | Nubo Meta Ads API proxy |
| `nubo-ads-cron` | — | Cron every 2h + 9AM IST digest |
| `v2-sql-analyst` | `sql.v2retail.net` | Plain English → T-SQL → DataV2 |
| `v2-rfc-pipeline` | `v2-rfc-pipeline.akash-bab.workers.dev` | RFC AI parsing pipeline |
| `v2-data-api` | — | Data API layer |

### R2 Buckets

| Bucket | CDN | Contents |
|---|---|---|
| `v2retail` | `apk.v2retail.net` | `V2_HHT_Azure_Release.apk`, `V2_HHT_UI_Redesign.apk` |
| `nubo` | `assets.eatnubo.com` | Nubo ad creatives, `creatives/shashank/` |

**R2 Upload (APK):**
```python
CF = {"Authorization": "Bearer ${CLOUDFLARE_API_TOKEN_SECRET}"}
with open("apk_file.apk", "rb") as f:
    data = f.read()
r = requests.put(
    "https://api.cloudflare.com/client/v4/accounts/bab06c93e17ae71cae3c11b4cc40240b/r2/buckets/v2retail/objects/V2_HHT_Azure_Release.apk",
    headers={**CF, "Content-Type": "application/vnd.android.package-archive"},
    data=data, timeout=120)
```

### Signing cert fingerprint (SHA-256) — for APK verification
```
2a97d10a78ecddff359ecf11d7e5b33a3e58d5a201caff9449d865ef925c5273
```
Always verify this after downloading a build artifact before uploading to CDN.

---

## HOW TO DO COMMON TASKS

### Fix a bug in the Android APK

1. Read the affected file: `read_file("V2-Application/Android_HHT", "path/to/File.java", "main")`
2. Identify the exact lines to change. Always patch, never rewrite the whole file.
3. Apply change, push to both `main` AND `ui-redesign` branches
4. Monitor build: `GET /repos/V2-Application/Android_HHT/actions/runs?per_page=10`
5. When both builds complete (`status==completed, conclusion==success`):
   - Download artifact zip from `actions/artifacts/{id}/zip`
   - Verify signing cert matches `2a97d10a78...`
   - Upload both APKs to R2

### Add a new screen / fragment to the APK

1. Identify which module: DC (dc/), Store (store/), Hub (hub/), Ecomm (ecomm/)
2. Create the new Fragment Java file
3. Create the layout XML in `app/src/main/res/layout/`
4. Add the fragment to the parent menu Activity's button handler
5. Follow Zebra crash rules (see below) for all layout XML
6. Push to both branches, wait for build, upload APKs

### Change a screen layout

1. Read the existing layout XML first
2. PATCH it — never rewrite. Only change what's needed.
3. Follow ALL Zebra crash rules before pushing
4. Push to `main` first, verify build passes, then push same change to `ui-redesign`

### Fix a middleware bug

1. Read `HHTController.cs`: `read_file("akash0631/v2-hht-middleware", "Controllers/HHT/HHTController.cs", "master")`
2. Make surgical change
3. Push to `master` — deploy triggers automatically
4. Monitor: `GET /repos/akash0631/v2-hht-middleware/actions/runs?per_page=3`
5. After `success`: wait 3-4 minutes for Azure to restart, then check `v2-hht-api.azurewebsites.net/api/hht/health`
6. After restart, `_sessions` and `_deviceSerials` are zeroed — wait 5 min for repopulation

### Update the live dashboard

1. Read worker source: `GET https://api.cloudflare.com/client/v4/accounts/{ACCOUNT}/workers/scripts/v2-hht-dashboard`
2. The response IS the JS directly (not multipart this time when reading)
3. Apply string replacements (surgical patches)
4. Deploy using the multipart format shown above — MUST include `{"body_part":"worker.js"}` metadata
5. Verify: `GET https://hht.v2retail.net/` — check the changed content is present

---

## ZEBRA CRASH RULES — NEVER VIOLATE THESE

These rules were learned from crashes on Zebra TC-series devices (Android 7.1, API 25). Violating any of these will crash the app on real devices even if it builds fine.

### Rule 1 — Spinner MUST have android:entries
```xml
<!-- ALWAYS include this on activity_ip.xml Spinner -->
<Spinner
    android:id="@+id/ip_spinner"
    android:entries="@array/ipAddress"    ← THIS IS MANDATORY
    ... />
```
Without `android:entries`, `getSelectedItem()` returns null, causing NPE before any screen shows.

### Rule 2 — AppTheme must NOT have windowBackground
```xml
<!-- WRONG — causes crash on Zebra -->
<style name="AppTheme">
    <item name="android:windowBackground">@color/white</item>  ← REMOVE THIS
</style>

<!-- RIGHT — only AppTheme.Launcher gets windowBackground -->
<style name="AppTheme.Launcher">
    <item name="android:windowBackground">@drawable/launcher_bg</item>
</style>
```

### Rule 3 — No API 26+ layout attributes
```xml
<!-- WRONG — minSdk=25, API 26+ attributes crash on Zebra -->
android:layout_marginVertical="8dp"    ← NOT ALLOWED
android:layout_marginHorizontal="16dp" ← NOT ALLOWED

<!-- RIGHT — use explicit directional attrs -->
android:layout_marginTop="8dp"
android:layout_marginBottom="8dp"
android:layout_marginStart="16dp"
android:layout_marginEnd="16dp"
```

### Rule 4 — No missing system drawables
```xml
<!-- WRONG — @android:drawable/ic_input_add is missing on Zebra -->
android:src="@android:drawable/ic_input_add"

<!-- RIGHT — use your own drawable or a different system drawable -->
android:src="@drawable/ic_add_custom"
```

### Rule 5 — Always PATCH layouts, never fully rewrite
Reading a layout, rewriting it from scratch risks losing subtle attributes and IDs that Java code references. Always read the existing XML, make the minimum change, push back.

### Rule 6 — All Java IDs must exist in XML
If `LoginActivity.java` references `R.id.response`, `R.id.clear`, `R.id.loc`, `R.id.dc`, `R.id.store`, `R.id.email_login_form` — every one of these MUST be present in `activity_login.xml`, even if `visibility="gone"`. Removing an ID causes build failure.

### Rule 7 — No compile-time scope errors in middleware
C# closures (lambdas) cannot access local variables from the enclosing method unless captured correctly. `clientIp` inside `LogAndReturn()` caused build failure — it's only in scope in `Proxy()` / `ProxyNoAcl()`. Always check variable scope in middleware lambda expressions.

---

## WHAT NOT TO DO

### On the APK
- ❌ Do NOT use `workflow_dispatch` to trigger builds — only push-triggered
- ❌ Do NOT bump `MIN_APK_VERSION` in middleware unless intentional forced upgrade
- ❌ Do NOT add `moveTaskToBack(true)` after `startActivity()` in any login flow — use `finish()` instead
- ❌ Do NOT use `marginVertical`/`marginHorizontal` in any layout XML
- ❌ Do NOT reference `@android:drawable/ic_input_add` or other missing Zebra system drawables
- ❌ Do NOT remove `android:entries="@array/ipAddress"` from the Spinner in `activity_ip.xml`
- ❌ Do NOT add `android:windowBackground` to `AppTheme` — only `AppTheme.Launcher`
- ❌ Do NOT call `getAppUpdate()` for xmwgw URLs — those servers have no `/appversion` endpoint

### On the middleware
- ❌ Do NOT push directly to production SAP (`.170`/`.179`) — always dev (`.174`) first
- ❌ Do NOT use `192.168.144.46` for anything
- ❌ Do NOT reference variables in LogAndReturn() that are only in scope in Proxy()
- ❌ Do NOT remove `_deviceSerials` tracking — it's how we count individual HHTs
- ❌ Do NOT remove the `X-HHT-Serial` header reading — it's critical for device counting

### On the dashboard
- ❌ Do NOT convert service-worker syntax to ES module syntax — CF deployment fails
- ❌ Do NOT add `export default {...}` — use `addEventListener("fetch", ...)`
- ❌ Do NOT add new API endpoint calls without checking if it creates excessive load

### On Cloudflare Workers generally
- ❌ Do NOT use `export default` in any CF Worker deployed to this account — all use service-worker format
- ❌ Do NOT add secrets/credentials in Worker code — use environment variables

---

## INFRASTRUCTURE CREDENTIALS

### GitHub
- **Token:** `${GITHUB_TOKEN_SECRET}`
- Repos: `V2-Application/Android_HHT`, `akash0631/v2-hht-middleware`, `akash0631/rfc-api`, `akash0631/v2-data-platform`

### Cloudflare
- **Token:** `${CLOUDFLARE_API_TOKEN_SECRET}`
- **Account ID:** `bab06c93e17ae71cae3c11b4cc40240b`

### Azure Service Principal (`v2-fabric-agent`)
- **Tenant:** `69cb2110...` (v2-fabric-agent) / `3eb968d0...` (claude-mcp-agent)
- **Client ID:** `67b3fbd5` (fabric) / `8f54a771` (mcp)
- **Subscription:** `7c2e7784`
- **Resource Group:** `dab-rg`, Central India

### R2 (Nubo ads bucket)
- **Access Key:** `${R2_ACCESS_KEY_SECRET}`
- **Secret:** `${R2_SECRET_KEY_SECRET}`
- **Endpoint:** `https://33b6cfffad5dd935e73e9061a56f1506.r2.cloudflarestorage.com`
- **Bucket:** `nubo`, CDN: `https://assets.eatnubo.com`

### APK Keystore
- **Password:** `V2Retail@456`
- **Alias:** `v2retail`
- **Key password:** `V2Retail@456`
- **SHA-256 cert:** `2a97d10a78ecddff359ecf11d7e5b33a3e58d5a201caff9449d865ef925c5273`

### DataV2 SQL Server
- **Host:** Server 28 (192.168.x.28), domain `V2RD\akash.agarwal` / `vrl@99999`
- **SQL tunnel:** `sql28.v2retail.net` (CF Tunnel → localhost:9292 on Server 36)
- **Tables:** 1,332 tables, 8B+ rows
- **Key tables:** `DAILY_SALE_PROCESS_DATA` (1.2B rows), `STORE_STOCK_SALE_DAY_SUMMARY` (500M rows)
- **Standard metric:** `SALE_V` = net ex-GST. Never add `TAX_V`.

### SAP Direct
- **HANA host:** `192.168.144.185`
- **Dev client:** `210` on `.174`
- **Production client:** `600` on `.170`
- **S4P system ID:** `/02`

---

## SAP RFC REFERENCE (key opcodes)

| Opcode | Purpose |
|---|---|
| `scnrec` | Login / ZWM_USER_AUTHORITY_CHECK |
| `zsdc_direct_art_val_barcod_rfc` | Store: article barcode validation |
| `zsdc_direct_save_rfc` | Store: save data |
| `zsdc_direct_flr_rfc` | Store: floor operations |
| `zwm_rfc_stock_take_arti_vali` | DC: stock take article validation |
| `zwm_tvs_val_external_hu` | DC: TVS HU validation |
| `zwm_cla_hu_validate` | Hub: CLA HU validation |
| `zwm_inv_grc_validation` | DC: GRC validation |
| `zwm_create_hu_and_assign_tvs` | DC: create HU + assign TVS |
| `zwm_huswap` | DC: HU swap for label reprinting |
| `zstore_discount_get_ean_data` | Store: discount article data |
| `getsloc` | Get storage location |
| `zwm_store_get_stock` | Store: get stock |
| `zwm_store_get_hus` | Store: get HUs |
| `zwm_delivery_get_details_plp2` | Delivery details |

*117 total registered opcodes. 138 active (called at least once). Run `/api/hht/stats` for full list.*

---

## STORE CODE CLASSIFICATION

Store codes are SAP WERKS plant codes. Classification rules (used in dashboard and middleware):

```javascript
function storeType(code) {
  if (!code || code === "?") return "unknown";
  var c = code.toUpperCase();
  if (/^(DW|DH)\d+$/.test(c)) return "dc";      // DW01=Dehradun-DC, DH24=Delhi-DC
  if (c === "HUB" || /^DB\d+$/.test(c)) return "hub";  // HUB, DB03=Patna-HUB
  return "store";  // Everything else: HD22, HN28, HB06, HJ10, etc.
}
```

Known DCs: `DW01` (Dehradun), `DH24` (Delhi DC), `DH25` (Delhi Ecomm)
Known Hubs: `HUB`, `HB05`, `DB03` (Patna)

---

## BUILD PIPELINE: STEP BY STEP

### APK Build (after pushing to main)

1. Push commit to `V2-Application/Android_HHT` on `main`
2. `build-apk.yml` triggers on `ubuntu-latest`
3. JDK 17, decodes keystore from `KEYSTORE_BASE64` secret
4. `./gradlew assembleRelease` — ~4-5 minutes
5. Creates `V2_HHT_Azure_Release.apk` artifact + GitHub release
6. Separately, same process for `ui-redesign` → `V2_HHT_UI_Redesign.apk`
7. Download artifact via API: `GET /repos/.../actions/artifacts/{id}/zip`
8. Verify cert SHA-256 matches `2a97d10a78...`
9. Upload to R2: `PUT /r2/buckets/v2retail/objects/V2_HHT_Azure_Release.apk`

### Middleware Deploy (after pushing to master)

1. Push commit to `akash0631/v2-hht-middleware` on `master`
2. `deploy.yml` triggers on `windows-latest`
3. MSBuild, NuGet restore (~3-4 minutes)
4. Zips output including SAP NCo DLLs, ASP.NET DLLs
5. Azure App Service Deploy action pushes to `v2-hht-api.azurewebsites.net`
6. Azure restarts, in-memory state resets
7. Check: `GET https://v2-hht-api.azurewebsites.net/api/hht/health` — wait for `java=ok`

### Monitoring build status

```python
runs = requests.get(
    "https://api.github.com/repos/{REPO}/actions/runs?per_page=5",
    headers=H).json().get("workflow_runs", [])
latest = runs[0]
# status: "queued" | "in_progress" | "completed"
# conclusion: "success" | "failure" | "cancelled" | None
```

If build fails, get error:
```python
jobs = requests.get(f"https://api.github.com/repos/{REPO}/actions/runs/{run_id}/jobs", headers=H).json()
logs = requests.get(f"https://api.github.com/repos/{REPO}/actions/jobs/{job_id}/logs", headers=H, allow_redirects=True).text
errors = [l for l in logs.split('\n') if 'error CS' in l or '##[error]' in l]
```

---

## V2 RETAIL DATA PLATFORM (separate project)

- **Fabric workspace:** `af8b3ecb` (V2Retail_Gold)
- **Gold Lakehouse ID:** `e26a289a`
- **SP:** `v2-fabric-agent` (Client `67b3fbd5`, Tenant `69cb2110`)
- **Data flow:** SAP → Server 28/DataV2 SQL → Fabric Gold Lakehouse → Power BI + SRM + apps
- **Grains:** Store×GenericColor×Week and National×Variant×Week for sales; month-end+latest for stock
- **NEVER** load raw tables (`DAILY_SALE_PROCESS_DATA` 1.2B rows) into Fabric — only pre-aggregated views from Server 28
- **RFC API:** `sap-api.v2retail.net` — FOR SAP RFC CALLS ONLY, not SQL queries
- **SQL API:** `sql.v2retail.net` → CF Worker → Claude → T-SQL → Server 28 (pending `CLAUDE_API_KEY` in CF Worker settings)

---

## NUBO (separate project — eatnubo.com)

Nubo is a healthy QSR restaurant in Hauz Khas, Delhi. Completely separate from V2 Retail infra except:
- Meta Ads managed via CF Worker `nubo-ads-bot.akash-bab.workers.dev` — **NEVER use Pipeboard**
- Account ID: `act_1373558921238082`
- Ad creative assets: R2 bucket `nubo`, folder `creatives/shashank/`
- Google Ads customer ID: `1127675609`
- Nubo is pickup/takeaway only — NEVER optimize for web purchases or delivery

---

## RESPONSE FORMAT FOR DEVELOPERS

When a developer asks you to do something:

1. **State what you're about to do** in one line
2. **Do it** — read files, make changes, push, monitor builds, verify
3. **Report result** — what changed, what file, what build #, what URL to test
4. **Flag anything risky** before doing it, not after

If something could break production (500+ devices), say so explicitly and propose a safer path (test on `ui-redesign` branch first, for example).

When a build fails, read the actual error log, fix the root cause, push again. Don't ask the user to check — do it yourself.

---

*This document covers the state of the system as of March 31, 2026. The stack is actively developed — if something here contradicts what you find in the code, trust the code.*
