# Revit 跳轉（外掛端）：網頁 ↔ Revit 元件／視圖／圖紙

> 文件性質：功能規格（人類與 AI 共用）。建立日期：2026-11-18。分支 `imrevitaddinjump`。決策見 [ADR D-015（Revit 跳轉）](adr/D-015-revit-link-addin.md)。
> 來源：Issue Manager（IM）repo 的 `docs/spec/revit-link.md`（§15 通道與指令、§16 識別碼、§17 觸發位置、§18 反向流程、§19 錯誤狀態、§20 安全性、§21 可編輯設定、§24 驗收、§27 實作版合約），以及 IM 端同分支 `src/lib/revit-protocol.ts`（協定常數與解析函式，格式只在那個檔案）。
> 本檔只寫**外掛端**要做的事；網頁端、資料庫、API 都在 IM repo，本 repo 不實作。
> 與 IM 規格衝突時，欄位與指令名稱以 IM 的 `revit-protocol.ts` 為準，行為以本檔為準（本檔補了 IM 草稿沒寫到的 Revit 版本差異與按鈕啟用條件）。

## 補記（2026-11-18，來源 Alex）：六題答案

Alex 2026-11-18 對 IM 規格 §26 的六題給了答案，外掛端照做、不再問：

| 題 | 答案 | 外掛端要做的事 |
|---|---|---|
| Q1 反向觸發 | 功能區按鈕與 Claude 指令兩個都做 | 兩個入口共用同一段 `OpenIssueDraft(kind)`；功能區兩顆按鈕＋兩個 MCP 工具 |
| Q2 元素查議題 | 做：在 Revit 選取元件（例如牆 W-12）可看到網頁上的相關議題 | `kind="lookup"` 開 `/revit/lookup?code=…` |
| Q3 模型版本不同 | 每次跳轉都先警告、仍然跳轉 | `viewsync_open_link` 回 `modelRevisionDiffers`；`revisionPolicy` 預設 `continue` |
| Q4 專案換檔批次改連結 | 要（網頁端功能） | 外掛不需做；`model.guid` 在別名對應時由網頁端處理 |
| Q5 外部可見度 | imperms 前由專案設定控制，預設看不到；外部永遠看不到識別碼、不能跳轉 | 外掛不分身分（外掛只服務已配對的內部瀏覽器）；識別碼只經本機傳輸 |
| Q6 IFC 跳轉 | 排進本期（IFC 換算修正另由 imifcfloorfix 處理，本件不修 IFC 對應） | 外掛不需懂 IFC：網頁把 IFC 換算成 UniqueId 後，照一般元素連結送進 `viewsync_open_link` |

## 1. 外掛新增的指令

### 1.1 瀏覽器（帶 Origin、已配對 token）可呼叫的唯讀指令

全部唯讀：**不改模型、不建立視圖、不套用暫時設定、不動相機以外的任何狀態、不動「已修改」旗標**。唯一的「副作用」是切換使用中視圖與設定選取（這是 UI 狀態，不是模型修改）。

| 指令 | 輸入 | 輸出 | 錯誤碼 |
|---|---|---|---|
| `viewsync_open_link` | `{ model:{title,guid?,revision?}, link:{elementUniqueIds[], elementIds[]?, viewUniqueId?, sheetNumber?}, allowFallback, revisionPolicy:"continue"｜"block"｜"sheet_only" }` | `{ level:"element"｜"view"｜"sheet"｜"none", found[], missing[], hintUsed, view:{uniqueId,name}, selectedCount, modelRevision?, modelRevisionDiffers?, warnings[] }` | `model_mismatch`、`model_revision_differs`（只在 `revisionPolicy=block`）、`activate_failed`、`no_document`、`not_project_document` |
| `viewsync_get_selection` | `{}` | `{ model:{title,guid,path?,revision}, view:{uniqueId,name,type,sheetNumber?}, elements:[{uniqueId,elementId,category,name,level?}] }`（最多 200 個元素） | `selection_empty`、`no_document` |
| `viewsync_capture_view` | `{ maxLongEdgePx?: 400–4000，預設 2000 }` | `{ mime:"image/png", dataBase64, width, height }`；超過 5 MB 自動降解析度重出 | `capture_failed` |
| `viewsync_draft_take` | `{ code }` | 草稿全文（含 `intent:"create"｜"lookup"`、`model`、`view`、`elements`、`image?`）；**取一次就刪** | `draft_expired`（超過保存分鐘數）、`draft_not_found` |

- `model.revision`＝`"<NumberOfSaves>:<VersionGUID>"`（取自 `Document.GetDocumentVersion`，每次存檔都會變），**只比對相同與否**。取不到時不回版次，網頁當作「無法比對」，不顯示警告。
- `viewsync_status` 的 `model` 物件新增 `revision`（讓網頁連線檢查時就能顯示「存檔 #N」）。
- 元素找不到的退化順序固定：**元素 UniqueId → 元素 ElementId（後備提示，命中時 `hintUsed=true`）→ 視圖 → 圖紙 → `none`**。`allowFallback=false` 時不退到視圖／圖紙。
- 元素在目標視圖看不到（被隱藏、被視圖範圍切掉）時，改開看得到它的樓層平面或 3D 視圖，並在 `warnings` 回 `hidden_in_view`。
- `ElementId` 在 Revit 2024 是 32 位元（`.IntegerValue`）、2025 起是 64 位元（`.Value`）：一律經 `RevitCompatibility` 的相容 helper 轉換，同一份原始碼要能同時編譯 R24 與 R25。

### 1.2 只供外掛內部（不對瀏覽器開放）

| 指令／入口 | 說明 |
|---|---|
| `viewsync_draft_put` | 暫存草稿：只放記憶體、取一次就刪、預設 10 分鐘過期（`draftMinutes` 設定，1–60）、不寫硬碟；一次性代碼 ≥128 位元隨機。瀏覽器送來一律 `command_not_allowed`。不在 `CommandExecutor` 的 switch，只由外掛內部直接呼叫。 |
| `OpenIssueDraft(kind)` | `kind="create"` 或 `"lookup"`：讀目前選取（＋`create` 時截目前視圖）→ 暫存草稿 → 以預設瀏覽器開 `{IM}/revit/draft?code=…` 或 `{IM}/revit/lookup?code=…`。**功能區按鈕與 Claude 指令共用這一個入口**（Q1）。 |

### 1.3 Claude／MCP 指令（Q1）

| MCP 工具 | 對應 | 回傳 |
|---|---|---|
| `open_issue_draft` | `OpenIssueDraft("create")` | `{ opened, kind, url, elementCount, pairedWebDevices }` |
| `open_element_issues` | `OpenIssueDraft("lookup")` | `{ opened, kind, url, elementCount, pairedWebDevices }` |

兩者都需要 Revit 已開專案且**至少選了一個元素**（沒選回 `selection_empty`）。回傳的 `url` **刻意不含一次性代碼**（代碼只交給瀏覽器，不進 Claude 對話與日誌）；`pairedWebDevices` 為 0 代表還沒有網頁配對，網頁會領不到草稿。（2026-11-18 實作時補：IM 草稿原寫 `{ opened, url }` 含代碼，改為不含，是為了不讓一次性代碼留在對話紀錄。）MCP 工具名稱用 snake_case；MCP-Server 端各加一個工具定義，並補上註解（`title`、`readOnlyHint`）。

## 2. 功能區按鈕位置

放在既有的 **「Issue Manager」** 面板（與「上傳到 Issue Manager」並排，`MCP/Application.cs`）：

| 按鈕（預設文字） | 做什麼 | 啟用條件 |
|---|---|---|
| 在 IM 建立議題 | `OpenIssueDraft("create")` | 有開啟的專案文件、有選取元素 |
| 這個元素有哪些議題 | `OpenIssueDraft("lookup")` | 同上 |

- 兩顆按鈕用 `IExternalCommand` 實作，**不經 MCP WebSocket 排隊就直接執行**，但網頁領草稿要靠 WebSocket：所以按下時若 **MCP 服務沒開** 或 **還沒有任何已配對的網頁裝置**，不開瀏覽器，改跳說明對話框（「請先按『MCP 服務(開/關)』啟動，再按『網頁配對』」）。這對應 IM 規格 §17「外掛已配對」的條件。
- 沒有選取元素時跳提示「請先在 Revit 選取至少一個元素」，不開瀏覽器。
- 按鈕文字、提示是字串常數集中在 `RevitLinkStrings`；使用者可編輯的值見第 4 節。

## 3. 安全與唯讀限制（不得放寬）

1. 瀏覽器只能呼叫第 1.1 節的四個新指令＋既有的 `viewsync_pair`／`viewsync_status`／`viewsync_open_view`；其他現有 MCP 指令一律 `command_not_allowed`（縱深防禦那一行不動）。
2. `viewsync_draft_put` **不在瀏覽器白名單**；單元測試保證。
3. 本功能（四個指令與 `OpenIssueDraft`）的原始碼**不得**呼叫：建立視圖（`viewsync_create_view`）、套用暫時設定（`viewsync_apply_temp`）、任何 `Transaction`、`TransactionGroup`、刪除或修改元素的 API。單元測試以掃描原始碼方式保證（見第 6 節）。
4. 草稿只在記憶體，不寫硬碟、不寫日誌；日誌只記「建立了一份草稿、元素數 N」，**不記識別碼、不記代碼、不記截圖內容**。
5. IM 網址只允許 `https`（本機測試才允許 `http://127.0.0.1`／`localhost`），沿用 `IssueManagerSettings.NormalizeBaseUrl`。

## 4. 使用者可編輯的設定（ALEX 規則）

外掛端設定檔 `%APPDATA%\RevitMCP\revit-link.json`（沒有檔案＝全用預設；檔案壞掉＝用預設並在日誌記一行）：

| 欄位 | 預設 | 說明 |
|---|---|---|
| `imBaseUrl` | 空＝依序取 `ISSUE_MANAGER_URL` 環境變數 → `issue-manager.json` 的 `baseUrl` → MCP 設定的網頁白名單第一筆 | 開預填頁用的 IM 網址 |
| `draftMinutes` | 10 | 草稿保存分鐘（1–60） |
| `captureMaxEdgePx` | 2000 | 截圖長邊像素（400–4000） |
| `captureEnabled` | true | 設為 false 時建立議題不截圖（預填頁顯示「沒有截圖」） |
| `browserPath` | 空＝系統預設瀏覽器 | 指定用哪個瀏覽器開預填頁（執行檔完整路徑，網址當唯一引數）；檔案不存在時改用預設瀏覽器並記一行日誌 |

新增的錯誤碼（只出現在功能區按鈕／Claude 指令路徑，不經瀏覽器）：`im_url_missing`（沒有 IM 網址）、`im_url_invalid`（網址不是 https／本機 http）。

- 版本不同時要不要擋、要不要只開到圖紙，由**網頁專案設定**決定，經 `revisionPolicy` 參數送進來；外掛不自己決定。
- 圖號、視圖名稱等可顯示欄位全由網頁端專案設定控制；外掛只回傳事實，不做可見度判斷。

## 5. 對應的錯誤畫面（外掛端責任）

外掛只負責回對的錯誤碼與白話訊息；畫面文字由網頁端設定決定。外掛端必須能區分：Revit 沒開專案（`no_document`）、族群文件（`not_project_document`）、開的是別的模型（`model_mismatch`）、Revit 正忙（拿不到 ExternalEvent，由既有 `SocketService` 逾時機制回應）、草稿過期／不存在。

## 6. 驗收

**離線（可機械檢查，`dotnet test MCP.Tests`）**
- `RevitLinkDraftStore`：取一次就刪、過期回 Expired、不存在回 NotFound、代碼長度 ≥ 128 位元（22 字元 base64url 以上）、`Sweep` 清過期。
- 瀏覽器白名單：四個新指令 `IsBrowserAllowed=true`；`viewsync_draft_put` 為 false；`viewsync_create_view`／`apply_temp` 不因本功能而被放寬。
- 唯讀掃描：本功能原始碼不含 `new Transaction`／`TransactionGroup`／`Delete(`／`viewsync_create_view`／`viewsync_apply_temp`。
- IM 網址解析：只收 https／本機 http；優先序正確。

**實機（Revit 2025 與 2024，範例模型另存副本；AC-R，IM §24）**

| 項 | 做什麼 |
|---|---|
| R1 | 網頁按「在 Revit 開啟」：Revit 真的切到視圖、選取元素；刪掉元素再按退到視圖；刪掉視圖再按退到圖紙；`hidden_in_view` |
| R2 | 反向：Revit 選元件按「在 IM 建立議題」，網頁開出預填頁（含視圖截圖） |
| R3 | 在 Revit 選取元件（例如牆）按「這個元素有哪些議題」，網頁顯示相關議題 |
| R4 | 連結建立時的模型版本與目前不同：回 `modelRevisionDiffers=true`，網頁警告、仍跳轉 |
| R6 | 唯讀限制：跳轉前後模型沒有變成「已修改」、沒有新增視圖；`viewsync_draft_put`／`viewsync_create_view` 從瀏覽器送來被拒 |

（編號沿用 IM 規格 AC-R 的 R1～R4、R6；原 R2「Revit 忙」與 R3「截圖 ≤5 MB」併入上面對應項目一起驗。）

## 7. 範圍外

- IFC 換算修正（imifcfloorfix）。外掛只用 UniqueId 找元素。
- 網頁端頁面、資料庫、API、權限（imperms）。
- 圖紙上圖釘座標回 Revit 視口座標、即時雙向同步、手機跳轉。
