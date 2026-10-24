# 提交交班單 — 2026-10-25

> 性質：人類與 AI 共用交班紀錄（繁中）。範圍：分支 `grading-workflow-v2` 主工作樹 140 筆未提交改動的整理與提交。只 commit，未 push、未動遠端、未 reset／checkout／stash／clean，未安裝外掛、未啟動或操作 Revit。

## 1. Commit 清單（依提交順序，HEAD 起點為 `742efaf`）

| # | Commit | 內容 | 檔數 |
|---|---|---|---:|
| 1 | `ed66d43` | 設計選項「無幾何」判定：`DesignOptionNoGeometryPolicy`、`CommandExecutor.DesignOptions.cs`、NUnit、WEB_COMPARE_EXPORT、CHANGELOG、log/2026-10 | 7 |
| 2 | `e83c121` | 整地工作流後續批次（既有 staged）：坵塊法／限建核對、樓板設計副本、Ribbon 出圖契約、整地 domain 與 skill、設計檔、log/2026-07、CHANGELOG | 70 |
| 3 | `2eabdf3` | 走動展示交付包批次（既有 staged）：Walkthrough 指令、MCP-Server 自動分批、viewer、深度渲染 bridge、install-addon、domain、log/2026-09 | 26 |
| 4 | `186a647` | 指令分派與工具註冊補齊（讓 `742efaf` 起的新指令不再是死碼）＋ socket 通訊層強化＋ Application 自動啟動 | 12 |
| 5 | `766f6b0` | 文件同步：工具數 137／domain 52／skill 24、ROADMAP、docs/ADR.md、BIM_MCP 參考頁、verify-qaqc、.gitignore | 20 |
| 6 | （本檔所在 commit） | 本交班單 | 1 |

分群逐檔清單：`C:\Temp\grokbot_sessions\revitcommit_groups.txt`（G0～G5）。每個 commit 都以 `git commit -F <訊息檔> -- <明確路徑>` 提交，沒有用 `git add -A`／`git add .`。

## 2. 建置與測試數字（整批工作樹，提交前跑；commit 5、6 只動文件）

| 項目 | 結果 |
|---|---|
| `dotnet build -c Release.R24 MCP/RevitMCP.csproj` | 0 錯誤、1184 警告（主要為 CS8600、CS8604、CS8618、CS0618、CS8603 等 nullable／過時 API 類；未逐條比對是否皆為既有） |
| `dotnet test MCP.Tests` | 314 通過、0 失敗、0 略過（與先前數字一致） |
| MCP-Server `npm test`（含 `tsc` 型別檢查） | `build/*.test.js` 與 `build/tools/*.test.js` 38 通過；`tools/*.test.mjs` 10 通過；0 失敗 |
| `viewer/walkthrough/pathmath.test.cjs` | 20 通過（不在 `npm test` 內） |
| `scripts/verify-qaqc.ps1 -SkipBuild -SkipDeploy` | 54 PASS、0 FAIL、0 WARN、3 SKIP |
| 中間 commit 各自建置（暫時 worktree，已移除）：`ed66d43`、`e83c121`、`2eabdf3` | 皆 0 錯誤；`186a647`、`766f6b0` 同最終工作樹 |

沒有既有失敗需要記錄，也沒有為過關而改測試。

## 3. 提交後仍未提交的檔案與理由

`git status` 只剩 9 個已被 git 追蹤、但屬於建置產物的檔，**刻意不提交**：

- `MCP/bin/Release/RevitMCP.dll`、`MCP/bin/Release/RevitMCP.pdb`
- `MCP/obj/Release/` 下 6 個檔（`*.editorconfig`、`*.assets.cache`、`*.AssemblyReference.cache`、`*.CoreCompileInputs.cache`、`FileListAbsolute.txt`、`sourcelink.json`）
- `MCP/obj/RevitMCP.csproj.nuget.g.props`

原因：`.gitignore` 已有 `MCP/bin/`，但這些檔在規則加入前就被追蹤，所以仍顯示為已修改。需要 Alex 決定是否 `git rm --cached` 取消追蹤（我沒有做，會改動歷史追蹤狀態）。

另：未追蹤的 `tmp/`（內含 `tmp/pdfs`，約 25 MB 本機 PDF 暫存）已加進 `.gitignore`（`/tmp/`），檔案未刪。

## 4. 風險

1. **中間 commit 的指令尚未接上分派**：commit 2、3 的新指令要到 commit 4 才註冊（但各 commit 皆可建置）。若有人只 cherry-pick 前者，指令不會生效。
2. **混合檔整檔提交**：`CommandExecutor.cs`、`SocketService.cs`、`Application.cs`、`socket.ts`、`tools/index.ts`、`package.json` 原本是「他人已 staged ＋ 我們的工作樹改動」兩層，不丟棄任一層就無法拆開，所以整檔放進 commit 4（訊息已註明）。
3. **行尾**：`CommandExecutor.cs` 既有 staged 版本含整檔行尾變更（git 顯示約 4100 行增刪，忽略行尾後約 60 行）；`git diff` 會很大，審查請加 `--ignore-cr-at-eol`。多個檔 git 提示「LF 將被換成 CRLF」。
4. **既有 staged 批次的 CHANGELOG 是我依檔案內容與 `log/2026-07.md` 歸納**（原作者未留），段落已標明「提交者未逐項重跑實機」；細節可能與原意有出入。
5. **全部未經實機驗證**：未連 Revit，設計選項重試／容器規則、自動啟動 marker、ExternalEventManager 佇列、連線接手行為都待實機。
6. **分支分歧未處理**：實測 `git rev-list --left-right --count main...HEAD` 為落後 230、領先 51（CHANGELOG 舊記載 192／42）。見 ROADMAP R-01。
7. `MCP-Server/scratch/*.js` 等檔被 `.gitignore` 列為忽略但仍被追蹤（`git ls-files -ci` 可見），本次未處理。

## 5. 待 Alex 決定

1. 是否把 `MCP/bin/Release/`、`MCP/obj/Release/`、`MCP/bin/Release.2024/`、`MCP/obj/RevitMCP.csproj.nuget.g.props` 從追蹤移除（`git rm --cached`），避免每次建置都出現已修改。
2. 是否 push 分支，以及何時處理與 `main` 的分歧（R-01）。
3. commit 4 的混合檔是否需要改成更細的拆分（需要用 `git reset` 重做索引，我依指示未做）。
4. 實機驗證順序：先部署後驗設計選項無幾何判定，還是先驗自動啟動／佇列。
