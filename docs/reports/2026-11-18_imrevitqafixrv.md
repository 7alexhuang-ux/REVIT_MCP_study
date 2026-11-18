# imrevitqafixrv：31a3a09 獨立複驗

日期：2026-11-18。驗收者：Codex（派工指定 gpt-6.1-sol／medium），與建造者 7.alex Claude 不同 harness。被驗提交：`31a3a098de803323a270097da150456f945500f6`；驗收分支：`imrevitqafixrv`。只新增本報告，沒有修改建造者程式或文件。

結論：**PASS-with-issues：0 個阻擋、1 個非阻擋（本報告 N1，既有來源標示小瑕疵）**。原 B1 已修，完整 QA/QC 為 **74 PASS／0 FAIL／3 WARN／3 SKIP**；390×844 的三顆導覽按鈕都不折行，補齊建造者標「缺」的證據。

## 檢查清單 1～6

| # | 結果 | 實查證據 |
|---|---|---|
| 1 | PASS | 起手 `git status --short` 空白、分支 `imrevitqafixrv`、HEAD `31a3a09`。`git show 31a3a09 --stat` 僅 6 檔、48 增／18 刪：CLAUDE.md、shared.js、decisions log、兩份修復報告、log/2026-11.md；未動 `MCP/`、`MCP-Server/src`。 |
| 2 | PASS | `git grep -n -i -E 'Users[\\/]+(alex|other)[\\/]' -- ':!log/*'` 無命中（exit 1）；完整 QA `3-4` 通過。原報告第 16 行已用文字描述與占位名稱，不再抄回帳號路徑。 |
| 3 | PASS | pwsh 7，先設定 `$OutputEncoding=[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false)`，再跑 `./scripts/verify-qaqc.ps1 -SkipBuild -SkipDeploy`；74／0／3／3、exit 0。報告加入追蹤後再跑結果見下方收尾。 |
| 4 | PASS | Python Playwright／Chromium 143.0.7499.4，真正 viewport 桌機 1280×800、手機 390×844，`innerWidth/innerHeight` 實讀一致。手機三顆按鈕各一行；兩種尺寸皆無水平溢出、無 pageerror；截圖已開圖核對。量測見下表。 |
| 5 | PASS | decisions log 已無 `BACKLOG.*第\s*\d+\s*行`；時間戳對到 BACKLOG 21:33／21:46 紀錄。hld070 出處對到結果檔所指的 Alex_Diary hld048 樹之報告及 wait_alex.md「十五」70-1～70-4；Revit 2024 描述對到 imrevitaddinjump_result.txt「沒有實際開 2024 測」及 repo 原整合報告 §5。無簽章句的來源仍有一處小瑕疵，列本報告 N1。 |
| 6 | PASS | CLAUDE.md:500 明寫 `Release.R25` 與 `Release.R26` `each emit 9`，並記 R26 於 2026-11-18 重數；語意清楚。既有 imrevitqafixv 驗收樹的 R26 輸出實數為 9 DLL，含 System.Drawing.Common。本次未重編 C#，不將他樹既有輸出當成本次建置證據。 |

## 前輪 B1、N1～N6 核對

以下編號沿用前輪，與本報告的非阻擋 N1 分開閱讀。

| 前輪項目 | 結果 | 證據／界線 |
|---|---|---|
| B1 報告抄回帳號路徑 | PASS | 原報告改為文字說明；提交後的 31a3a09 重跑 `3-4` 與整套 QA 均綠。 |
| N1 R26 DLL 數 | PASS | CLAUDE.md 已改各 9，未再聲稱 R26 未重數；未變成固定計數的驗證規則。 |
| N2 BACKLOG 行號漂移 | PASS | 改以時間戳與原文關鍵字定位；搜尋舊式引用無命中。 |
| N3 出處 | PASS（主要更正） | hld070 已說明 BACKLOG 只列編號、細節另有來源；R24 未實測已註出處。無簽章句仍未明列直接出處，見本報告 N1，非阻擋。 |
| N4 手機折行 | PASS | 390×844 的「安裝」「📝 未開放報名」「淺色」各一行，舊版對照各兩行，見量測。 |
| N5 測試目標 | PASS | 重修報告已明記兩次都跑 net48，R25 .NET 8 未被測；`MCP.Tests/RevitMCP.Tests.csproj:3` 實為 `TargetFramework=net48`。本次不重跑 .NET 測試，也不宣稱有 .NET 8 執行期覆蓋。 |
| N6 舊全綠說法 | PASS | 原建造者報告追加更正、log/2026-11.md 23:00 段追加更正，明說 216f268 時 3-4 紅；沒有改寫歷史結論。 |

## 瀏覽器量測與基底對照

服務本 worktree 的 `docs/BIM_MCP/`，綁 `127.0.0.1:5189`；事先查 ports.json 與空埠並登錄。使用乾淨 browser context，等 networkidle、字型及頁面初始化後量測。逐字 Range 取文字的 y 座標計算行數，不只看元素總高度。

| 量測 | 桌機目前 | 手機目前 | 桌機基底對照 | 手機基底對照 |
|---|---:|---:|---:|---:|
| innerWidth × innerHeight | 1280×800 | 390×844 | 1280×800 | 390×844 |
| scrollWidth / clientWidth | 1280 / 1280 | 390 / 390 | 1280 / 1280 | 390 / 390 |
| documentElement.scrollHeight | 6433 | 11075 | 6433 | 11075 |
| body.scrollHeight | 6433 | 11075 | 6433 | 11075 |
| 導覽列高度 px | 61.39 | 57.39 | 61.39 | 77.78 |
| 安裝／報名／淺色的文字行數 | 1／1／1 | 1／1／1 | 1／1／1 | 2／2／2 |
| pageerror 數 | 0 | 0 | 0 | 0 |

手機目前三顆寬度分別為 50、105.17、50px，高度皆 32.39px。頂端與頁尾共兩個 `[data-meetup-link]`，文字均為「📝 未開放報名」、`href=null`、`aria-disabled=true`、含 `is-disabled`；effective config 為 `{url:"",eventDate:""}`。

基底對照方式：以 `git show 216f268:docs/BIM_MCP/shared.js` 原文攔截同名腳本回應，其餘仍由本樹供應；`git diff 216f268 31a3a09 -- docs/BIM_MCP/index.html docs/BIM_MCP/nav.css docs/BIM_MCP/styles.css` 為空。這是瀏覽器對照重播，沒有切換／重設分支，也沒有宣稱重跑整個基底 QA。

整頁高度大於視窗，**未通過「整頁無垂直捲動」判準**，不把它寫成通過：首頁有多節教學、reference、歷次活動與頁尾，兩個版本各尺寸高度完全相同；本次沒有新增多餘的垂直捲動。這是既有長頁設計，與此次導覽按鈕折行修正分開記錄；不以壓掉內容來湊高度。

截圖存使用者家目錄（`%USERPROFILE%`），均已視覺核對：

- `imrevitqafixrv_desktop_current.png`、`imrevitqafixrv_mobile_current.png`：實際 viewport。
- `imrevitqafixrv_desktop_current_nav.png`、`imrevitqafixrv_mobile_current_nav.png`：頂端 160px。
- `imrevitqafixrv_desktop_baseline.png`、`imrevitqafixrv_mobile_baseline.png`，以及各自 `_nav.png`：舊版腳本對照。

原始量測：`C:/Temp/grokbot_sessions/imrevitqafixrv_web_metrics.json`；重現腳本：同目錄 `imrevitqafixrv_browser.py`。核心數據已完整寫入本 repo 報告，不以 Temp 作為唯一交付。

## 阻擋

無。QA/QC FAIL=0，前輪 B1 已消除。

## 非阻擋

**N1：decisions log 的無簽章句仍少列直接出處（前輪 N3 的殘留）。** `docs/decisions/alex-decisions-log.md` §D 標「原文照抄」、以 BACKLOG 21:33 作為整段來源，但 BACKLOG 只寫「外掛 Q2『永遠載入』」，沒有「無簽章／無法驗證發佈者」那些描述。描述本身有據：`docs/reports/2026-11-18_imrevitaddinjump.md` §7 第 2 點明記沒有簽章、每次啟動詢問。本項建議只補該直接來源，毋須改決策內容；`git show 216f268:docs/decisions/alex-decisions-log.md` §D 同樣已有這個來源落差，31a3a09 沒有新增它。驗收者按授權只記問題，不改文件。

## 驗證環境、限制與收尾

- 初始本樹缺 node_modules／build，QA 第一次於 Phase 7 因缺 runtime registry 中止，不能算通過。沒有執行 npm ci、沒有複製 repo。
- 在 gitignore 的 `MCP-Server/node_modules` 建 junction 共用主樹既有依賴；tsc 成功。標準 npm build 的 Apps 步驟因共用依賴缺 esbuild 而失敗；之後以 Temp 版 build-apps 載入建造者現有 esbuild，並給它建造者 node_modules 的解析路徑，對本樹 app.ts/template.html 成功產生 clash-viewer HTML（303.7 KiB JS）。Repo build script 未修改。這是環境補足，並非完整標準 npm build 成功的宣稱。
- 補足後完整 QA 74 PASS／0 FAIL／3 WARN／3 SKIP。3 WARN 是既有 check_sanitary_fixture_requirements 隔離與兩個 rollback-only 探針；3 SKIP 是明示略過 C# 建置、MCP Server 建置與部署。未做 Revit 實機、C# 建置、正式站／資料庫／部署。
- HTTP server 在量測腳本 finally 內 shutdown／server_close，僅結束自己的服務，PID 15912；5189 已釋放、ports.json 自己的項目標 released=true；沒有動占用 5187 的程序。沒有 taskkill node.exe。
- 本次無程式實作，無需新增 spec commit／D 號。既有 Revit 跳轉規格提交為 `9f1282f`、D-015；本次僅提交這份驗收報告，不修改規格／log，不 push、merge、rebase、reset、stash 或使用 --no-verify。
- 加入追蹤後完整 QA 再跑：74 PASS／0 FAIL／3 WARN／3 SKIP、exit 0（pwsh 7.7.0）；原始輸出 `C:/Temp/grokbot_sessions/imrevitqafixrv_qaqc_staged.txt`。暫存區僅本報告，`git diff --cached --check` 無錯。
