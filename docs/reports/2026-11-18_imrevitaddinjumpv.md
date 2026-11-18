# 獨立驗收報告：Revit 跳轉外掛端（imrevitaddinjumpv）

> 日期：2026-11-18。驗收者：Claude Sonnet 5.5（7.alex 帳號；建造者是 8.ape 帳號，不同帳號、不同對話）。
> 驗收對象：分支 `imrevitaddinjump`，commits `6f654b8`（merge）、`9f1282f`（spec）、`9ab4727`（實作）、`a3382a6`（browserPath）、`181e9bc`（報告）；決策 [ADR D-015](../adr/D-015-revit-link-addin.md)，規格 [REVIT_LINK.md](../REVIT_LINK.md)。
> 驗收樹：本 worktree（分支 `imrevitaddinjumpv`，起點 `181e9bc`）。只新增本報告與一筆 log，**沒有改建造者的任何程式**。
> SPEC_FIRST_RULE：repo 內沒有 `docs/SPEC_FIRST_RULE.md`，改讀 `C:\Temp\grokbot_sessions\SPEC_FIRST_RULE.txt`。

## 1. 結論：PASS-with-issues

- **阻擋（FAIL）：0 項。**
- 非阻擋：7 項（N1～N7），其中 **N1（頁頂橫幅還寫「外掛端還沒整合」）確實誤導**，要在 Issue Manager（IM）端改；N2 回答你問的「#2 為什麼算這面牆的議題」。
- 沒做的：**沒有重新開 Revit 實機重跑**（理由見 §5）。實機部分我只能驗「證據」與「已裝 DLL 就是這個 commit 編出來的」，沒有親手再按一次按鈕。

## 2. 派工者點名的兩個問題

| 項 | 判定 | 白話說明 |
|---|---|---|
| (a) 「這個元素有哪些議題」頁頂橫幅仍寫「外掛端還沒整合進 Revit、沒有實機驗證」 | **誤導，成立（N1）** | 橫幅文字不是畫面臨時寫的，是 IM 程式裡的預設文字：`issue-manager` 的 `imrevitjump` 分支 `src/lib/feature-status.ts:68`（`revitLink` 那一列，狀態 `testing`）。它是在「外掛還沒做」時寫的，現在外掛已整合、並在 Revit 2025 走通，所以「還沒整合、沒有實機驗證」是**錯的**。建議改成例如：「議題與 Revit 互相跳轉（Revit 2025 實機驗證過，2026-11-18）；Revit 2024、中央檔（工作共享）、Revit 忙碌畫面還沒驗證。」（「測試中」徽章本身仍然正確，不用改。）這個設定可在 IM 的 `/admin/feature-status` 由管理者改，所以屬於 IM 端待辦，不影響外掛端結論。 |
| (b) 同頁把「#2 另一件議題（沒有 Revit 連結）」列為這面牆的相關議題 | **配對是對的，是測試資料的標題誤導（N2）** | 我直接查了驗收用的資料庫（docker `imrevitjump_pg`，唯讀查詢）：議題 #2 **真的有一筆**連到同一面牆的連結（`revit_links`：`kind=element`、`element_unique_id=50f92000-…-0009741e`、`model_guid=46ec4172-…`、`source=from_revit`，建立於 21:06:43；#1 的連結建立於 21:04:59）。兩個議題是同一時間（21:03:04）當測試資料建立的，#2 的標題「沒有 Revit 連結」是**建立當下的描述**，後來建造者的測試步驟（在 Revit 移到「Block 37 - Parapet」再「加入 Revit 連結」，見截圖 `issueB-all-jumped.png`：#2 的主要連結視圖名就是 Block 37 - Parapet）又替 #2 加了這面牆的連結，標題沒改。IM 查詢 `lookupIssuesByElement`（`src/db/revit-link-queries.ts:226`）是「連結表內元素 UniqueId 相符」才列出，所以**不是錯誤配對**。但這讓畫面上出現「標題寫沒有連結卻被列出」，看起來像 bug；而且**這個測試沒有反例**（見 N3）。 |

## 3. 清單 1～9 項逐項結果（建造者結果檔末段「給獨立複驗的檢查清單」）

| # | 項目 | 結果 | 證據 |
|---|---|---|---|
| 1 | git 順序、D-015 撞號、衝突標記 | **PASS** | `git log --reverse 6f654b8..HEAD`：`9f1282f spec:`（20:42）先於 `9ab4727` 實作（20:55）。掃全部本機分支的 `docs/adr/`：`D-015-revit-link-addin.md` 只出現在 `imrevitaddinjump` 與本驗收分支（同一個檔），沒有第二個 D-015 檔。`git ls-files` 掃衝突標記（`<<<<<<<`／`=======`／`>>>>>>>`）：無。merge `6f654b8` 只動 7 檔（580 行加、2 行減），FamilyPreviews.cs、family-preview-tools.ts、planting-families.html 三個檔與 gwv2 的 `e7679f7` 逐位元相同。（另見 N5：D-015 這個編號在 `docs/camera-manager/adr.zh-TW.md` 另一套編號裡也有。） |
| 2 | 建置與測試 | **PASS** | 我自己跑：`dotnet build -c Release.R24 RevitMCP.csproj` → 0 錯誤（4263 警告，都是既有型別警告）；`-c Release.R25` → 0 錯誤。`dotnet test MCP.Tests/RevitMCP.Tests.csproj` → **通過 838、略過 2、失敗 0**（建造者寫 837，多 1，見 N6）；只跑 `RevitLink` 篩選：46 個全過。log：`C:\Temp\grokbot_sessions\imrevitaddinjumpv_build.log`。 |
| 3 | 唯讀掃描 | **PASS** | 三個檔（`CommandExecutor.RevitLink.cs`、`CommandExecutor.RevitLinkDraft.cs`、`MCP/Commands/RevitLinkCommands.cs`）＋草稿暫存與設定檔，我用 grep 掃 `Transaction`／`ViewSyncCreate`／`ViewPlan.Create`／`.Delete(`／`.Commit(`／`viewsync_create_view`／`viewsync_apply_temp`：**只有註解**與兩個 `[Transaction(TransactionMode.ReadOnly)]` 屬性命中（`RevitLinkCommands.cs:65、72`）。唯一的 `.Create(` 是 `RandomNumberGenerator.Create()`（產代碼）。截圖用的 `ExportViewToImage` 只呼叫 `doc.ExportImage`＋暫存資料夾，沒有 Transaction。`ViewSyncSecurity.cs` 的 `Authorized` 加了 `OpenLink、GetSelection、CaptureView、DraftTake` 四個，`DraftPut` 只是常數、**不在** `Authorized`；`CommandExecutor.cs` switch 沒有 `viewsync_draft_put` 的 case。`__browser` 旗標由 `SocketService.cs:806` 無條件覆寫，瀏覽器不能自己宣稱不是瀏覽器而拿到本機路徑。單元測試 `RevitLinkTests` 含白名單、`DraftPut_IsNotAnExecutorSwitchCase`、原始碼掃描。 |
| 4 | 計數 279 與索引頁 | **PASS** | `cd MCP-Server; npm ci; npm run build` 成功；`registerRevitTools()` 實數 **279**（log 印 `loaded 279 tools`）；`open_issue_draft`、`open_element_issues` 都在，`readOnlyHint=true`、`destructiveHint` 無。`python scripts/gen-tools-index.py --check` → OK；`gen-skills-index.py --check` → OK。`CLAUDE.md:40`、`README.md:36`、`README.zh-TW.md:36`、`docs/DOCUMENT_AUDIENCE_INVENTORY.md:18` 都是 279。`verify-qaqc.ps1 -SkipBuild -SkipDeploy`（UTF-8 主控台）：2 FAIL、3 WARN，**與建造者回報相同**：`3-4`（`SheetsOnlyAttachTests.cs:273、401` 寫死帳號名 `alex`，在基底 `f4abd77` 就有，建造者沒動）、`7-17`（`shared.js` 聚會日期 2026-10-24 過期，建造者只改了同檔一行 276→279）。7-15（工具與派送一致）、9-3（readOnlyHint 誠實）都 PASS。 |
| 5 | 協定欄位對 IM `revit-protocol.ts` | **PASS** | 對 `issue-manager/.worktrees/imrevitjump` 的 `src/lib/revit-protocol.ts`、`src/lib/revit-link.ts`：指令名稱四個新指令一致；`revisionPolicy` 外掛吃 `continue／block／sheet_only`，IM 送 `warn→continue`、`block`、`sheet_only`（`revit-link.ts:306`）；`openLink` 回覆 `level／found／missing／hintUsed／view.name／selectedCount／modelRevision／modelRevisionDiffers／warnings` 外掛都有，IM `parseOpenLinkResult` 照這些欄位讀；`get_selection` 回覆 `model{title,guid,path,revision}`、`view{uniqueId,name,type,sheetNumber}`、`elements[{uniqueId,elementId(字串),category,name,level}]` 與 IM `parseSelection` 一致；`draft_take` 回覆多 `intent`、`image{mime,dataBase64,width,height}` 與 `parseDraft` 一致；錯誤碼 `selection_empty、capture_failed、draft_expired、draft_not_found、model_revision_differs、no_document` 都在 IM 兩份清單內。`open_issue_draft`／`open_element_issues` 回傳的 `url` 是 `baseUrl+page`，**不含 code**（`CommandExecutor.RevitLinkDraft.cs:79`）；日誌只寫「建立了一份草稿、元素數 N」，證據日誌也確實沒有代碼。 |
| 6 | 已裝 DLL 與建置一致 | **PASS** | 我自己用 HEAD 重編：`MCP/bin/Release.R25/RevitMCP.dll` sha256 前 16 碼 `a7735e5230367878` ＝ 已裝 `Addins\2025\RevitMCP\RevitMCP.dll`；`Release.R24` 前 16 碼 `e0e425a1c2878146` ＝ 已裝 `Addins\2024\RevitMCP\RevitMCP.dll`（編譯是可重現的，所以兩個已裝 DLL 就是 `181e9bc` 的程式碼，包含 `browserPath`）。`Addins\2025\RevitMCP.addin` 存在（`AddInId` 與 repo 的 `MCP/RevitMCP.addin` 相同＝`090a4c8c-…`、路徑是相對的 `RevitMCP\RevitMCP.dll`）；2025 資料夾 9 個檔。備份 `…\2024\RevitMCP\RevitMCP.dll.bak-20261118-imrevitaddinjump` 存在，前 16 碼 `d386cd4c9a4201c…` 與報告一致。`%APPDATA%\RevitMCP\config.json` 與 `config.json.bak-20261118-imrevitaddinjump` 逐行相同（已還原）；`revit-link.json` 不存在（已刪）。 |
| 7 | evidence 截圖與 results.json | **PASS（附 N7）** | 我逐張看了 `lookup-desktop`、`draft-desktop`、`issue-desktop-revision-warn`、`issue-mobile`、`issueB-all-jumped`、`revit_ribbon`、`revit_undo_list`，並讀 `results.json`、`web-metrics.json`。`s7` 四種退化：`hidden_in_view`、退到視圖 L1、退到圖紙（文字寫 First Floor Plan）、連結已失效——文字與報告一致。`s8` 版本不同：「已跳轉＋模型版本不同」仍跳轉。`s9`：`viewsync_draft_put`、`create_wall`→`command_not_allowed`；沒 token 領草稿→`token_missing`；`viewsync_status` 有 `revision`。功能區截圖看得到「Issue Manager」面板三顆按鈕：「上傳到 Issue Manager」「在 IM 建立議題」「這個元素有哪些議題」。Revit 復原清單只有「建立位移集元素」一筆（建造者自述點錯位置，在配對之前）。`web-metrics.json`：14 筆桌面 1280×800、手機 390×844，`scrollWidth／scrollHeight` 皆等於視窗；手機版沒有「在 Revit 開啟」。**我沒有重新量**（IM 伺服器已停，見 §5）。 |
| 8 | Revit 實機重跑 | **未重跑（可選）** | 見 §5。 |
| 9 | 環境殘留 | **PASS（附觀察）** | docker `imrevitjump_pg`（5701）仍在、`Up 2 hours`，我只做了 `select` 查詢。**觀察**：建造者寫「Revit 已關閉」，但我驗收時 `Revit.exe`（PID 60248）與 `RevitAccelerator.exe` 正在執行；不是我開的，我沒碰。 |

## 4. Alex 2026-11-18 決定逐條對照

| 決定 | 結果 | 依據 |
|---|---|---|
| Q1 功能區按鈕＋Claude 指令都做 | **PASS** | 功能區兩顆按鈕（截圖）；MCP 工具 `open_issue_draft`、`open_element_issues`（279 計數實測）；兩個入口共用 `OpenIssueDraft(kind)`。 |
| Q2 選元件查議題 | **PASS（有保留）** | `kind="lookup"` 開 `/revit/lookup`；IM 查詢只回內部成員看得到的未結議題。保留：測試沒有反例（N3）。 |
| Q3 版本不同每次跳轉都警告、仍跳轉 | **PASS** | `revisionPolicy` 預設 `continue`；版次 `NumberOfSaves:VersionGUID`；`issue-desktop-revision-warn.png` 與 `s8`：存檔後再跳 → 已跳轉＋警告。程式 `CommandExecutor.RevitLink.cs:93-98`：只有 `block` 才擋。 |
| Q5 imperms 前外部看不到、永不給識別碼與跳轉 | **外掛端中立；IM 端未獨立驗** | 外掛不分身分，只服務已配對的內部瀏覽器；識別碼只走本機 WebSocket。IM 查詢的 `memberships.role in INTERNAL_ROLES` 條件我讀過程式，但「外部帳號實測看不到」不在本次驗收範圍，沒驗。 |
| 唯讀（不建視圖、不套暫時設定） | **PASS** | 見清單 3；另 `viewsync_apply_temp`／`create_view` 本來就在既有白名單（imviewsync），本件沒有放寬也沒有引用。 |

## 5. 為什麼沒有重新開 Revit 實機

Revit 2025 載入未簽章的 RevitMCP 時每次都會跳「無法驗證發佈者」對話框，需要有人點「載入一次」；此刻 `Revit.exe` 已有一個實例在跑（不是我開的，可能是 Alex 或別的工作階段在用），我不去關它或另開。取而代之的獨立證據：①已裝 DLL 與 HEAD 重編**逐位元相同**（清單 6）；②程式碼逐行讀過（清單 3、5）；③46 個單元測試與全部 838 個測試重跑通過；④我直接查驗收用的資料庫，連結資料與網頁畫面一致（§2 (b)）。**實機行為（切視圖、選取、截圖）本身我沒有重新觸發**，仍然只是建造者的實機證據。

## 6. 非阻擋問題

| # | 問題 | 白話例子與建議 |
|---|---|---|
| **N1** | IM 頁頂橫幅文字過時（誤導） | `/revit/lookup`、`/revit/draft` 頂端寫「外掛端還沒整合進 Revit、沒有實機驗證」，但外掛已整合且 Revit 2025 實測過。使用者看到會以為按下去不會動。改 IM `src/lib/feature-status.ts:68`（或在 `/admin/feature-status` 改），文字見 §2 (a)。**IM 端待辦。** |
| **N2** | 測試資料標題與實際不符 | 議題 #2 標題寫「沒有 Revit 連結」，其實有。下次驗收前把標題改成「第二件議題（同一面牆）」，或另外新增一件**真的沒有連結**的議題。 |
| **N3** | 「選元件查議題」缺反例 | 驗收只證明「有連結的 2 件議題都被找到」，沒證明「沒連結的議題不會出現」、「同一 UniqueId 但別的模型 GUID 的連結不會出現」。例如：再開議題 #3（不加連結）、議題 #4（連到另一個模型 GUID 的同一 UniqueId），lookup 應仍只回 #1、#2。（從 `lookupIssuesByElement` 程式碼看，應該會過濾，但沒有實測證據。） |
| **N4** | `CLAUDE.md:500` 的 DLL 數過時 | 寫「R25/R26 emit **8**」，但這次為了 `System.Drawing.Common` 加了套件，R25 現在輸出 **9** 個 DLL（我實數：`MCP/bin/Release.R25` 9 個，R24 仍 13 個）。Phase 5 的檢查是列舉實際輸出，不會失敗；只是那段說明文字沒跟著改（`CLAUDE.md` 其他地方也沒提 System.Drawing.Common）。 |
| **N5** | 「D-015」編號在 repo 內有兩套 | 建造者只掃了 `docs/adr/`。`docs/camera-manager/adr.zh-TW.md` 是另一套編號（D-001～D-046），也有 D-015（剖面重設）。`docs/adr/` 的 D-009～D-014 本來就與它重疊，是既有慣例，所以不算撞號；但 CHANGELOG 與 commit 裡單寫「D-015」會有歧義，建議寫成「ADR D-015（Revit 跳轉）」。 |
| **N6** | 報告測試數差 1 | 報告寫「837 通過」，我跑出 838。多一個測試通過，不影響結論；報告數字略舊。 |
| **N7** | 「平面圖視圖數驗收前後都是 71」只有前、沒有後 | 建造者暫存資料夾 `C:\Temp\imrevitaddinjump\req\v.out.json`（21:02）只有一筆 `Count: 71`，沒有第二次讀數，repo 的 evidence 資料夾也沒有。另外「模型沒有變成已修改」沒有直接讀 `IsModified`／標題星號（又因為建造者自己誤點產生一筆復原項目，那份副本本身就不乾淨）。靜態掃描＋`TransactionMode.ReadOnly`＋復原清單足以支持「外掛沒改模型」，但**直接證據缺**；下次實機驗收請在跳轉前後各讀一次視圖數與 `IsModified` 並存進 evidence。另 ADR D-015 第 9 點設定清單沒有 `browserPath`（`REVIT_LINK.md` §4 有），建議補。 |

## 7. 待 Alex 決定／處理

1. 要不要在 Revit 2024 也實測一次（目前 R24 DLL 只編譯＋複製，沒有開過）。
2. IM 端把 N1 橫幅文字改掉（誰改、何時改）。
3. 建造者提出的其他三題（RevitMCP 無簽章是否選「永遠載入」、版本提醒太吵是否改算法、兩個舊 QA/QC 失敗誰處理）維持待決，本次驗收沒有新資訊。

## 8. 環境與還原

- 我只做：`dotnet build`（寫進本 worktree 的 `MCP/bin`、`obj`，被 `.gitignore` 排除）、`npm ci`＋`npm run build`（`MCP-Server/node_modules`、`build`，同樣被忽略）、`dotnet test`、`verify-qaqc.ps1`、docker `select` 唯讀查詢。沒有改 Revit 設定、沒有動 `%APPDATA%`、沒有啟停任何服務、沒有 push／merge。
- 過程檔：`C:\Temp\grokbot_sessions\imrevitaddinjumpv_build.log`、`imrevitaddinjumpv_npm.log`、`imrevitaddinjumpv_qaqc.log`（不進 git）。
