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
