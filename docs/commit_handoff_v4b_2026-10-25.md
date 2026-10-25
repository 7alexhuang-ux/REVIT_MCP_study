# 交班單 — 任務書 V4 的 4B（Revit 外掛端：上傳到 Issue Manager）— 2026-10-25

> 性質：人類與 AI 共用交班紀錄（繁中）。範圍：分支 `v4b`（工作樹 `C:\temp\revit_v4b\wt`，從 `ff60626` 開出）。**建置者：Claude Sonnet 5.5（Claude Code）；審查由另一個 session 做。**
> 只 commit，未 push、未動主工作樹、未動遠端；**未啟動或操作 Revit、未安裝外掛到 `%APPDATA%\Autodesk\Revit\Addins`、未連真實 Issue Manager 或任何真實 Google 帳號**。所有「需實機」的項目都沒有宣稱通過。
> 功能說明：[ISSUE_MANAGER_UPLOAD.md](ISSUE_MANAGER_UPLOAD.md)；決策：[adr/D-011-issue-manager-upload-addin.md](adr/D-011-issue-manager-upload-addin.md)。

## 1. Commit 清單（依提交順序）

| # | Commit | 內容 | 檔數 |
|---|---|---|---:|
| 1 | `1408007` | 外掛端核心（不依賴 Revit API）：PKCE、loopback 接收器、DPAPI 權杖保存、登入服務、API 客戶端、設定、勾選檢視模型、manifest、匯出流程、上傳器、整體流程；schema＋fixture；假 Issue Manager（Node）；NUnit／Node 測試；CHANGELOG | 33 |
| 2 | `377530d` | Revit 端與 UI：功能區按鈕、WPF 勾選視窗、讀 Revit 清單、經分派器匯出、工作階段；`install-addon.ps1` 部署合併腳本；視窗與整段流程測試；CLAUDE.md、ROADMAP R-08、CHANGELOG | 12 |
| 3 | （本檔所在 commit） | 文件：功能說明、ADR D-011、SPEC、文件清單、PROJECT_INTRO、LESSONS L-18～L-20、log、本交班單 | — |

每個 commit 都以 `git commit -F <訊息檔> -- <明確路徑>` 提交，沒有用 `git add -A`／`git add .`。建置產物（`MCP/bin`、`MCP/obj`，已被追蹤）不在任何 commit。commit 1 在獨立暫時工作樹驗證過可單獨建置：R24 0 錯誤／1184 警告、`dotnet test` 420 通過。

## 2. 建置與測試數字

| 項目 | 基線（`ff60626`） | 現在 | 備註 |
|---|---|---|---|
| `dotnet build -c Release.R24 MCP/RevitMCP.csproj` | 0 錯誤、1184 警告 | 0 錯誤、1184 警告 | 新增檔案 0 個警告（檔頭 `#nullable disable`，同既有程式風格）；編譯輸出無任何含 `IssueManager`／`UploadToIssueManager` 的警告 |
| `dotnet test MCP.Tests` | 314 通過 | **429 通過**、0 失敗、0 略過（新增 115） | commit 1 單獨 420 |
| MCP-Server `npm test`（含 `tsc`） | 38 ＋ Node 10 | 38 ＋ Node **34**（新增 24：`bundle-manifest.schema.test.mjs` 15、`fake-issue-manager.test.mjs` 9）、0 失敗 | 本案未改 MCP-Server 原始碼 |
| `scripts/verify-qaqc.ps1 -SkipBuild -SkipDeploy` | 54 PASS／0 FAIL／3 SKIP | 54 PASS／0 FAIL／0 WARN／3 SKIP | 在含本批文件的工作樹跑；`docs/DOCUMENT_AUDIENCE_INVENTORY.md` 已登錄新文件 |
| 其他 Revit 年份 | — | R22：3 錯誤；R25／R26：各 5 錯誤 | **全部來自既有檔案**（R25／R26：`RevitGltfExportContext.cs` 缺 `System.Drawing.Common`；R22：`WebCompareSection.cs` 用了 2022 沒有的 API），沒有任何錯誤來自新增檔案；新增檔案在這些年份的實際行為未驗證 |

新增 115 個 NUnit 測試的分布（依測試檔）：PKCE＋loopback 14、權杖保存＋設定 19、登入服務 13、manifest＋圖號排序＋勾選檢視模型 18、匯出器＋上傳器＋API 客戶端＋整體流程 33、GLB 合併腳本 3、視窗綁定 5、**視窗整段流程 4**（真的視窗＋真的 `UploadFlow`＋假伺服器）、Node 假伺服器整合 6（無 Node 時略過；本機有 Node，已實跑）。

## 3. 驗收對照

標示：✅ 離線已驗證（附測試）／⏳ 需實機（未宣稱通過）／— 不在本案範圍。

### 外掛端 X 系列（revit-addin-export.md §7）

| 條件 | 狀態 | 證據／說明 |
|---|---|---|
| X01 manifest 產生器輸出通過 schema | ✅ | `ManifestBuilderTests.X01_*`（golden fixture ＋ 測試用 schema 驗證器）；`tools/bundle-manifest.schema.test.mjs`（ajv 驗同一份 fixture＋13 種壞 manifest）。⚠️ schema 是**客戶端副本**，網站端 4A 正式版出來後需對齊（§5） |
| X02 PKCE 與 RFC 7636 測試向量相符 | ✅ | `PkceTests.X02_*`（verifier `dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk` → `E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM`） |
| X03 loopback 只綁 127.0.0.1；state 不符被拒；逾時關閉 | ✅ | `LoopbackCallbackListenerTests`（綁定位址、state／Host／path 不符回 400／404 且繼續等、逾時後埠關閉）；逾時秒數預設 120（`AddinAuthService.LoginAsync` 呼叫端傳入） |
| X04 refresh token 為 DPAPI blob、登出後檔案刪除 | ✅ | `TokenVaultTests.X04_*`、`AddinAuthServiceTests.Logout_*`（檔案內搜不到 UTF-8／UTF-16 明文及 email） |
| X05 步驟間檢查取消；取消後不上傳並呼叫 DELETE | ✅ | `BundleExporterTests.X05_*`、`UploadFlowTests.X05_*`（匯出中、上傳中）、`UploadWindowFlowTests.CancelButton…`（真的視窗）、Node 整合 `CancelAfterBundleCreation…` |
| X06 勾選視窗檢視模型 | ✅ | `ExportSelectionViewModelTests.X06_*` ＋ `UploadWindowTests`（控制項綁定：階段預設列、停用原因、圖紙集預設勾選與回「（自訂）」） |
| X07 上傳重試不重傳已完成（sha256 相符）的檔案 | ✅ | `BundleUploaderTests.X07_*`（含「檔案內容變了就重傳」）、`UploadFlowTests.UploadFailureCanBeResumed…` |
| X08 按鈕在 MCP 服務關閉時仍可用 | ⏳ 需實機 | 程式結構上不依賴 `SocketService`（按鈕 → `IExternalCommand` → `ExternalEventManager`），但未在 Revit 內驗證 |
| X09 完整跑一次（登入、勾選、上傳；網站出現單一 W 版本） | ⏳ 需實機 | 離線已用假伺服器跑通整條（Node 整合 `FullFlow_*`：伺服器收到的 manifest 通過 schema、64 bytes 分段上傳、finalize）；**對真的 Issue Manager** 需 4A 完成後由 Alex 的 Revit 環境跑，附網站畫面截圖 |
| X10 斷網或中途取消後網站沒有半個版本 | ⏳ 需實機（離線等價 ✅） | 離線：取消／上傳失敗都不會 finalize（`UploadFlowTests`、視窗流程測試），取消會 DELETE；實機斷網情境見 §4 T9 |

### 網站端／其他系列（不在 4B，列出以免誤認）

| 系列 | 狀態 | 說明 |
|---|---|---|
| A01～A12（授權，網站端） | — | 網站端 V3 的事。客戶端對應行為：A10 日誌／錯誤訊息不含祕密 ✅（`AddinAuthServiceTests.A10_*`，用全部祕密字串搜尋日誌與例外訊息）；A02／A03／A05 等伺服器行為只對**假伺服器**驗證（`tools/fake-issue-manager.test.mjs`），不能當網站端通過 |
| B01～B10（版本包 API，網站端 4A） | — | 4A 尚未實作。客戶端相關：B01／B02（階段、說明被拒 → 白話訊息）✅、B05（finalize 冪等，重送回同一 `setId`）✅（對假伺服器）、B08（426 → 「要求先更新外掛」）✅ |
| Q01～Q10（視圖位置） | — | 任務書 V5，本案不做；manifest 省略 `files.viewPositions` |

## 4. 給 Alex 的實機測試清單（逐步）

> 先用**假伺服器**走完 T1～T11（不碰真的 Issue Manager、不需要 Google 帳號），再等 4A 完成後做 T12。
> 測試專案建議：≥3 張圖紙且圖號不照順序建立（例如 A-10、A-2、A-1）、含平面／剖面／立面／3D 視圖、有至少一個圖紙集（ViewSheetSet）、最好有設計選項（AR_CPC 或等價專案）。

**準備（一次）**

1. 開 PowerShell：`cd C:\temp\revit_v4b\wt`（分支 `v4b`；不是主工作樹）。
2. 啟動假伺服器並保持視窗開著：`node tools\fake-issue-manager\server.mjs --auto-approve --part-bytes 65536` → 記下印出的網址（例如 `http://127.0.0.1:50123`）。
3. 同一個 PowerShell 視窗：`$env:ISSUE_MANAGER_URL = "http://127.0.0.1:50123"`。（或寫 `%APPDATA%\RevitMCP\issue-manager.json`：`{"baseUrl":"http://127.0.0.1:50123"}`；環境變數只對**從這個視窗啟動**的 Revit 有效。）
4. 安裝：`.\scripts\install-addon.ps1 -Version <你的 Revit 年份>`（會自己編譯、複製到 `%APPDATA%\Autodesk\Revit\Addins\<年份>`、部署合併腳本；**會覆蓋你目前安裝的外掛版本**）。
5. 從同一個 PowerShell 視窗啟動 Revit、開測試專案。**不要**按「MCP 服務 (開/關)」。

**測試**

| # | 動作 | 預期看到 |
|---|---|---|
| T1（X08） | 看功能區 | 「MCP Tools」旁出現新面板「Issue Manager」，按鈕「上傳到 Issue Manager」；MCP 服務沒開也能點，開啟視窗；登入列「尚未登入」。若登入列是紅字「尚未設定 Issue Manager 網址…」＝環境變數沒傳進 Revit |
| T2 登入 | 按「登入」 | 預設瀏覽器開啟授權網址並立刻顯示「已完成授權」；視窗顯示 `fake.user@example.test（admin）`，專案下拉出現「CPC　測試專案」。檢查：`%LOCALAPPDATA%\RevitMCP\issue-manager\auth.bin` 存在、用記事本開是亂碼；`%APPDATA%\RevitMCP\Logs\RevitMCP_*.log` 搜尋 `imat_`／`imrt_`／`imac_`／`code_verifier` 應該**沒有命中** |
| T3 勾選 | 先不選階段 | 「上傳」停用，滑鼠移上去顯示原因；階段第一項是「請選擇階段」。選好專案、階段、說明後啟用 |
| T3b 圖紙集 | 圖紙集下拉 | 列出專案內全部圖紙集＋「（自訂）」（**驗證 `ViewSheetSet` 能被讀到**）；選一個 → 勾選與該集一致；手動多勾一張 → 下拉回「（自訂）」；樹中圖紙依 A-1、A-2、A-10 排序 |
| T4（X09 假伺服器版） | 勾 2～3 張圖紙＋1 個 3D 視圖＋保持勾選模型 → 上傳 | 狀態列依序 [1/N]…[N/N]；匯出 PDF；匯出 GLB（大模型要等，期間不能取消）；完成後出現「完成」與連結。開 `http://127.0.0.1:50123/__state`：`bundles[0].status` 為 `finalized`，`manifest.sheets[].sheetNumber` 等於 Revit 圖紙號 |
| T5 PDF 頁序（最重要的未知） | 先 `curl -X POST http://127.0.0.1:50123/__config -d "{\"failNextPuts\":100}"` 讓上傳失敗，再上傳一次 | 失敗後出現「重試上傳」；本機暫存保留於 `%LOCALAPPDATA%\RevitMCP\issue-manager\bundles\<時間>\`。開裡面的 `sheets.pdf`：**第 N 頁應是 `manifest.json` 的第 N 張圖紙**（A-1 → A-2 → A-10）；頁數＝勾選張數。再 `curl … -d "{\"failNextPuts\":0}"` 按「重試上傳」→ 只傳還沒完成的檔案、**不重新匯出**，完成後暫存刪除 |
| T6 取消 | 上傳中按「取消上傳」 | PDF 步驟之後按 → 進入 GLB 前停止；GLB 步驟期間按 → 要等 GLB 結束才停（視窗有說明）。`/__state` 的 `deleted` 多一筆該 bundle、沒有 `finalized` |
| T7 匯出期間畫面 | GLB 匯出中觀察 | 視窗文字是否更新？Revit 主視窗是否仍可點擊／可操作？（**未知**，記錄現象並截圖；視窗已提示「請勿操作 Revit」） |
| T8 設計選項 | 在有設計選項的專案分別勾／不勾「含設計選項」 | 成功時上傳的 GLB 是合併檔。若 `export_design_options` 回 `complete:false`，視窗應顯示「3D 模型匯出不完整…」並指向 `design_options_export.json`（不上傳缺漏的模型）。未勾＝只含主模型＋各集合主要選項。沒有 Node.js 時應顯示「找不到 Node.js」 |
| T9（X10） | 上傳中關閉假伺服器視窗（模擬斷網） | 視窗顯示白話原因（「連不到 Issue Manager…」），沒有「完成」；重新啟動假伺服器後資料已清空，屬預期 |
| T10 登出／切換帳號 | 按「登出」 | `auth.bin` 消失；`/__state.revokedTokens` 增加；再登入可成功 |
| T11 重開 Revit 與 refresh | 先 `curl -X POST …/__config -d "{\"accessTtlSec\":10}"`，再登入；等 20 秒後上傳 | 自動用 refresh 換新權杖不需重新登入（`/__state.refreshCount` 增加）；關掉 Revit 重開，登入狀態仍在（從 DPAPI 還原） |
| T12（X09 真實版，等 4A） | `ISSUE_MANAGER_URL` 改真網址，重做 T2、T4 | 網站出現**單一** W 版本，圖紙、模型、視圖數與勾選一致，附網站畫面截圖 |

## 5. 風險

| # | 風險 | 影響／處理 |
|---|---|---|
| R1 | **PDF 頁序未查證**：不確定 `PDFExportOptions.Combine=true` 是否依傳入順序排頁；manifest 的 `pageNumber` 假設＝圖號自然排序＝傳入順序 | 頁序錯＝網站上圖紙與頁面對不起來。程式只做「PDF 頁數估計 ≠ 圖紙數」的警告。T5 驗證；若不符需改成逐張匯出再合併或另想辦法 |
| R2 | 匯出期間視窗靠 `Dispatcher.Invoke(Background)` 重繪並處理取消，Revit 主視窗的訊息也可能被處理 | 使用者可能在匯出中操作 Revit（視窗有提示）。T7 觀察 |
| R3 | `await` 之後的執行緒：已用 `Ui()` 全面切回 UI 執行緒並以真視窗測過，但 Revit 內的 `SynchronizationContext` 行為只能實機確認 | LESSONS L-20 |
| R4 | 設計選項匯出完整性：AR_CPC 在 10-24 曾 5 選項全 `ok:false`（之後已補規則，未再實機確認）。本流程遇 `complete:false` 會**中止模型步驟** | 可能導致含設計選項的模型一直上傳不了；可取消勾選模型先上圖紙。另需 Node.js |
| R5 | 合約缺口：建立 bundle 時尚不知各檔大小，簽名網址數量如何決定（規格只說回 `files:[{kind,name,partBytes,urls}]`） | 現行以回應為準，網址不足會明確失敗（「上傳網址不足」）；需 4A 約定（例如 finalize 前再補核發，或建立時給足） |
| R6 | schema 是客戶端副本：`viewKind`／`kind` 列舉我定為 `3d`、`plan`、`ceiling`、`section`、`elevation`、`detail`；`files.*.bytes` 最小 1；各層 `additionalProperties:false` | 與網站端不一致就會被伺服器擋。對齊步驟見功能說明 §8 |
| R7 | `ViewSheetSet` 以 `FilteredElementCollector(typeof(ViewSheetSet))` 取得（未實機）；為空時下拉只有「（自訂）」 | T3b 驗證 |
| R8 | 圖例／明細表／製圖視圖的視口不列入 manifest（不對應模型位置） | 這類視口不會有 3D 對應；需要的話之後再調 |
| R9 | DPAPI（使用者層級）保護的是「別的 Windows 使用者」，同一帳號下的其他程式理論上可解（已加額外熵只是門檻） | 與規格決策一致；縮短風險靠 refresh 輪替與伺服器撤銷 |
| R10 | 其他 Revit 年份既有建置錯誤；新增檔案在 R22／R25／R26 的實際行為未驗證 | 非本案造成；若 Alex 要在這些年份用，需先處理既有錯誤 |
| R11 | `MCP.Tests` 改為 `UseWPF`、引用 Newtonsoft.Json；Node 整合測試需 Node.js | 在 Windows＋.NET SDK 環境沒問題；無 Node 時整合測試略過（不算通過） |
| R12 | 分支 `v4b` 從 `grading-workflow-v2` 開出，尚未 push、尚未合併回任何分支；與 `main` 的分歧問題見前一份交班單（R-01） | 合併策略待決定 |

## 6. 待 Alex 決定

1. **Issue Manager 網址如何設定**：目前是環境變數 `ISSUE_MANAGER_URL` 或 `%APPDATA%\RevitMCP\issue-manager.json`，沒有預設值。要不要改成寫死正式網址、或在 Revit 內加設定畫面？
2. **「含設計選項」未勾的語意**：我解讀為「只匯出主模型＋各集合主要選項」（只匯出主模型會漏掉主要選項內的元素）。是否符合預期？
3. **R5 合約缺口**要由 4A 還是客戶端怎麼解（建立時給足網址／finalize 前補核發／改成匯出後才建立 bundle）。
4. **R1 PDF 頁序**若實機證明不保證，要用哪種替代：逐張匯出再合併（需引入 PDF 合併元件）、或改上傳逐張 PDF。
5. **分支去向**：`v4b` 要合併回 `grading-workflow-v2`、單獨 push，或 cherry-pick。
6. 視窗**英文字串**何時做（目前只有 zh-TW，字串已集中在 `Strings.cs`）。
7. 假伺服器與 Node 整合測試是否納入 CI。

## 7. 其他

- 本檔所在 commit 之後，工作樹只剩已被追蹤的建置產物（`MCP/bin/Release*`、`MCP/obj/*`）顯示為已修改，**刻意不提交**（同前一份交班單）。
- 中央日誌已追記；完成旗標 `C:\Temp\grokbot_sessions\imv4b_finished.flag` 已寫入。
- 暫時用過的 `C:\temp\revit_v4b\c1`（commit 1 單獨驗證用）已移除。


---

## 8. 審查後修正（2026-10-25，獨立安全審查 opus 之後）

> 建置者：Claude Sonnet 5.5（Claude Code）。審查者以程式碼行號指出五個必修；每一項都先自行讀程式碼確認屬實才修。**仍只做離線驗證**：未啟動或操作 Revit、未安裝外掛、未連真實 Issue Manager、未 push。
> 本段取代上文中與之衝突的敘述：§2 的數字（429 → 479）、§4 T7（匯出期間視窗行為）、§5 R2（巢狀訊息迴圈）、T10（登出）。

### 8.1 Commit

| Commit | 內容 |
|---|---|
| `fba7a60` | 修正：程式與測試（21 個檔：外掛端 12 個、測試 7 個、`Application.cs`、`ExternalEventManager.cs`；新增 `ApiThreadRunners.cs`、`BundleTempCleaner.cs`、`HardeningTests.cs`、`RunnerAndCleanupTests.cs`） |
| （本檔所在 commit） | 文件：功能說明、ADR D-011 審查後修正、CHANGELOG、LESSONS L-21～L-23、log、本段 |

### 8.2 必修項（審查指出 → 確認 → 修法 → 測試）

| # | 審查指出 | 確認結果 | 修法 | 主要測試 |
|---|---|---|---|---|
| 1 | `AddinAuthService.cs` 登出：有記憶體 access 就當 Bearer，不看到期／401，也不改用 refresh 撤銷 | **屬實**（舊 `LogoutAsync` 把回應丟掉；假伺服器對無效 Bearer 回 401，審查探針 `revokedTokens`＝0） | access 未過期→Bearer；已過期、401、或重開後沒有 access→`{"refresh_token"}` 走同一端點（`addin-auth.md` §4.4）。`LogoutAsync` 回傳 `LogoutOutcome`；失敗時本機仍刪除，視窗顯示「伺服器端撤銷未完成」並指引到網站「帳號 › 已授權的外掛」。切換帳號同一條路（舊帳號撤銷失敗時登入完成後仍警告）。整段持有 refresh 鎖 | `FakeServerIntegrationTests.LogoutAfterAccessExpired…`（真的 Node 假伺服器，accessTtl 1 秒、2.5 秒後登出，`revokedTokens` 增加 **2**）；`AddinAuthServiceTests` 新增 6 個（Bearer、過期→refresh、401→refresh、重開還原→refresh、兩者都失敗、全過期）；視窗 5 個（過期登出、撤銷失敗文案、切換帳號兩種、登入取消） |
| 2 | `GlbMergeRunner.cs`：`CanRun("node")` 裸名；`FindScript` 往上 8 層找 `tools\` | **屬實**（`UseShellExecute=false` 先找目前工作目錄；我實測對照組：把會寫標記檔的假 `node.exe` 放工作目錄，裸名 `node` 確實執行了它） | `FindNode` 只回絕對、存在的路徑（標準安裝位置→PATH 內絕對目錄），略過工作目錄與開啟中專案資料夾之下者，`canRun` 用絕對路徑並固定工作目錄；`FindScript` 往上找 `tools\` 預設關閉，需 `REVITMCP_DEV_TOOLS=1` | `GlbMergeRunnerTests`：陷阱 `node.exe`（CodeDom 現場編譯、執行就留標記檔）放工作目錄→`FindNode` 不執行、PATH 放 `.`／相對／工作目錄絕對路徑→`canRun` 一次都不被呼叫；專案資料夾略過；旗標測試 |
| 3 | `RevitApiThreadRunner`：排隊不理取消、不看 `Raise()`，`_running` 卡 1 | **屬實**（無條件 `await` 一個永遠不會完成的 `TaskCompletionSource`） | 新增 `QueuedApiThreadRunner`（不引用 Revit API）：排隊／已開始／已放棄三態；取消或開始前逾時（120 秒）→白話訊息「Revit 目前沒有回應，請關掉 Revit 裡的對話框後重試」，之後 Revit 處理佇列也不執行該工作；`Raise()` 非 `Accepted`／`Pending`→立刻失敗；已開始的長呼叫不受逾時限制。`ExternalEventManager.TryExecuteCommand` 回傳 `Raise()` 結果。`UploadFlow` 的 `_running` 在所有結束路徑 `finally` 釋放 | `QueuedApiThreadRunnerTests` 8 個；`UploadFlowStuckRevitTests` 4 個（外部事件永不執行→逾時＋旗標歸零＋可立刻再上傳；排隊中取消→伺服器 bundle 被刪＋旗標歸零；Raise 被拒；步驟例外） |
| 4 | `UploadWindow.cs`：X 關閉不呼叫 `DiscardAsync`；本機暫存累積 | **屬實**（只有「關閉」按鈕路徑會 Discard） | `Closing` 事件統一處理：進行中→取消；有可重試 bundle→`ConfirmDiscardOnClose` 詢問「放棄這次上傳並清除？」（真實環境為 MessageBox；測試未設定＝直接放棄），同意則**先同步刪本機檔**、再背景盡力 `DELETE`（連不上也不留本機檔）；選「否」→取消關閉。啟動時 `BundleTempCleaner` 背景清除超過 3 天（`DefaultMaxAgeDays`）的暫存資料夾（資料夾內有最近寫入的檔案不刪） | 視窗 3 個（X 關閉清本機＋伺服器、回答「否」保留且可重試成功、已完成的上傳不詢問）；`BundleTempCleanerTests` 4 個 |
| 5 | 巢狀訊息迴圈（`Dispatcher.Invoke` 在 ExternalEvent 內） | **屬實** | **選「每個 Revit 步驟各自一次 ExternalEvent」**（`BundleExporter.ExportAsync`）；步驟之間回到 UI 執行緒更新進度、檢查取消；移除 `IExportProgress.Pump`。不採 `EnableWindow`（取捨表見 ADR D-011「審查後修正」） | `ExporterStepSplittingTests`（事件順序 report/run 交錯、排隊中取消）；視窗整段流程測試（真視窗＋Dispatcher 模擬 Revit 執行緒）仍全過 |

**#5 取捨摘要**：優點是移除問題根源、不碰 Win32、取消在步驟邊界即時生效；代價是**步驟之間 Revit 主視窗可以操作**（使用者若在匯出中途編輯模型，PDF 與 GLB 可能對應不同狀態，程式只檢查作用中文件沒換），以及 GLB 那一步仍會佔住 Revit 執行緒（視窗此時可能顯示「沒有回應」，與以前相同）。未採 B（`EnableWindow`）的理由：保留巢狀迴圈的一半風險，且「Revit 主視窗被我們停用後 `finally` 前當掉」的後果更重，行為又只能實機驗證。

### 8.3 建議項

| 項目 | 狀態 |
|---|---|
| `LoopbackCallbackListener` `ExclusiveAddressUse` ＋ `Task.WhenAny`／`Delay` 讀取逾時 | ✅ 做了；測試：只連線不送資料的連線在逾時後被關閉且不擋後續正確回呼、`SO_REUSEADDR` 搶綁會失敗 |
| `BundleUploader` 重試時重新計算磁碟 sha256 | ✅ 做了（只在狀態檔已存在＝續傳時重算，第一次上傳不重算避免大 GLB 多花時間）；檔案被改動→拒絕續傳並請放棄後重新匯出 |
| `PdfInspector` 串流掃描 | ✅ 做了（1 MB 區塊＋128 字元重疊，測試涵蓋跨區塊邊界不重複計數） |
| 頁數不符時中止而非只警告 | ✅ 做了，**含例外**：頁數少於圖紙數、且 PDF 含物件串流（`/ObjStm`）時估計不可靠，只警告（否則壓縮 PDF 會誤擋所有上傳）；頁數多於圖紙數一律中止 |
| 登入中取消的文案 | ✅ 「已取消登入。」，按鈕文字「取消登入」 |
| `Application.cs` 面板建立包 `try/catch` | ✅ |

### 8.4 測試與建置數字

| 項目 | 基線（`b07bd5d`） | 現在 |
|---|---|---|
| `dotnet build -c Release.R24 MCP/RevitMCP.csproj` | 0 錯誤、1184 警告 | 0 錯誤、**1184 警告**（沒有增加） |
| `dotnet test MCP.Tests` | 429 通過 | **479 通過**、0 失敗、0 略過（+50；連跑 6 次穩定） |
| MCP-Server `npm test` | 38 ＋ Node 34 | 38 ＋ Node 34（未改 MCP-Server 與假伺服器原始碼） |

新增測試分布：`QueuedApiThreadRunnerTests` 8、`UploadFlowStuckRevitTests` 4、`ExporterStepSplittingTests` 2、`BundleTempCleanerTests` 4、`LoopbackHardeningTests` 3、`UploaderResumeVerificationTests` 3、`PdfInspectionTests` 7、`GlbMergeRunnerTests` +4、`AddinAuthServiceTests` +6、整合測試 +1、視窗流程測試 +8；調整既有：`FakeSteps` 預設 PDF 改為「頁數＝傳入圖紙數」，頁數不符的既有測試改為預期中止。

測試途中發現並修掉的自己的問題：視窗測試在背景載入專案清單完成前就結束，背景工作之後對已結束的 UI 執行緒 `Dispatcher.Invoke` 造成 `NUnitEngineUnloadException`（改為等專案清單載入完才結束）；進度回報在結果之後才到達會蓋掉最後的狀態文字（結果處理一律先 `InvalidateProgress()`）。

### 8.5 未做項與理由

| 項目 | 理由 |
|---|---|
| `EnableWindow` 停用 Revit 主視窗 | 見 8.2 #5 取捨；等實機觀察後再決定要不要疊加 |
| 匯出失敗（非取消）時也 `DELETE` 伺服器端 bundle | 審查未要求；規格說 open 的 bundle 到期自動清。逾時後使用者重試會多出一個 open bundle（到期清除） |
| `CommandExecutor.Walkthrough.cs` 的 `FindNodeExecutable`（同型裸名 `node` 問題） | 不屬於本案（走動展示的深度圖匯出）、也不在審查範圍；**建議另案處理** |
| 其他 Revit 年份建置 | 同前，既有檔案錯誤 |

### 8.6 待 4A 約定（合約缺口，客戶端無法單方面解決）

1. **finalize 進行中按取消**：`UploadFlow` 在 finalize 請求送出後被取消時，伺服器可能已建立版本，客戶端卻接著 `DELETE /bundles/:id`。需約定：①`DELETE` 一個已 finalize 的 bundle 的語意（建議 409 且**不刪版本**）；②客戶端取消後如何得知結果（冪等 finalize 重送回同一 `setId`，B05 已有）。在約定前，客戶端建議行為是「finalize 開始後不再允許取消」，目前**尚未實作**，需 Alex 決定。
2. **`POST /bundles` 回應 `files[].kind` 的值域**：規格只寫 `files:[{kind,name,partBytes,urls}]`，沒列舉 `kind`。客戶端目前假設 `pdf`／`glb`／`namedViews`（`viewPositions` 屬 V5），以 `kind` 對應 manifest 與上傳憑證；與網站端不一致就會出現「伺服器沒有為…核發上傳憑證」。與既有 R5（簽名網址數量）一併約定。

### 8.7 需實機觀察（未宣稱通過）

| # | 項目 | 為什麼只能實機 |
|---|---|---|
| O1 | 每步一次 ExternalEvent 後，視窗在步驟之間是否正常重繪、「取消上傳」是否在 PDF／命名視圖步驟邊界即時生效 | 取決於 Revit 真實的 Idling／ExternalEvent 排程 |
| O2 | GLB 那一步（可達十幾分鐘）視窗是否顯示「沒有回應」、使用者此時點視窗會怎樣 | 與以前相同的限制，但未量測 |
| O3 | 步驟之間使用者操作 Revit 主視窗（切文件、編輯模型）的實際影響；程式只能偵測文件切換 | 無法離線模擬使用者操作 |
| O4 | `ExternalEvent.Raise()` 在「前一個要求尚未處理」時是否回 `Pending`、Revit 停在對話框時 `Raise()` 回傳什麼；120 秒開始前逾時是否合理（佇列與 MCP 指令共用，若 MCP 正在跑長指令也會等） | 只能看 Revit 實際行為；`Accepted`／`Pending` 以外視為被拒是依 API 文件的設計 |
| O5 | 關閉視窗時 `MessageBox` 詢問是否在 Revit 內正常顯示（Owner、焦點） | UI 行為 |
| O6 | 外掛啟動時背景清暫存是否拖慢 Revit 啟動（預期只列舉資料夾） | 需實測 |
| O7 | T10 登出：access 過期情境（`/__state.revokedTokens` 增加 2）可用 `POST /__config {"accessTtlSec":10}` 登入、等 15 秒後按登出驗證 | 在 Revit 內走一次 |

### 8.8 對 §4 實機測試清單的補充

- **T7（匯出期間畫面）**：改為觀察 O1～O3；視窗文字現為「匯出進行中：請勿切換文件、編輯模型或關閉專案，直到匯出完成。」
- **T10**：再做一次「`accessTtlSec:10` → 登入 → 等 15 秒 → 登出」，預期 `/__state.revokedTokens` 比登出前多 2。
- **新增 T13**：上傳失敗出現「重試上傳」後直接按視窗右上角 X → 預期跳出「放棄這次上傳並清除？」，選「是」後 `bundles\` 下該資料夾消失、`/__state.deleted` 增加；選「否」視窗留著且可重試。
- **新增 T14**：Revit 中開一個會卡住的對話框（例如未關閉的屬性對話框）後按上傳 → 約 2 分鐘後出現「Revit 目前沒有回應…」，關掉對話框後可立刻再上傳，不需重開 Revit。
