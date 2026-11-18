# 報告：QA/QC 清理的獨立驗收（imrevitqafixv）

> 日期：2026-11-18。驗收者：Claude Sonnet 5.5（7.alex 帳號，與建造者 8.ape 不同帳號）。
> 被驗：分支 `imrevitqafix`，commit `216f268`（基底 `6be1f86`）。驗收分支 `imrevitqafixv` 指向同一個 commit，本報告是唯一新增的 commit。
> 依據：建造者報告 `docs/reports/2026-11-18_imrevitqafix.md` 與派工者的檢查清單 1～8。**驗收者沒有改任何建造者的程式或文件，問題只記錄。**

## 1. 結論：FAIL（1 個阻擋，修法一行）

建造者說「QA/QC 全綠、FAIL 0」。我在乾淨的 commit `216f268` 上用 `pwsh` 7＋UTF-8 主控台重跑 `verify-qaqc.ps1 -SkipBuild -SkipDeploy`，結果是 **PASS 73／FAIL 1／WARN 3／SKIP 3**，不是全綠。失敗的正是這件要修的 `3-4`：建造者**寫報告時又把兩個帳號名寫回 repo**。其餘 7 項都過，另有 6 個非阻擋小項。

## 2. 檢查清單逐項結果

| # | 項目 | 結果 | 證據 |
|---|---|---|---|
| 1 | commit 範圍：`6be1f86..HEAD` 只有 `216f268`；只動預期的 11 個檔，沒碰 `MCP/` 外掛原始碼與 `MCP-Server/src` | **PASS** | `git log 6be1f86..HEAD` 一筆；`git show --stat`：CHANGELOG.md、CLAUDE.md、`MCP.Tests/IssueManager/SheetsOnlyAttachTests.cs`、`docs/BIM_MCP/shared.js`、REVIT_LINK.md、SPEC.md、ADR D-015、imrevitaddinjump 報告、decisions log、imrevitqafix 報告、`log/2026-11.md`，共 11 檔、105 增 11 刪 |
| 2 | `git diff 6be1f86 -- MCP.Tests` 只有兩行字串 | **PASS** | 只動兩行（檔名 AR-CPC_other.rvt 與 AR-CPC_alex.rvt 的那兩個測試路徑字串）：把裡面的帳號資料夾名換成 `<OTHER_MACHINE_USER>`。兩個斷言都只比檔名與中央模型路徑，語意不變；測試仍過（見第 4 項） |
| 3 | `verify-qaqc.ps1 -SkipBuild -SkipDeploy` 要 FAIL 0 | **FAIL（B1）** | 73 PASS／**1 FAIL**／3 WARN／3 SKIP。失敗＝`3-4`（細節見 §3 B1）。`7-17`、`7-14`、`7-15`、`7-16`、`7-18`、`9-1～9-3` 都 PASS。3 個 WARN 都是既有的：`check_sanitary_fixture_requirements` 隔離名單、兩個 rollback-only 探針。先決條件：這個 worktree 原本沒有 `node_modules`，我跑了 `npm ci`＋`npm run build`（被 `.gitignore` 排除，不進 git） |
| 4 | `dotnet test -c Release.R24` 與 `-c Release.R25` 各一次，838 過 | **PASS** | R24：`失敗 0，通過 838，略過 2，總計 840（48 s）`；R25：`失敗 0，通過 838，略過 2，總計 840（55 s）`。兩次輸出都標 `RevitMCP.Tests.dll (net48)`（見 N5） |
| 5 | `dotnet build -c Release.R25` 後 `MCP/bin/Release.R25/*.dll` 是 9 個 | **PASS** | 實數 9：ClosedXML、DocumentFormat.OpenXml、ExcelNumberFormat、Irony、Newtonsoft.Json、RevitMCP、SixLabors.Fonts、System.Drawing.Common、XLParser。清單與建造者報告一致。補編 R26 也是 **9** 個（同一份清單），見 N1 |
| 6 | decisions log 對 BACKLOG 抽驗，無杜撰；Q6 時間與 hld070 細節標「缺」是否合理 | **PASS（有 2 個小瑕疵 N2、N3）** | 見 §4 對照表。每一條都能在 BACKLOG 或 `REVIT_LINK.md` 對照表找到出處，沒有發現杜撰的決定 |
| 7 | `shared.js` 空值後，網頁 `[data-meetup-link]` 顯示「目前未開放報名」 | **PASS（桌機）／手機版面見 N4** | 見 §5 |
| 8 | ADR D-015 第 9 點的 `browserPath` 描述與 `REVIT_LINK.md` 設定表一致 | **PASS** | ADR 第 9 點列 5 個鍵（草稿分鐘、截圖長邊、是否截圖、IM 網址、`browserPath`）；`REVIT_LINK.md:82-87` 設定表正是這 5 列，`browserPath` 說明「指定用哪個瀏覽器開預填頁（執行檔完整路徑，網址當唯一引數）；檔案不存在時改用預設瀏覽器並記一行日誌」。兩處語意相同 |

## 3. 阻擋（FAIL）

### B1　建造者報告自己把 2 個寫死帳號名寫回 repo，`3-4` 又紅了

- 現象：`verify-qaqc.ps1` 的 `3-4`（掃描所有被追蹤文字檔裡的家目錄路徑）回 `2 hardcoded path(s)`：
  - `docs/reports/2026-11-18_imrevitqafix.md` 的第 14 行（表格「舊 QA/QC 失敗 1」那一列），內文為了說明「改前長怎樣」，把改掉前的兩個帳號資料夾原樣抄了一次，掃描器判成「家目錄路徑裡的帳號名」。
- 為什麼建造者沒發現：報告檔是改完測試字串**之後**才寫的，而 `3-4` 只掃 `git ls-files`，當時報告還沒 `git add`，所以那次跑是綠的；提交後才被追蹤到。
- 連帶影響：`log/2026-11.md` 與建造者報告都寫「QA/QC 全綠、FAIL 0」，以提交後的實際狀態來看**不成立**。
- 修法（驗收者沒動，交給建造者／派工者）：把報告第 14 行那兩個「改前」路徑改成文字描述（例如「兩處測試字串裡寫死的帳號資料夾名」），不要出現「家目錄資料夾＋帳號名」的路徑字串；改完再跑一次 `3-4`。
- 驗證建議：改完後須再跑一次完整 `verify-qaqc.ps1`，確認 FAIL 0。

## 4. decisions log 對 BACKLOG 的抽驗（清單第 6 項）

BACKLOG 檔 `C:\Temp\grokbot_sessions\BACKLOG.md` 仍在被續寫，建造者引用的行號（2130～2152）現在已落在 **2135～2157**（上方有插入）。我用時間戳與關鍵字重新定位，逐條對：

| decisions log 條目 | BACKLOG／來源（現行行號） | 結果 |
|---|---|---|
| A. IM Q1～Q5，20:25～20:27 Alex 回覆 | 2149「依 [PARENT] 20:25～20:27（Alex Q1～Q5 已答）」；各題答案內容在 `docs/REVIT_LINK.md` 開頭對照表（逐題比對一致） | 有據 |
| A. IM Q6 IFC 跳轉，回覆時間缺 | BACKLOG 只寫 Q1～Q5；Q6 答案只在 `REVIT_LINK.md` 表中 | 有據，標「缺」合理 |
| A. Q3 每次提醒（Alex 11-18 已定） | 2155「Q3 版本提醒→維持每次提醒（Alex 11-18 已定）」 | 有據 |
| B. 17:32 imrevitjump 下一輪立刻開 | 2135、2136「[PARENT 17:32] 要求下一輪立刻開」；「spec 先寫入 Q1～Q6 再實作」取自 2135 任務欄 | 有據 |
| B. 17:47 worktree 一律開在 repo 內 `.worktrees/`＋`.git/info/exclude` | 2136「依 Alex 17:47 規則…（已加 .git/info/exclude）」 | 有據 |
| B. 20:25 開 imrevitaddinjump（2025 優先、2024 保持可用、R1～R4、R6） | 2140、2149 | 有據 |
| B. 20:26 imifcfloorfix（樓板 13/39、開口 0/167）與 imnextsec | 2141、2147 | 有據 |
| C. 21:33 代決：Q1 Revit 2024 實測併進 imifcfloorfix、Q3 維持提醒、Q4 開 imrevitqafix 並寫 `docs/decisions` | 2155 | 有據 |
| C. 21:46 非阻擋項自行決定：N1→imrevitjumpn、N4～N7→imrevitqafix | 2157 | 有據 |
| D. 仍待決：「永遠載入」、hld070 70-1～70-4 | 2155「待 Alex：外掛 Q2『永遠載入』、hld070 70-1～70-4」 | 有據，沒有被代決 |

## 5. 網頁按鈕實測（清單第 7 項）

做法：用 `python -m http.server 5187 --bind 127.0.0.1` 服務 `docs/BIM_MCP/`（5187 在 `ports.json` 的 5170～5199 範圍內、未登錄也未被占用，只是暫用），開本機 headless Chrome 157（除錯埠 5188，用乾淨的暫存使用者資料夾），以 DevTools 協定設定桌面 1280×800 與手機 390 寬、載入 `index.html`、等 2.5 秒讀 DOM、截圖。用完已停掉自己的兩個程序，兩個埠都已釋放，沒有動 node.exe。

| 量測 | 桌機 1280×800 | 手機 390 |
|---|---|---|
| `[data-meetup-link]` 元素數 | 2（頂端導覽＋頁尾） | 2 |
| 文字 | 兩個都是「📝 目前未開放報名」 | 同 |
| `href` | 無（已移除） | 無 |
| `aria-disabled` / `is-disabled` | `true` ／ 有 | 同 |
| `title` | 「下一場小聚的報名連結尚未公布」 | 同 |
| `__meetupSignupEffective()` | `{url:"", eventDate:""}` | 同 |
| `scrollHeight`（整頁） | 6433 px | 11075 px |
| 水平溢出（`scrollWidth` vs `clientWidth`） | 1265 = 1265，無 | 390 = 390，無 |

- `scrollHeight` 是這個長篇首頁本身的總長度（沒有固定高度容器，也沒有橫向捲動），不是多餘捲動。
- 截圖：存在驗收者的使用者家目錄下（檔名開頭 `imrevitqafixv_`）：`imrevitqafixv_desktop_btn.png`、`imrevitqafixv_mobile_btn.png`（導覽列）、`imrevitqafixv_desktop_full.png`、`imrevitqafixv_mobile_full.png`（整頁）。桌機導覽列上按鈕為灰色、無 hover 連結，頁面其他部分正常。

## 6. 非阻擋問題

- **N1　R26 也是 9 個 DLL，`CLAUDE.md:500` 可以寫實數。** 我補編 `Release.R26` 實數 9（同 R25 那份清單，含 `System.Drawing.Common`）。目前文字寫「`Release.R26` was not re-counted」且後面接著「Both are correct」，只剩兩個數字（13 與 9）的說法已經不精確。建議改成「R25／R26 各 9」並刪掉「未重新計數」。
- **N2　decisions log 引用的 BACKLOG 行號會漂移。** 建造者引的 2144、2150、2152 現在是 2149、2155、2157（BACKLOG 持續被改）。例：日後有人照「第 2150 行」找，會找到別的任務。建議改用「2026-11-18 21:33 那一筆」這種時間戳定位，或在檔頭註明「行號為抄錄當下」。
- **N3　decisions log 有兩處出處標得不準。**
  - D 段寫 hld070 的 70-1～70-4「BACKLOG 該行文字在此截斷」——其實該行**沒有被截斷**，BACKLOG 就只寫了「70-1～70-4」，沒列細節；細節在 Alex_Diary 的 hld070 報告與 `docs/wait_alex.md` 十五（見 `hld070_result.txt` 第 6、15 行，例如 70-1 是「三房型優先排邊間」其實是建築師座談發言、不是規範）。「細節缺」的結論合理，但原因與出處寫錯，且其實查得到。
  - C 段「目前 R24 DLL 只編譯＋複製、沒有開過 Revit 2024」與 D 段 Q2 的「無法驗證發佈者」描述，出處是 `imrevitaddinjump_result.txt`（及該件報告第 7 節），不是 BACKLOG 第 2150 行，標示為 BACKLOG 來源不夠精確。
- **N4　手機 390 寬的頂端導覽列擠成多行。** 截圖 `imrevitqafixv_mobile_btn.png`：「安裝」「目前未開放報名」「淺色」三顆都折成兩行，導覽列看起來疊到下方「00 / 04 頁首」那一列。這不是本次改動引入的（舊設定的 2026-10-24 日期早已過期，按鈕在 10-25 起就是同一個停用文字），但這次把它變成「正式常態」，建議之後把停用時的文字縮短（例：「未開放報名」）或手機版隱藏。
- **N5　R24 與 R25 的測試輸出都標 `RevitMCP.Tests.dll (net48)`。** 也就是「R24／R25 各 838」目前是同一個 net48 測試目標跑了兩次（設定只換編譯組態），並不代表 R25（.NET 8）的外掛組件被載入測過；DLL 計數與建置成功才是 R25 的實際證據。建造者報告沒有宣稱更多，只是提醒日後別把這個數字當成 R25 執行期覆蓋。
- **N6　`log/2026-11.md` 與建造者報告「QA/QC 全綠」的說法在提交後不成立**（同 B1，修好 B1 後兩處文字就成立；歷史日誌不要改，補一條更正即可）。

## 7. 沒做 / 限制

- 沒有重開 Revit 實機（本件只動測試字串與文件，不需要）。
- 沒有檢查「ADR D-015（Revit 跳轉）」改名後的所有舊連結是否都更新，只驗了 `SPEC.md`、`CHANGELOG.md`、`REVIT_LINK.md`、ADR 標題；QA 的連結檢查（本機 markdown link rot）PASS。
- 驗收者環境：Windows、pwsh 7、Chrome 157、dotnet 與 node 同建造者。
