# 報告：Revit 跳轉外掛端整合與實機驗收（imrevitaddinjump）

> 日期：2026-11-18。分支 `imrevitaddinjump`（從 `imaddinmerge` f4abd77 開出）。執行者：Claude Sonnet 5.5（8.ape 帳號派工）。
> 規格：[REVIT_LINK.md](../REVIT_LINK.md)、[ADR D-015](../adr/D-015-revit-link-addin.md)。來源：Issue Manager（IM）`imrevitjump` 分支的 `docs/spec/revit-link.md`、`addin/revit-link/` 草稿、`src/lib/revit-protocol.ts`。
> 證據（截圖、日誌、結果 JSON）：[2026-11-18_imrevitaddinjump_evidence/](2026-11-18_imrevitaddinjump_evidence/)。

## 1. 結論

Alex 要的五件事，**全部在 Revit 2025 實機（Snowdon Towers Sample Architectural 的另存副本）用真的瀏覽器走通**：
網頁→Revit 跳到元件、Revit→網頁開議題草稿、選取元件查議題、版本不同的警告、唯讀限制。
**沒做到、沒驗證的項目寫在第 6 節**（Revit 2024 沒有實際開起來、Revit 忙碌的畫面、大截圖降解析度、Chrome 連正式站）。

## 2. 做了什麼與 commit

| commit | 內容 |
|---|---|
| `6f654b8` merge | 把 `grading-workflow-v2` 併進來（只多一個 commit：`export_family_previews`）。兩邊行尾不同（LF／CRLF）造成 11 個檔整檔衝突：取我方版本後手動補入新工具；工具數與文件計數留到最後重算。 |
| `9f1282f` **spec:**（單獨先交） | `docs/REVIT_LINK.md`、`docs/adr/D-015-revit-link-addin.md`、`docs/SPEC.md` 一列、`DOCUMENT_AUDIENCE_INVENTORY.md` 一列。含「補記（2026-11-18，來源 Alex）」六題答案。ADR 編號：掃全部分支，最大 D-014，取 D-015。 |
| `9ab4727` 實作 | 四個瀏覽器唯讀指令、兩顆功能區按鈕、兩個 Claude 指令、草稿暫存、設定檔、MCP 工具、單元測試、R25 建置修正、文件計數。 |
| `a3382a6` | 設定檔多一個 `browserPath`（指定用哪個瀏覽器開預填頁），實機驗收時用它把網址交給測試用的 Chrome。 |

實作內容（對齊 IM `revit-protocol.ts`）：

- 瀏覽器可呼叫的**唯讀**指令：`viewsync_open_link`、`viewsync_get_selection`、`viewsync_capture_view`、`viewsync_draft_take`（取一次就刪）；`viewsync_status` 的 `model` 多 `revision`（存檔次數:版本 GUID）。`viewsync_draft_put` 只供外掛內部，不在白名單。
- 功能區「Issue Manager」面板：「在 IM 建立議題」「這個元素有哪些議題」（`TransactionMode.ReadOnly`）。MCP 工具 `open_issue_draft`、`open_element_issues`。兩個入口共用 `CommandExecutor.OpenIssueDraft`。回傳的網址不含一次性代碼。
- 設定檔 `%APPDATA%\RevitMCP\revit-link.json`（沒有檔案就全用預設）：`imBaseUrl`、`draftMinutes`、`captureMaxEdgePx`、`captureEnabled`、`browserPath`。
- ElementId 用 `RevitCompatibility` helper，同一份程式同時編 R24（32 位元）與 R25（64 位元）。
- **建置修正**：Release.R25／R26（.NET 8）原本編不過（`RevitGltfExportContext.cs` 用 `System.Drawing`），在 csproj 補 `System.Drawing.Common` 套件（只對非 net48）。
- MCP 工具數 **276 → 279**（原始碼 `registerRevitTools()` 實測；+`export_family_previews`、+2 個新工具）。CLAUDE.md、README×2、DOCUMENT_AUDIENCE_INVENTORY、docs/BIM_MCP 各頁改為 279，`gen-tools-index.py`／`gen-skills-index.py` 重產（並補它們的分類表與寫死的 276）。不照抄 grading-workflow-v2 的 148（那條分支基線不同）。

## 3. 裝在哪個 Revit、備份與還原

| 版本 | 位置 | 狀態 |
|---|---|---|
| **Revit 2025**（新裝） | `%APPDATA%\Autodesk\Revit\Addins\2025\RevitMCP.addin` ＋ `…\2025\RevitMCP\*.dll`（R25 建置，RevitMCP.dll SHA256 前 16 碼 `a7735e5230367878`） | 實機驗收用的就是這個。之前 2025 沒有 RevitMCP。 |
| **Revit 2024**（更新） | `…\2024\RevitMCP\RevitMCP.dll`（R24 建置，前 16 碼 `e0e425a1c2878146`） | 只換 RevitMCP.dll（其餘 DLL 與舊的一致），**沒有實際開 Revit 2024 測**。 |

- 備份：`%APPDATA%\Autodesk\Revit\Addins\2024\RevitMCP\RevitMCP.dll.bak-20261118-imrevitaddinjump`（原 DLL，2026-11-03 15:05 版，前 16 碼 `d386cd4c9a4201c…`）。我**沒有**用 `install-addon.ps1`，因為它會把舊備份輪替到只剩 3 份，會刪掉你 25 份以上的舊備份。
- 另備份：`%APPDATA%\RevitMCP\config.json.bak-20261118-imrevitaddinjump`（測試時暫加了 `http://localhost:3713` 與一台測試配對裝置；**測完已用備份還原**，目前只有正式站、1 台已配對）。測試用的 `revit-link.json` 已刪除。
- **還原舊 DLL**：關閉 Revit 2024，把 `RevitMCP.dll.bak-20261118-imrevitaddinjump` 複製回 `RevitMCP.dll`。要把 2025 的也撤掉：刪 `Addins\2025\RevitMCP.addin` 與 `Addins\2025\RevitMCP\` 資料夾。
- Revit 2025 每次啟動都會跳「無法驗證此增益集的發佈者」（RevitMCP 沒有簽章）；選「載入一次」或「永遠載入」都行。

## 4. 實機驗收（Revit 2025.4，範例副本 `C:\Temp\imrevitaddinjump\Snowdon_test.rvt`，IM 用 `imrevitjump` 樹在 `localhost:3713`、DB 用 docker `imrevitjump_pg`）

測試方式：真的 Chrome（Playwright）連真的 Revit，沒有假外掛。Revit 端的選取與視圖切換用 repo 的 `tools/revit-command.mjs`（正式 MCP 橋）；兩顆功能區按鈕用滑鼠在 Revit 畫面上真的按。

| 項目 | 結果 | 證據 |
|---|---|---|
| **R1 網頁→Revit 跳到元件** | ✅ 網頁從 Revit 目前選取建立連結（牆 `Exterior - 12 5/8"…(Alley)`）；把 Revit 切到「Block 37 - Parapet」並選另一面牆後，按「在 Revit 開啟」：Revit 切回 L1、選取那面牆（讀回 `get_active_view`＝L1、`get_selected_elements`＝619550） | `issue-desktop-link-added.png`、`revit_after_buttons.png`（藍色牆）、`results.json`、日誌 `viewsync_get_selection`／`viewsync_open_link` |
| R1 退化四種 | ✅ ①元素在連結的視圖看不到→`hidden_in_view`，自動切到看得到的視圖並選取；②元素已不存在→退到視圖 L1；③元素與視圖都不存在→退到圖紙 A101；④全部不存在→「這個連結已失效」 | `issueB-row0…3-*.png`、`results.json`(`s7`) |
| **R2 Revit→網頁開議題草稿** | ✅ ①Claude 指令 `open_issue_draft`：回 `{opened,kind,url(不含代碼),elementCount:1,pairedWebDevices}`；②功能區「在 IM 建立議題」真的按了，日誌 21:09:28 `建立了一份草稿（create），元素數 1`；網頁預填頁領到草稿：模型 Snowdon_test、視圖 L1、圖紙 A101、1 個牆元素、有視圖截圖 | `draft-desktop.png`、`revitmcp-log-excerpt.txt` |
| **R3 選取元件查議題** | ✅ 功能區「這個元素有哪些議題」真的按了（日誌 21:08:13 `lookup`）；網頁顯示「找到 2 個議題」#1、#2（兩個議題都連到這面牆） | `lookup-desktop.png` |
| **R4 版本不同的警告** | ✅ 連結建立時版次 `125:8c757e9c…`；用 `save_document` 存檔後再按：「已跳轉」＋「模型版本不同：這個連結是在較舊的模型版本建立的，位置可能已變動」，**仍然跳轉**（預設 `continue`） | `issue-desktop-revision-warn.png` |
| **R6 唯讀限制** | ✅ ①瀏覽器直送 `viewsync_draft_put` 與 `create_wall`→`command_not_allowed`；沒 token 領草稿→`token_missing`；`viewsync_status` 回有 `revision`。②整個驗收期間樓板平面視圖數維持 71，沒有新增視圖。③Revit「復原」清單只有一筆「建立位移集元素」——那是我**第一次點功能區時點錯位置**（在配對之前，點到 `修改｜位移集` 頁籤的按鈕），**不是外掛造成**；外掛的跳轉、草稿、截圖、選取之後沒有新增任何可復原項目。④單元測試掃原始碼：本功能不含 `Transaction`、建立視圖、套用暫時設定。 | `results.json`(`s9`)、`revit_undo_list.png` |
| 未配對的白話說明 | ✅ 沒配對時網頁顯示「還沒和 Revit 配對」 | `web-unpaired.png` |
| 網頁截圖尺寸 | 桌面 1280×800、手機 390×844，`scrollWidth`／`scrollHeight` 都等於視窗（無橫向溢出）。手機版沒有「在 Revit 開啟」 | `web-metrics.json`、`issue-mobile.png` |

離線測試：`dotnet test MCP.Tests` **837 通過、2 略過、0 失敗**（含新增 `RevitLinkTests`）；Release.R24 與 Release.R25 都 0 錯誤。QA/QC（`verify-qaqc.ps1 -SkipBuild -SkipDeploy`，需 UTF-8 主控台）72 PASS、2 FAIL（見第 6 節，皆非本件造成）。

## 5. 未解項與已知限制

1. **沒有實際開 Revit 2024**（只編譯、複製了 DLL）。
2. 沒測「Revit 正忙」（對話框開著時）的畫面；沒測截圖超過 5 MB 時的自動降解析度（範例平面圖截圖遠小於 5 MB）。
3. 沒測 Chrome 對 https 正式站呼叫 localhost（IM 規格 R6 的原意）；本次用 `http://localhost:3713`。
4. `model_guid` 只在本機檔取到（`46ec4172-…`）；中央檔（工作共享）沒測。
5. 功能區按鈕「在 IM 建立議題」真的按了，但按完那一次的預填頁沒有再用網頁領取一次（Claude 指令那一次已完整走通）。
6. 版本警告太吵的問題仍在：版次是「存檔次數」，只要模型存過檔，舊連結每次跳轉都會出現提醒（Alex 已決定先警告仍跳轉，IM 也留了設定關掉或改成擋住）。
7. IM 端文字小問題：退到圖紙時畫面寫「已改為開啟視圖『First Floor Plan』」，實際是圖紙。

## 6. 非本件造成、需有人處理的

- QA/QC `3-4`：`MCP.Tests/IssueManager/SheetsOnlyAttachTests.cs` 有兩處寫死帳號名（`alex`、`other`）——來自 imsheetsonly，不是這次改的。
- QA/QC `7-17`：`docs/BIM_MCP/shared.js` 的聚會報名連結日期 2026-10-24 已過期。
- `verify-qaqc.ps1` 的 `tools-index descriptions` 檢查在中文主控台（cp950）會誤報失敗（node 輸出被誤解碼）；用 `[Console]::OutputEncoding=UTF8` 就過。
- `Alex_Diary/3_system/1_config/91_services/ports.json` 我加了一筆 3713（已標釋放）；那個檔案本來就有別人未提交的修改，我沒有 commit。

## 7. 待 Alex 決定

1. **要不要在 Revit 2024 也實測一次？** 例如：你的辦公室電腦平常用 2024 開專案，我可以在這台開 2024 照同樣步驟走一遍；目前只保證 2024 能編譯。
2. **RevitMCP 沒有簽章，每次啟動 Revit 2025 都會問「載入一次」還是「永遠載入」。** 你要選「永遠載入」嗎？（選了之後不會再問，但表示信任這個檔案。）
3. **版本不同的提醒**：例如 3F 平面圖的連結是上週建的、之後模型存過十次，每次按都會多一行提醒。要先照現在這樣，還是改成「只在模型結構變動才提醒」（要改版次的算法，需另外評估）？
4. 兩個舊的 QA/QC 失敗（寫死帳號名、聚會日期過期）要由誰處理？

## 8. 環境與殘留

- IM dev server（3713）已停止（只停自己的 node 程序）；`ports.json` 3713 標已釋放。docker `imrevitjump_pg`（5701）保留給獨立複驗，內有本次的測試資料（專案名稱 `Snowdon 實機驗收 …`）；用完可 `docker rm -f imrevitjump_pg`。
- Revit 已關閉（範例副本不存檔；副本與測試暫存在 `C:\Temp\imrevitaddinjump\`）。
- IM 樹 `imrevitjump` 沒有留下任何變更（暫時的測試腳本已刪、`next-env.d.ts` 已還原）。
