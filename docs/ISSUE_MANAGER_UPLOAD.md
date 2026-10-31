# 上傳到 Issue Manager（Revit 外掛端，任務書 V4 的 4B）

> 文件性質：人類與 AI 共用的功能說明與合約摘要（繁中）。日期：2026-10-25，分支 `v4b`。
> 網站端（任務書 4A：版本包 API、`bundle-manifest.schema.json` 正式版）已在 Issue Manager 的 `imv4a` 分支實作；本檔描述外掛端依 Issue Manager repo 的規格（`docs/spec/revit-addin-export.md` §5／§5.2、`docs/spec/addin-auth.md`、`docs/spec/sheet-sets.md`）寫成的客戶端，**2026-10-25 已依網站端實際合約對齊**（見交班單 §9 與本檔 §8）。**全部只做到建置與離線測試（含對假伺服器的真實 HTTP 測試），沒有在 Revit 內實跑，也沒有連過真實的 Issue Manager**；實機項目見 [commit_handoff_v4b_2026-10-25.md](commit_handoff_v4b_2026-10-25.md) 的清單。
> 決策與取捨見 [ADR D-011](adr/D-011-issue-manager-upload-addin.md)。

## 1. 它做什麼

在 Revit 功能區「Issue Manager」面板按「上傳到 Issue Manager」→ 勾選視窗 → 一次匯出（圖紙 PDF、3D 模型 GLB、3D 視角）→ 打成版本包（資料夾＋`manifest.json`）→ 背景上傳 → 網站出現**一個**工作版（W）。

- 不經 MCP WebSocket：以 `IExternalCommand` 啟動、匯出經 `ExternalEventManager` 佇列在 Revit API 執行緒執行，**MCP 服務關閉時也能用**。匯出本身仍呼叫既有指令（`export_sheets_pdf`、`list_design_options`、`export_design_options`、`export_named_views`），經 `CommandExecutor.ExecuteCommand` 分派，沒有另寫匯出碼。
- 不新增 MCP 工具（工具數維持 137）、不新增 domain、不新增 skill。
- **2026-11-01（分支 `imrevitaddin`，D-012）**：勾選含平面／天花／剖面／立面時自動多匯出 `view_positions.json`（既有 `export_view_positions`）；視窗新增「匯出參數與面積表」勾選，多匯出 `params.json`（`RevitParamsReader` 讀 Revit、`ParamsFileBuilder` 組 JSON）；finalize 回的 `version` 顯示在完成畫面。舊網站沒有對應上傳網址時事先略過並提醒。實機自測見下方「實機自測鉤子」。

## 1.5 實機自測鉤子（開發／驗收用，opt-in）

建立 `%USERPROFILE%\.revit-mcp\im-selftest.json`（外掛不會自己建立；跑過一次即改名 `.done`）後（Revit 已開著也可以，外掛每 3 秒看一次）會用標記裡的短效 refresh 權杖（記憶體內，不碰 DPAPI 的正式登入檔）跑一次與按鈕相同的 `UploadFlow`，結果寫到 `resultPath`。欄位：`baseUrl`、`refreshToken`、`userId`、`email`、`projectId`、`stage`、`description`、`openPath`（可省略，要開的 .rvt）、`maxSheets`（預設 6）、`includeModel`／`includeParams`（預設 true）、`glbOverride`（可省略：指定先前匯出的主模型 GLB 取代即時匯出，結果與警告會註明）、`evidenceDir`、`resultPath`。權杖是一次性的（refresh 輪替），每次執行都要重新發一個。**標記內容含權杖，用完請刪 `.done`。**

## 2. 設定（第一次使用）

網址沒有預設值（寧可顯示「尚未設定」，也不猜一個網址把權杖送出去）。擇一：

1. 環境變數 `ISSUE_MANAGER_URL=https://…`
2. `%APPDATA%\RevitMCP\issue-manager.json`：`{"baseUrl": "https://…"}`

只接受 `https`；`http` 僅限本機（`127.0.0.1`／`localhost`，給假伺服器試跑）。

含設計選項的模型需要系統 **Node.js** 與已部署的 `merge-option-glbs.mjs`（`scripts/install-addon.ps1` 會複製到 `%APPDATA%\RevitMCP\bridge\node\bundle\`）。沒有設計選項的專案不需要 Node。

- **找 Node 只用絕對路徑（審查後修正）**：依序是標準安裝位置（`Program Files\nodejs\node.exe` 等）、PATH 內的絕對目錄；不使用裸名 `node`（`UseShellExecute=false` 時 Windows 會先找目前工作目錄，專案資料夾放一個 `node.exe` 就會被執行）。目前工作目錄與開啟中專案所在資料夾之下的 `node.exe` 一律略過。
- **開發旗標**：往上層目錄找 `tools\merge-option-glbs.mjs` 的開發用路徑，正式安裝版預設關閉；開發時設環境變數 `REVITMCP_DEV_TOOLS=1` 才啟用。正式版只找 dll 同層與 `%APPDATA%\RevitMCP\bridge\node\bundle\`。

## 3. 勾選視窗

| 欄位 | 規則 |
|---|---|
| 登入列 | 顯示 email（角色）；登入／登出／切換帳號 |
| 專案 | 登入後從 `GET /api/addin/projects` 載入（只含可上傳工作版的專案） |
| 設計階段 | 第一項「請選擇階段」，**沒有預設值**，不可送出；其後 PD／SD／DD |
| 版本說明 | 必填 |
| 圖紙集 | Revit 內建 `ViewSheetSet` 名稱＋「（自訂）」。選一個＝取代目前勾選（集內圖紙＋其視口視圖＋集內單獨的視圖）；手動增減後自動回「（自訂）」 |
| 圖紙清單 | 樹狀：圖紙（圖紙號＋圖名，依圖號自然排序）→ 視口視圖。勾圖紙連視口一起勾，之後可單獨取消某視口。開視窗只讀名稱與對應；視口外框（用來決定主要視口）延後到匯出時才分批量測（見交班單 §10） |
| 其他視圖 | 不在任何圖紙上的視圖，依種類分組（3D／平面／天花／立面／剖面／詳圖） |
| 模型 | 「匯出 3D 模型（GLB）」預設勾；「含設計選項」預設勾 |
| 摘要 | 「將上傳 N 張圖紙、M 個視圖、K 個模型」；沒圖紙或沒模型時提示「這個版本將缺圖／缺模型」（允許送出） |

「上傳」啟用條件：已登入、專案、階段（非預設列）、說明、**至少勾一張圖紙或一個模型**（只勾視圖會被伺服器拒絕：finalize 回 400 `invalid_manifest`，視圖只是圖紙／模型的附屬資料，所以按鈕停用並說明原因）、圖紙 ≤ 1,000 張、視圖 ≤ 5,000 個（網站端 schema 上限）。滑鼠移到停用的按鈕上會顯示原因。

不列入的視圖：樣板、圖例、明細表、製圖視圖、漫遊、渲染（它們不對應模型位置）。

## 4. 匯出與上傳流程

1. `POST /api/addin/v1/bundles`（專案、階段、說明）— 先建立，階段／說明有問題就不必等長時間匯出。
2. 匯出 PDF（`export_sheets_pdf`，合併單檔 `sheets.pdf`，頁序＝manifest 的 `pageNumber`＝圖號自然排序）。
3. 匯出 GLB（`export_design_options`；有選項分件時以 `merge-option-glbs.mjs` 合併成 `model.glb`）。
4. 匯出 `named_views.json`（`export_named_views`，原點與 GLB 相同）。
5. 產生 `manifest.json`（檔案 sha256、圖紙號取自 Revit）。
6. 背景分段 PUT 到簽名網址（可重試、不重傳已完成的檔案）。分段大小**以建立回應的 `partBytes` 為準**（網站端磁碟模式目前 ≤ 8 MiB、Supabase 模式 45 MB，客戶端不寫死；只擋明顯不合理的值，> 128 MB）。磁碟模式（上傳網址在 Issue Manager 自己的主機）每段另帶 `X-Part-Sha256`，網站端不符就在該段回 400 `checksum_mismatch`；簽名網址不加額外標頭。上傳前先用回應的 `limits`（PDF／GLB／命名視圖檔大小、PDF 頁數）檢查，超過就不傳（並 `DELETE` 該版本包）。
7. `POST …/finalize`（冪等）→ 顯示版本連結。見下面「finalize 的處理」。

步驟 2～5 的**每個 Revit 步驟各自是一次 ExternalEvent**（文件資訊、PDF、GLB、命名視圖各一次，經 `QueuedApiThreadRunner` 排進與 MCP 指令共用的佇列）；步驟之間回到 UI 執行緒更新進度、檢查取消旗標，不再在 API 呼叫中途跑巢狀訊息迴圈。檔案雜湊、manifest 等不需要 Revit API 的工作在背景執行緒，不佔用 Revit。**GLB 匯出是單一長呼叫，無法中途取消**（CPC 案實測 631～1,175 秒）。步驟 6、7 在背景執行緒。

- **排隊等待可取消**：還在佇列裡、尚未開始的工作，取消或逾時時標成「已放棄」，之後即使 Revit 才處理佇列也不會執行；已開始的工作會做完。
- **`ExternalEvent.Raise()` 被拒絕**（回傳不是 `Accepted`／`Pending`）→ 立刻以白話訊息失敗，不會永遠等。
- **開始執行前的等待逾時 120 秒**（外部事件一直沒被處理，例如 Revit 停在對話框）→「Revit 目前沒有回應，請關掉 Revit 裡的對話框後重試」。逾時只算「開始前」，開始後的長呼叫不受限。
- 任何結束路徑（成功、取消、逾時、例外）都在 `finally` 釋放「同一時間只允許一個上傳」的旗標，不需要重開 Revit。

- 取消：呼叫 `DELETE /bundles/:id`、刪本機暫存、不再上傳。**finalize 之後不能取消**：伺服器對已 finalize 的 bundle 回 409 `already_finalized`（版本已建立，什麼都不刪）、finalize 進行中回 409 `finalize_in_progress`。客戶端據此顯示「已經上傳完成，無法取消；請到 Issue Manager 網站刪除該版本。」（或「伺服器正在建立版本，已無法取消…」），不假裝取消成功。
- 失敗：顯示白話原因；若匯出已完成，視窗出現「重試上傳」（從未完成的檔案／分段繼續）。
- **未完成的 bundle 不佔名額（2026-10-25，v4b3）**：網站端每人最多 5 個「未完成」bundle、24 小時才過期。以下情況外掛會對舊 bundle **盡力送一次 `DELETE`**（15 秒逾時；失敗不阻擋流程、不顯示給使用者，日誌只記一行不含編號的文字）：①匯出失敗（PDF／GLB 失敗、Revit 無回應 120 秒逾時）；②500 `finalize_failed` 後重建新包之前；③簽名網址失效而重建之前；④新的上傳取代了待重試的舊 bundle。`DELETE` 若回 409 `already_finalized`／`finalize_in_progress`（其實已建立／正在建立版本），**不重建**，改對同一個 bundle 再問一次 finalize 取回結果。仍遇到 429 `too_many_open_bundles` 時訊息說明「24 小時內會自動清除，請稍後再試；若急需請通知管理員」。目前網站端**沒有**列出或清除自己未完成 bundle 的端點，所以視窗沒有「清除我的未完成上傳」按鈕（待網站端補列表端點再做）。
- **上傳分段被拒也會重建（v4b3）**：分段 PUT 回 410 `bundle_expired`、409 `bundle_failed`／`bundle_closed`、404（已被清除），或簽名網址所在主機（Supabase，簽名約 2 小時失效）回 4xx（408／429 與 5xx 除外，仍照原本重試），都視為這個 bundle 不能用 → 同一次上傳內**自動重建一次**（先確認本機檔案沒被改動、清掉上傳進度、`POST /bundles`、整批重傳）；新 bundle 也被拒才把錯誤交給使用者，之後按「重試上傳」會再重建。分段本身的 400（`checksum_mismatch`、`invalid_part`）只代表該分段要重送，仍續傳同一個 bundle。是否過期**一律以伺服器回應為準**，不用本機時鐘判斷。
- **`bundle_closed` 不直接當作廢（v4b3）**：第一個 finalize 等滿 10 分鐘、使用者按重試而讓伺服器重新認領時，第二個 finalize 可能在第一個提交後收到 409 `bundle_closed`。外掛收到後先對**同一個 bundle** 再送一次 finalize（冪等，已完成回同一個 `setId`），成功就顯示完成、不重建；仍失敗才視為作廢重建，避免多出第二個工作版本。
- **finalize 的處理（2026-10-25 對齊網站端）**：
  - **驗證失敗即作廢**：伺服器對 400／413 立即把 bundle 標成 `failed` 並清暫存，之後再送 finalize 一律 409 `bundle_failed`；500 `finalize_failed`、410 過期、404 同樣表示這個 bundle 不能用了。客戶端**不再對舊 bundle 重送 finalize**：按「重試上傳」時重新 `POST /bundles`（用已匯出的本機檔案，先確認檔案沒被改動，清掉舊的上傳進度）、整批重傳、再 finalize，**不重新匯出**。版本包過期（到期時間已過）也是同樣處理。
  - **內容本身被拒**（`invalid_manifest`、`page_count_mismatch`、`invalid_pdf`、413）：同樣的檔案重傳只會再被拒，所以直接放棄本機暫存、不提供重試，請使用者調整勾選後重新上傳。
  - **逾時與處理中**：finalize 專用 `HttpClient` 逾時 **330 秒**（伺服器最長約 300 秒，磁碟模式更久；其餘 JSON 呼叫維持 60 秒、上傳分段 15 分鐘）。等不到回應（逾時）、502／504、或 409 `finalize_in_progress` → 視窗顯示「伺服器處理中…」，每 5 秒重送 finalize（伺服器端冪等：做完了就回同一個 `setId`），**上限 10 分鐘**；超過就停止等待並請使用者稍後到網站確認，「重試上傳」仍可用。網站端沒有 `GET /bundles/:id` 狀態端點，所以用重送 finalize 代替查詢。連線被拒／中斷（不是逾時）不輪詢，直接報錯並保留 bundle 供重試。
- 同一時間只允許一個進行中的上傳。
- **關閉視窗（含右上角 X）**：進行中 → 送出取消，流程自己清伺服器與本機暫存；閒置但有可重試的 bundle → 詢問「放棄這次上傳並清除？」，同意就先刪本機 PDF／GLB 暫存、再盡力通知伺服器 `DELETE`（伺服器連不上也不會留下本機檔），選「否」回到視窗可繼續重試。
- **舊暫存清理**：外掛啟動時背景刪除 `%LOCALAPPDATA%\RevitMCP\issue-manager\bundles\` 下超過 3 天（常數 `BundleTempCleaner.DefaultMaxAgeDays`）沒有任何檔案更新的資料夾；清不掉的（被占用）下次再清，不影響啟動。
- **PDF 頁數檢查**：以串流掃描（每次 1 MB）估計 PDF 頁數；頁數與勾選圖紙數不符 → **中止**（避免網站上圖紙與頁面對錯）。例外：頁數少於圖紙數、且 PDF 含物件串流（`/ObjStm`，頁面物件可能被壓縮而漏數）時估計不可靠，只警告。
- **續傳前驗證**：重試（狀態檔已存在）時重新計算磁碟上每個要送的檔案的 sha256；檔案被改動或損毀 → 拒絕續傳並請使用者放棄後重新匯出（第一次上傳不重算，避免大 GLB 多花時間）。
- 「含設計選項」未勾：只匯出主模型與各選項集合的**主要選項**（`isPrimary`），不含其他選項（這是本案的解讀，見 ADR D-011）。
- 匯出報告 `complete:false`（任何分件失敗／預算截斷）→ 整個模型步驟中止並指出報告路徑，**不上傳缺漏的模型**（LESSONS L-15）。

## 5. 登入（瀏覽器授權碼＋PKCE＋loopback）

依 `addin-auth.md`：產生 `state`、`code_verifier`（32 位元組亂數）、`code_challenge`（S256）→ 在 `127.0.0.1` 隨機埠開監聽 → 開預設瀏覽器到 `/addin/authorize?state&code_challenge&code_challenge_method=S256&port&client_version&client_name` → 使用者在網站按「授權」→ 302 回 `http://127.0.0.1:埠/callback?code&state` → 比對 state → `POST /api/addin/token`（`authorization_code`＋`code_verifier`）→ 取得 access／refresh。

- loopback 用 `TcpListener(IPAddress.Loopback, 0)`（不需 URL ACL），並設 `ExclusiveAddressUse`（別的程式不能用 `SO_REUSEADDR` 搶綁同一個埠攔截授權碼）；單一連線讀請求標頭上限 5 秒（以 `Task.WhenAny`／`Task.Delay` 實作，因為 .NET Framework 4.8 的 `NetworkStream.ReadAsync` 不理會取消）；只接受 `GET /callback`、Host 必須是 `127.0.0.1:埠`、state 固定時間比對；不符回 400／404 並**繼續等**（本機其他程式亂打不會讓正確回呼失敗）；120 秒逾時自動關閉。
- refresh token 以 **Windows DPAPI（使用者層級，P/Invoke crypt32）** 加密寫入 `%LOCALAPPDATA%\RevitMCP\issue-manager\auth.bin`（連 email 也不明文）；access token 只放記憶體。
- refresh 輪替：新 refresh 立刻落檔；refresh 以單一鎖序列化（並行使用同一份 refresh 會被伺服器判成外洩而撤銷整族）；`invalid_grant` → 清本機並要求重新登入；暫時性錯誤（5xx、斷網）不清登入。
- 登出（審查後修正）：先撤銷伺服器端這一族授權，再刪本機檔（不論撤銷成不成功，本機一定刪）。access 尚未過期用 `Authorization: Bearer`；**access 已過期、被伺服器回 401、或重開 Revit 後只剩 DPAPI 檔裡的 refresh** 時，改用 `{"refresh_token": …}` 走同一個 `POST /api/addin/token/revoke`（`addin-auth.md` §4.4）。撤銷沒成功時 `LogoutAsync` 回傳 `ServerRevokeFailed`，視窗顯示「伺服器端撤銷未完成」並指引到網站「帳號 › 已授權的外掛」手動撤銷。「切換帳號」走同一條路（舊帳號撤銷失敗時，重新登入完成後仍顯示警告）。
- 權杖、授權碼、verifier、state 不寫日誌、不進例外訊息（單元測試以全部祕密字串搜尋日誌與訊息驗證）。
- Bearer 只送給 Issue Manager 自己的主機；簽名上傳網址若在別的主機，不附 Authorization。上傳網址必須是 https（本機例外）。HttpClient 不自動跟隨轉址。**伺服器若回傳 `http://localhost:<埠>` 之類的本機上傳網址、而 Issue Manager 本身不在本機**（磁碟模式沒設 `ADDIN_PUBLIC_URL` 時伺服器用請求的 origin 組網址，反向代理後可能組出本機位址），客戶端中止並顯示「伺服器回傳的上傳網址不是 Issue Manager 的主機，已中止；請通知管理員設定 ADDIN_PUBLIC_URL。」，一個位元組都不會送到本機。
- **主機比對改為嚴格（v4b3）**：協定、主機名稱、埠都相同才算 Issue Manager 自己的主機；`localhost`、`127.0.0.1`、`[::1]` 彼此視為不同主機，除非 BaseUrl 本身就是那個名稱（否則另一個迴圈名稱上的程式就能收到 Bearer）。上傳網址長得像 Issue Manager 自己接收分段的路徑（`/api/addin/v1/bundles/:id/parts/:kind/:index`）、主機或埠卻和 BaseUrl 不同（管理員把 `ADDIN_PUBLIC_URL` 設成別的主機／埠）時，外掛一個位元組都不送，顯示「上傳網址與 Issue Manager 主機不同，請通知管理員檢查 ADDIN_PUBLIC_URL。」（原本會不帶權杖也不帶 `X-Addin-Api` 而收到 426，被誤認成「請更新外掛」）；別的主機回 426 時也是這個訊息。伺服器自己給的未知錯誤代碼顯示前先過濾（只留英數底線點橫線、最多 120 字元），不再原樣貼最長 8 KB 的字串。

## 6. 檔案與程式位置

| 路徑 | 內容 | 依賴 Revit API |
|---|---|---|
| `MCP/Core/IssueManager/Pkce.cs` | PKCE、state、固定時間比對 | 否 |
| `…/LoopbackCallbackListener.cs` | loopback 接收器 | 否 |
| `…/TokenVault.cs` | `ITokenVault`、DPAPI 檔案保存 | 否 |
| `…/AddinAuthService.cs`、`AddinApiClient.cs`、`AddinHttp.cs`、`AddinModels.cs`、`IssueManagerSettings.cs` | 登入、API 客戶端、錯誤轉換、設定 | 否 |
| `…/SelectionModels.cs`、`ExportSelectionViewModel.cs`、`Strings.cs` | 勾選視窗的資料模型與檢視模型（zh-TW 字串集中） | 否 |
| `…/BundleManifest.cs`、`BundleExporter.cs`、`BundleUploader.cs`、`UploadFlow.cs`、`GlbMergeRunner.cs` | manifest 產生、匯出流程、上傳器、整體流程 | 否 |
| `…/ApiThreadRunners.cs`、`BundleTempCleaner.cs` | 排隊式 API 執行緒執行器（取消、Raise 被拒、開始前逾時）、舊暫存清理 | 否 |
| `…/UploadWindow.cs` | WPF 勾選視窗（程式碼建構，不引用 Revit API） | 否 |
| `…/RevitSelectionCollector.cs`、`RevitBundleExportSteps.cs`、`IssueManagerSession.cs` | 讀 Revit 清單、經分派器匯出、工作階段 | **是** |
| `MCP/Commands/UploadToIssueManagerCommand.cs`、`MCP/Application.cs` | 功能區按鈕與指令 | 是 |
| `docs/schemas/bundle-manifest.schema.json`、`docs/schemas/fixtures/bundle-manifest.sample.json` | manifest schema（與網站端權威檔逐欄一致，只有 `$id`／`description` 不同）與共用 fixture | — |
| `docs/schemas/web-authoritative/bundle-manifest.schema.json` | 網站端權威 schema 的原樣副本（比對用；更新時與上一列一起換） | — |
| `tools/fake-issue-manager/server.mjs` | 假 Issue Manager（Node，無相依；行為對齊網站端 §5.2） | — |
| `MCP.Tests/IssueManager/` | 約 230 個 NUnit 測試（IssueManager 命名空間 231 通過、2 略過）（含 `ServerContractAlignmentTests`：對齊網站端合約） | — |
| `tools/*.test.mjs`（`bundle-manifest.schema.test.mjs`、`fake-issue-manager.test.mjs`） | ajv 驗 schema／fixture 與網站端上限、schema 差異清單；假伺服器合約 | — |

## 7. 假伺服器與試跑

```powershell
node tools/fake-issue-manager/server.mjs --auto-approve --part-bytes 65536   # 印出網址
$env:ISSUE_MANAGER_URL = "http://127.0.0.1:<印出的埠>"                          # 再啟動 Revit
```

不加 `--auto-approve` 時授權頁會顯示「授權／取消」按鈕（手動走一遍瀏覽器流程）。`GET /__state` 看 bundle（含狀態 open／finalizing／finalized／failed／deleted／expired）、刪除紀錄、PUT 與 finalize 次數；`POST /__config` 可調：`{"failNextPuts":2}`（接下來的 PUT 回 503）、`{"finalizeDelayMs":20000}`（finalize 慢慢做，用來看「伺服器處理中」）、`{"failNextFinalize":1}`（下一次 finalize 回 500 並作廢 bundle）、`{"expireOpenBundles":true}`（讓開著的 bundle 過期，之後 410）、`{"maxOpenBundles":1}`（回 429）、`{"uploadOrigin":"http://localhost:9"}`（上傳網址改成本機位址，看「不是 Issue Manager 的主機」訊息）。資料只存在記憶體。

## 8. 與網站端合約的對齊（2026-10-25 已做）

網站端 `imv4a` 的實際行為與原先依規格寫的客戶端有 16 項差異（網站端 `docs/plans/evidence/imv4a/handoff.md` §4），本分支已逐項處理，細節與測試對照在交班單 §9。重點：

1. **manifest schema**：`docs/schemas/bundle-manifest.schema.json` 改成與網站端權威檔**逐欄一致**（只有 `$id`／`description` 不同）：`warnings` ≤ 200 項（每項 ≤ 500 字）、`sheets` ≤ 1,000、單張圖紙視口 ≤ 200、`views` ≤ 5,000、各名稱字數上限、檔名須以 .pdf／.glb／.json 結尾且不含路徑、`bytes` 上限（PDF 300 MiB、GLB 500 MiB、JSON 4 MiB）與下限（GLB 20、JSON 2）、`originOffsetMeters` 絕對值 ≤ 1e9。`ManifestBuilder` 依這些上限處理：**顯示用文字截斷**（圖名、視圖名、警告；警告超過 200 項時收成一項「另有 N 則警告已省略」，不讓整個版本被拒），**不能截斷的**（圖紙號／識別碼過長、圖紙或視圖數超過上限、原點過大）在匯出前就以白話訊息失敗或讓「上傳」鈕停用，不等匯出完才發現。
2. **差異清單測試**：`docs/schemas/web-authoritative/` 放權威檔的原樣副本；`tools/bundle-manifest.schema.test.mjs` 與 C# `ClientSchemaDiffersFromTheWebsiteAuthoritativeCopy…` 比對兩份 schema，差異清單必須只有 `$id`、`description`；Node 測試另在本機找得到網站端工作樹時（或設 `IM_WEB_SCHEMA`）比對原樣副本與網站端現況（找不到就略過，不算通過）。**網站端 schema 更新時**：把新檔複製成 `web-authoritative/` 副本，再用 `$id`／`description` 以外的內容覆寫客戶端 schema，跑兩邊測試。
3. **API 行為**：建立成功 201（也接受 200）；階段錯誤碼 `invalid_input`（`field:"stage"`）；`files[].kind` 只有 `pdf`／`glb`／`namedViews`，`ManifestBuilder` 不輸出 `viewPositions`；回應多的 `limits` 已讀取使用；錯誤碼的白話訊息涵蓋 `bundle_failed`、`bundle_expired`（410）、`finalize_in_progress`、`already_finalized`、`bundle_closed`、`page_count_mismatch`、`invalid_pdf`、`too_many_open_bundles`（429）、`server_misconfigured`、`finalize_failed`、`invalid_manifest`、`client_too_old`／426、`uploads_paused`。
4. 原先列為「合約缺口」的兩項：簽名網址數量（R5）網站端已解（依「大小上限 ÷ 分段大小」給足）；合併 PDF 的頁序保證仍待實機（交班單 R1／T5）。

## 9. 已知限制

- 全部未在 Revit 內實跑；視窗行為（每步一次 ExternalEvent 的實際表現、取消、**步驟之間** Revit 主視窗可被操作時的影響、單一長步驟期間視窗是否顯示「沒有回應」）、PDF 頁序、設計選項匯出完整性都待實機（**需實機觀察**，見交班單「審查後修正」）。
- 步驟之間 Revit 可以操作：使用者若在匯出中途編輯模型，PDF 與 GLB 可能對應不同的模型狀態（程式只檢查作用中文件沒有換掉）。視窗提示「請勿切換文件、編輯模型或關閉專案」。這是拆步驟（取代巢狀訊息迴圈）的已知取捨。
- 只有 Release.R24 驗證可建置（0 錯誤、警告數與基線相同）。其他年份的建置失敗來自既有檔案（R25／R26：`RevitGltfExportContext.cs` 缺 `System.Drawing.Common`；R22：`WebCompareSection.cs` 用了 2022 沒有的 API），新增檔案在這些目標上沒有新增錯誤。
- 視窗字串目前只有 zh-TW。
- 圖紙上的圖例／明細表視口不列入（不對應模型位置）。
