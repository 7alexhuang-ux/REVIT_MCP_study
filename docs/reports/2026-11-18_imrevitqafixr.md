# 報告：imrevitqafixv 驗收問題重修（imrevitqafixr）

> 日期：2026-11-18。建造者：Claude Sonnet 5.5（7.alex 帳號，effort low）。分支 `imrevitqafix`，基底 `216f268`。狀態：待審核（Codex 獨立複驗 imrevitqafixrv）。

## 1. 逐項處理

| 項目 | 處理 | 證據 |
|---|---|---|
| B1（阻擋） | `docs/reports/2026-11-18_imrevitqafix.md` 第 16 行（原第 14 行）改成文字描述，不再寫帳號資料夾名；全 repo 再掃（排除 node_modules、.git、log）已無該字串 | 掃描無輸出；QA `3-4` PASS |
| N1 | `CLAUDE.md` 改成「R25 與 R26 各 9」，註明 2026-11-18 已重新計數（R26 實數 9 為驗收者實查，本次未重編） | CLAUDE.md:500 |
| N2 | `docs/decisions/alex-decisions-log.md` 的 BACKLOG 行號全改成「時間戳那一筆」定位 | grep 無「第 N 行」 |
| N3 | hld070 說明改為「BACKLOG 只列編號；細節在 Alex_Diary hld070 報告與 wait_alex.md 十五」；Revit 2024 未實測描述註明出處是 imrevitaddinjump_result.txt | 同檔第 36、46 行 |
| N4 | `docs/BIM_MCP/shared.js` 停用文字由「目前未開放報名」縮成「未開放報名」（註解同步） | 頁面 DOM 兩個 `[data-meetup-link]` 都顯示「📝 未開放報名」、無 href、aria-disabled=true |
| N5 | 只記錄：R24、R25 測試輸出都是 `RevitMCP.Tests.dll (net48)`，同一個 net48 測試目標跑兩次；R25（.NET 8）組件未被測。未改測試架構 | 見驗收報告 N5 |
| N6 | 建造者報告與 `log/2026-11.md` 補更正：216f268 時 QA/QC 並非全綠，B1 修後才全綠；歷史段落不改 | 建造者報告第 8 行下方；log 末段 |

## 2. 驗證

- `pwsh 7`＋UTF-8 跑 `verify-qaqc.ps1 -SkipBuild -SkipDeploy`（git add 前）：PASS 74／FAIL 0／WARN 4／SKIP 3。git add 後再跑一次：PASS 74／FAIL 0／WARN 3／SKIP 3。
- 手機版截圖限制：本機 headless Chrome 最小視窗寬度大於 390，截圖 `imrevitqafixr_mobile.png`（存使用者家目錄）無法嚴格呈現 390px；未量到 scrollHeight，**N4 的折行是否消除未能在真正 390px 驗證，缺**。文字由 8 字縮成 6 字，預期較不易折行，請複驗者用 DevTools 裝置模擬 390 確認。桌機 1280×800 截圖 `imrevitqafixr_desktop.png` 已存。
- 預覽伺服器 127.0.0.1:5187（ports.json 範圍 5170～5199 內），用完已停自己的 pid。
