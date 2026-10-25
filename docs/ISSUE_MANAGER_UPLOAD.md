# 上傳到 Issue Manager（Revit 外掛端，任務書 V4 的 4B）

> 文件性質：人類與 AI 共用的功能說明與合約摘要（繁中）。日期：2026-10-25，分支 `v4b`。
> 網站端（任務書 4A：版本包 API、授權頁、`bundle-manifest.schema.json` 正式版）**尚未實作**；本檔描述外掛端依 Issue Manager repo 的規格（`docs/spec/revit-addin-export.md`、`docs/spec/addin-auth.md`、`docs/spec/sheet-sets.md`）寫成的客戶端。**全部只做到建置與離線測試，沒有在 Revit 內實跑**；實機項目見 [commit_handoff_v4b_2026-10-25.md](commit_handoff_v4b_2026-10-25.md) 的清單。
> 決策與取捨見 [ADR D-011](adr/D-011-issue-manager-upload-addin.md)。

## 1. 它做什麼

在 Revit 功能區「Issue Manager」面板按「上傳到 Issue Manager」→ 勾選視窗 → 一次匯出（圖紙 PDF、3D 模型 GLB、3D 視角）→ 打成版本包（資料夾＋`manifest.json`）→ 背景上傳 → 網站出現**一個**工作版（W）。

- 不經 MCP WebSocket：以 `IExternalCommand` 啟動、匯出經 `ExternalEventManager` 佇列在 Revit API 執行緒執行，**MCP 服務關閉時也能用**。匯出本身仍呼叫既有指令（`export_sheets_pdf`、`list_design_options`、`export_design_options`、`export_named_views`），經 `CommandExecutor.ExecuteCommand` 分派，沒有另寫匯出碼。
- 不新增 MCP 工具（工具數維持 137）、不新增 domain、不新增 skill。
- 視圖位置檔（`view_positions.json`）屬任務書 V5，**本案不匯出**，manifest 省略該欄位。

## 2. 設定（第一次使用）

網址沒有預設值（寧可顯示「尚未設定」，也不猜一個網址把權杖送出去）。擇一：

1. 環境變數 `ISSUE_MANAGER_URL=https://…`
2. `%APPDATA%\RevitMCP\issue-manager.json`：`{"baseUrl": "https://…"}`

只接受 `https`；`http` 僅限本機（`127.0.0.1`／`localhost`，給假伺服器試跑）。

含設計選項的模型需要系統 **Node.js** 與已部署的 `merge-option-glbs.mjs`（`scripts/install-addon.ps1` 會複製到 `%APPDATA%\RevitMCP\bridge\node\bundle\`）。沒有設計選項的專案不需要 Node。

## 3. 勾選視窗

| 欄位 | 規則 |
|---|---|
| 登入列 | 顯示 email（角色）；登入／登出／切換帳號 |
| 專案 | 登入後從 `GET /api/addin/projects` 載入（只含可上傳工作版的專案） |
| 設計階段 | 第一項「請選擇階段」，**沒有預設值**，不可送出；其後 PD／SD／DD |
| 版本說明 | 必填 |
| 圖紙集 | Revit 內建 `ViewSheetSet` 名稱＋「（自訂）」。選一個＝取代目前勾選（集內圖紙＋其視口視圖＋集內單獨的視圖）；手動增減後自動回「（自訂）」 |
| 圖紙清單 | 樹狀：圖紙（圖紙號＋圖名，依圖號自然排序）→ 視口視圖。勾圖紙連視口一起勾，之後可單獨取消某視口 |
| 其他視圖 | 不在任何圖紙上的視圖，依種類分組（3D／平面／天花／立面／剖面／詳圖） |
| 模型 | 「匯出 3D 模型（GLB）」預設勾；「含設計選項」預設勾 |
| 摘要 | 「將上傳 N 張圖紙、M 個視圖、K 個模型」；沒圖紙或沒模型時提示「這個版本將缺圖／缺模型」（允許送出） |

「上傳」啟用條件：已登入、專案、階段（非預設列）、說明、至少勾一項（圖紙、視圖或模型）。滑鼠移到停用的按鈕上會顯示原因。

不列入的視圖：樣板、圖例、明細表、製圖視圖、漫遊、渲染（它們不對應模型位置）。

## 4. 匯出與上傳流程

1. `POST /api/addin/v1/bundles`（專案、階段、說明）— 先建立，階段／說明有問題就不必等長時間匯出。
2. 匯出 PDF（`export_sheets_pdf`，合併單檔 `sheets.pdf`，頁序＝manifest 的 `pageNumber`＝圖號自然排序）。
3. 匯出 GLB（`export_design_options`；有選項分件時以 `merge-option-glbs.mjs` 合併成 `model.glb`）。
4. 匯出 `named_views.json`（`export_named_views`，原點與 GLB 相同）。
5. 產生 `manifest.json`（檔案 sha256、圖紙號取自 Revit）。
6. 背景分段 PUT 到簽名網址（可重試、不重傳已完成的檔案）。
7. `POST …/finalize`（冪等）→ 顯示版本連結。

步驟 2～5 在 Revit API 執行緒內（期間 Revit 無法操作）；每個步驟之間檢查取消旗標並讓視窗重繪。**GLB 匯出是單一長呼叫，無法中途取消**（CPC 案實測 631～1,175 秒）。步驟 6、7 在背景執行緒。

- 取消：呼叫 `DELETE /bundles/:id`、刪本機暫存、不再上傳。
- 失敗：顯示白話原因；伺服器端 bundle 保持 open 至過期自動清；若匯出已完成，視窗出現「重試上傳」（從未完成的檔案／分段繼續）。
- 同一時間只允許一個進行中的上傳。
- 「含設計選項」未勾：只匯出主模型與各選項集合的**主要選項**（`isPrimary`），不含其他選項（這是本案的解讀，見 ADR D-011）。
- 匯出報告 `complete:false`（任何分件失敗／預算截斷）→ 整個模型步驟中止並指出報告路徑，**不上傳缺漏的模型**（LESSONS L-15）。

## 5. 登入（瀏覽器授權碼＋PKCE＋loopback）

依 `addin-auth.md`：產生 `state`、`code_verifier`（32 位元組亂數）、`code_challenge`（S256）→ 在 `127.0.0.1` 隨機埠開監聽 → 開預設瀏覽器到 `/addin/authorize?state&code_challenge&code_challenge_method=S256&port&client_version&client_name` → 使用者在網站按「授權」→ 302 回 `http://127.0.0.1:埠/callback?code&state` → 比對 state → `POST /api/addin/token`（`authorization_code`＋`code_verifier`）→ 取得 access／refresh。

- loopback 用 `TcpListener(IPAddress.Loopback, 0)`（不需 URL ACL）；只接受 `GET /callback`、Host 必須是 `127.0.0.1:埠`、state 固定時間比對；不符回 400／404 並**繼續等**（本機其他程式亂打不會讓正確回呼失敗）；120 秒逾時自動關閉。
- refresh token 以 **Windows DPAPI（使用者層級，P/Invoke crypt32）** 加密寫入 `%LOCALAPPDATA%\RevitMCP\issue-manager\auth.bin`（連 email 也不明文）；access token 只放記憶體。
- refresh 輪替：新 refresh 立刻落檔；refresh 以單一鎖序列化（並行使用同一份 refresh 會被伺服器判成外洩而撤銷整族）；`invalid_grant` → 清本機並要求重新登入；暫時性錯誤（5xx、斷網）不清登入。
- 登出：`POST /api/addin/token/revoke` ＋刪檔（伺服器不通也照刪）。
- 權杖、授權碼、verifier、state 不寫日誌、不進例外訊息（單元測試以全部祕密字串搜尋日誌與訊息驗證）。
- Bearer 只送給 Issue Manager 自己的主機；簽名上傳網址若在別的主機，不附 Authorization。上傳網址必須是 https（本機例外）。HttpClient 不自動跟隨轉址。

## 6. 檔案與程式位置

| 路徑 | 內容 | 依賴 Revit API |
|---|---|---|
| `MCP/Core/IssueManager/Pkce.cs` | PKCE、state、固定時間比對 | 否 |
| `…/LoopbackCallbackListener.cs` | loopback 接收器 | 否 |
| `…/TokenVault.cs` | `ITokenVault`、DPAPI 檔案保存 | 否 |
| `…/AddinAuthService.cs`、`AddinApiClient.cs`、`AddinHttp.cs`、`AddinModels.cs`、`IssueManagerSettings.cs` | 登入、API 客戶端、錯誤轉換、設定 | 否 |
| `…/SelectionModels.cs`、`ExportSelectionViewModel.cs`、`Strings.cs` | 勾選視窗的資料模型與檢視模型（zh-TW 字串集中） | 否 |
| `…/BundleManifest.cs`、`BundleExporter.cs`、`BundleUploader.cs`、`UploadFlow.cs`、`GlbMergeRunner.cs` | manifest 產生、匯出流程、上傳器、整體流程 | 否 |
| `…/UploadWindow.cs` | WPF 勾選視窗（程式碼建構，不引用 Revit API） | 否 |
| `…/RevitSelectionCollector.cs`、`RevitBundleExportSteps.cs`、`IssueManagerSession.cs` | 讀 Revit 清單、經分派器匯出、工作階段 | **是** |
| `MCP/Commands/UploadToIssueManagerCommand.cs`、`MCP/Application.cs` | 功能區按鈕與指令 | 是 |
| `docs/schemas/bundle-manifest.schema.json`、`docs/schemas/fixtures/bundle-manifest.sample.json` | manifest schema（客戶端副本）與共用 fixture | — |
| `tools/fake-issue-manager/server.mjs` | 假 Issue Manager（Node，無相依） | — |
| `MCP.Tests/IssueManager/` | 115 個 NUnit 測試 | — |
| `tools/*.test.mjs`（`bundle-manifest.schema.test.mjs`、`fake-issue-manager.test.mjs`） | ajv 驗 schema／fixture；假伺服器合約 | — |

## 7. 假伺服器與試跑

```powershell
node tools/fake-issue-manager/server.mjs --auto-approve --part-bytes 65536   # 印出網址
$env:ISSUE_MANAGER_URL = "http://127.0.0.1:<印出的埠>"                          # 再啟動 Revit
```

不加 `--auto-approve` 時授權頁會顯示「授權／取消」按鈕（手動走一遍瀏覽器流程）。`GET /__state` 看 bundle、刪除紀錄、PUT 次數；`POST /__config {"failNextPuts":2}` 讓接下來的 PUT 回 503。資料只存在記憶體。

## 8. 與網站端 schema 的對齊（必做）

`docs/schemas/bundle-manifest.schema.json` 是**客戶端依規格 §5.1 寫的副本**。網站端 4A 建立正式的 `docs/spec/schemas/bundle-manifest.schema.json` 後：

1. 逐欄比對（尤其 `viewKind`／`kind` 列舉：本案用 `3d`、`plan`、`ceiling`、`section`、`elevation`、`detail`；`files.*.bytes` 最小 1；`additionalProperties:false`）。
2. 兩邊測試改載入**同一份** fixture（`docs/schemas/fixtures/bundle-manifest.sample.json`），C# 的 golden 比對與 ajv 測試都已經是這樣用。
3. 規格裡尚有兩個合約缺口，見交班單「風險」：建立 bundle 時尚不知各檔大小（簽名網址數量如何決定）、合併 PDF 的頁序保證。

## 9. 已知限制

- 全部未在 Revit 內實跑；視窗行為（訊息泵、取消、Revit 主視窗在匯出期間是否仍可點）、PDF 頁序、設計選項匯出完整性都待實機。
- 只有 Release.R24 驗證可建置（0 錯誤、警告數與基線相同）。其他年份的建置失敗來自既有檔案（R25／R26：`RevitGltfExportContext.cs` 缺 `System.Drawing.Common`；R22：`WebCompareSection.cs` 用了 2022 沒有的 API），新增檔案在這些目標上沒有新增錯誤。
- 視窗字串目前只有 zh-TW。
- 圖紙上的圖例／明細表視口不列入（不對應模型位置）。
